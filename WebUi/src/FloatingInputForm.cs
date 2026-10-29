using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Automation;
using System.Windows.Forms;

namespace RichTextGen
{
    /// <summary>
    /// 系统级浮动输入面板：全局鼠标钩子看点击位置，UI Automation 判断是不是输入框，
    /// 是的话就在光标旁弹出一个极简小面板：输入文字、选中后套颜色 / 上下标 / 循环渐变，
    /// 点「回填」把带标签的文字直接粘贴回原来的输入框。
    /// </summary>
    public sealed class FloatingInputForm : Form
    {
        // ---------------- Win32 ----------------
        private const int WH_MOUSE_LL = 14;
        private const int WM_LBUTTONUP = 0x0202;
        private const int WM_NCLBUTTONDOWN = 0xA1;
        private const int HTCAPTION = 2;
        private const uint GA_ROOT = 2;

        private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int X; public int Y; }

        [StructLayout(LayoutKind.Sequential)]
        private struct MSLLHOOKSTRUCT
        {
            public POINT pt;
            public uint mouseData;
            public uint flags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);
        [DllImport("user32.dll")]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);
        [DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
        [DllImport("kernel32.dll")]
        private static extern IntPtr GetModuleHandle(string lpModuleName);
        [DllImport("user32.dll")]
        private static extern IntPtr WindowFromPoint(POINT p);
        [DllImport("user32.dll")]
        private static extern IntPtr GetAncestor(IntPtr hwnd, uint gaFlags);
        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")]
        private static extern bool ReleaseCapture();
        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")]
        private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, IntPtr dwExtraInfo);

        // ---------------- 静态状态（全局只挂一个钩子 / 一个面板） ----------------
        private static FloatingInputForm _inst;
        private static bool _enabled;
        private static IntPtr _hook;
        private static LowLevelMouseProc _proc;          // 必须保活，否则回调被回收
        private static System.Windows.Forms.Timer _clickTimer;
        private static Point _clickPt;
        private static int _clickSeq;

        private AutomationElement _target;
        private TextBox _txt;
        private Label _hint;
        private Button _colorBtn;
        private PaletteForm _palette;
        private readonly float _s;

        // ============================================================ 开关
        public static bool IsEnabled { get { return _enabled; } }

        public static void SetEnabled(bool on)
        {
            if (on == _enabled) return;
            if (on)
            {
                if (_inst == null || _inst.IsDisposed) _inst = new FloatingInputForm();
                IntPtr unused = _inst.Handle;            // 提前建句柄，后台线程才能 BeginInvoke
                _proc = HookProc;
                _hook = SetWindowsHookEx(WH_MOUSE_LL, _proc, GetModuleHandle(null), 0);
                _enabled = _hook != IntPtr.Zero;
                if (!_enabled) _proc = null;
            }
            else
            {
                if (_hook != IntPtr.Zero) { UnhookWindowsHookEx(_hook); _hook = IntPtr.Zero; }
                _enabled = false;
                _proc = null;
                if (_clickTimer != null) { _clickTimer.Stop(); }
                HidePanelStatic();
            }
        }

        public static void Shutdown()
        {
            SetEnabled(false);
            if (_clickTimer != null) { try { _clickTimer.Dispose(); } catch { } _clickTimer = null; }
            if (_inst != null) { try { _inst.Dispose(); } catch { } _inst = null; }
        }

        public static void HidePanelStatic()
        {
            if (_inst != null && !_inst.IsDisposed) _inst.HidePanel();
        }

        // ============================================================ 全局鼠标钩子
        private static IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
        {
            try
            {
                if (nCode >= 0 && (int)wParam == WM_LBUTTONUP)
                {
                    MSLLHOOKSTRUCT info = (MSLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(MSLLHOOKSTRUCT));
                    Point pt = new Point(info.pt.X, info.pt.Y);
                    if (!IsOwnWindow(pt))
                    {
                        _clickPt = pt;
                        _clickSeq++;
                        EnsureTimer();
                        _clickTimer.Stop();
                        _clickTimer.Start();             // 90ms 后再判断，等目标窗口完成聚焦
                    }
                }
            }
            catch { }
            return CallNextHookEx(_hook, nCode, wParam, lParam);
        }

        private static void EnsureTimer()
        {
            if (_clickTimer != null) return;
            _clickTimer = new System.Windows.Forms.Timer();
            _clickTimer.Interval = 90;
            _clickTimer.Tick += delegate
            {
                _clickTimer.Stop();
                OnClickSettled(_clickPt, _clickSeq);
            };
        }

        /// <summary>点在自己窗口上（面板 / 调色盘 / 主程序）不处理，避免自己弹自己。</summary>
        private static bool IsOwnWindow(Point pt)
        {
            try
            {
                IntPtr h = WindowFromPoint(new POINT { X = pt.X, Y = pt.Y });
                if (h == IntPtr.Zero) return false;
                IntPtr root = GetAncestor(h, GA_ROOT);
                uint pid;
                GetWindowThreadProcessId(root, out pid);
                return pid == (uint)System.Diagnostics.Process.GetCurrentProcess().Id;
            }
            catch { return false; }
        }

        private static void OnClickSettled(Point pt, int seq)
        {
            if (!_enabled || _inst == null || _inst.IsDisposed) return;
            ThreadPool.QueueUserWorkItem(delegate
            {
                AutomationElement target = null;
                try { target = FindEditable(pt); } catch { }
                try
                {
                    if (!_enabled || _inst == null || _inst.IsDisposed) return;
                    if (_clickSeq != seq) return;        // 又点了别处，这次作废
                    _inst.BeginInvoke((MethodInvoker)delegate
                    {
                        try
                        {
                            if (!_enabled || _inst == null || _inst.IsDisposed) return;
                            if (_clickSeq != seq) return;
                            if (target == null)
                            {
                                if (_inst.Visible) _inst.HidePanel();
                                return;
                            }
                            _inst.ShowFor(target, pt);
                        }
                        catch { }
                    });
                }
                catch { }
            });
        }

        /// <summary>从点击点向上找 4 层，命中 Edit / Document / ComboBox 即认为点了可输入区域。</summary>
        private static AutomationElement FindEditable(Point pt)
        {
            try
            {
                AutomationElement e = AutomationElement.FromPoint(new System.Windows.Point(pt.X, pt.Y));
                for (int i = 0; e != null && i < 4; i++)
                {
                    ControlType t = null;
                    try { t = e.GetCurrentPropertyValue(AutomationElement.ControlTypeProperty) as ControlType; } catch { }
                    if (t == ControlType.Edit || t == ControlType.Document || t == ControlType.ComboBox)
                    {
                        try
                        {
                            if (e.Current.IsEnabled && e.Current.IsKeyboardFocusable) return e;
                        }
                        catch { }
                    }
                    try { e = TreeWalker.ControlViewWalker.GetParent(e); }
                    catch { e = null; }
                }
            }
            catch { }
            return null;
        }

        // ============================================================ 面板本体
        private FloatingInputForm()
        {
            int dpi = 96;
            try { dpi = DeviceDpi; } catch { }
            _s = Math.Max(0.75f, Math.Min(2.5f, dpi / 96f));

            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            AutoScaleMode = AutoScaleMode.None;
            BackColor = Color.FromArgb(250, 250, 250);
            Font = new Font("Microsoft YaHei UI", 9f);
            ClientSize = new Size(S(420), S(152));
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
            Paint += delegate (object s, PaintEventArgs e)
            {
                using (Pen p = new Pen(Color.FromArgb(196, 200, 206)))
                    e.Graphics.DrawRectangle(p, 0, 0, Width - 1, Height - 1);
            };
            BuildUi();
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        private int S(int v) { return (int)Math.Round(v * _s); }

        private void BuildUi()
        {
            int W = ClientSize.Width;

            // ---- 标题条（可拖） ----
            Panel bar = new Panel();
            bar.Bounds = new Rectangle(0, 0, W, S(22));
            bar.BackColor = Color.FromArgb(240, 241, 243);
            bar.Cursor = Cursors.SizeAll;
            bar.MouseDown += DragPanel;
            Controls.Add(bar);

            Label title = new Label();
            title.Text = "浮动输入 · 选中文字可套颜色 / 格式";
            title.AutoSize = false;
            title.Bounds = new Rectangle(S(8), 0, W - S(36), S(22));
            title.TextAlign = ContentAlignment.MiddleLeft;
            title.ForeColor = Color.FromArgb(90, 96, 104);
            title.Font = new Font("Microsoft YaHei UI", 8f);
            title.Cursor = Cursors.SizeAll;
            title.MouseDown += DragPanel;
            bar.Controls.Add(title);

            Label close = new Label();
            close.Text = "✕";
            close.AutoSize = false;
            close.Bounds = new Rectangle(W - S(26), 0, S(24), S(22));
            close.TextAlign = ContentAlignment.MiddleCenter;
            close.ForeColor = Color.FromArgb(110, 116, 124);
            close.Cursor = Cursors.Hand;
            close.Click += delegate { HidePanel(); };
            bar.Controls.Add(close);

            // ---- 输入区 ----
            _txt = new TextBox();
            _txt.Multiline = true;
            _txt.AcceptsReturn = true;
            _txt.BorderStyle = BorderStyle.FixedSingle;
            _txt.ScrollBars = ScrollBars.Vertical;
            _txt.Bounds = new Rectangle(S(8), S(26), W - S(16), S(72));
            _txt.BackColor = Color.White;
            _txt.Font = new Font("Microsoft YaHei UI", 9.5f);
            _txt.KeyDown += delegate (object s, KeyEventArgs e)
            {
                if (e.Control && e.KeyCode == Keys.Enter) { CommitToTarget(); e.SuppressKeyPress = true; }
                else if (e.KeyCode == Keys.Escape) { HidePanel(); e.SuppressKeyPress = true; }
            };
            Controls.Add(_txt);

            // ---- 工具条 ----
            int y = S(102);
            int h = S(24);

            _colorBtn = new Button();
            _colorBtn.Text = "颜色";
            _colorBtn.Font = new Font("Microsoft YaHei UI", 8f);
            _colorBtn.FlatStyle = FlatStyle.Flat;
            _colorBtn.FlatAppearance.BorderColor = Color.FromArgb(200, 204, 210);
            _colorBtn.BackColor = Color.White;
            _colorBtn.Bounds = new Rectangle(S(8), y, S(48), h);
            _colorBtn.Click += delegate { OpenPalette(); };
            Controls.Add(_colorBtn);

            string[][] wraps = new string[][]
            {
                new string[] { "B", "<b>", "</b>", "粗体 <b>" },
                new string[] { "I", "<i>", "</i>", "斜体 <i>" },
                new string[] { "U", "<u>", "</u>", "下划线 <u>" },
                new string[] { "S", "<s>", "</s>", "删除线 <s>" },
                new string[] { "上", "<sup>", "</sup>", "上标 <sup>" },
                new string[] { "下", "<sub>", "</sub>", "下标 <sub>" },
            };
            int x = S(62);
            foreach (string[] w in wraps)
            {
                Button b = new Button();
                b.Text = w[0];
                b.Font = new Font("Microsoft YaHei UI", 8f);
                b.FlatStyle = FlatStyle.Flat;
                b.FlatAppearance.BorderColor = Color.FromArgb(200, 204, 210);
                b.BackColor = Color.White;
                b.Bounds = new Rectangle(x, y, S(26), h);
                b.Tag = new string[] { w[1], w[2] };
                b.Click += delegate (object s, EventArgs e)
                {
                    string[] tag = (string[])((Button)s).Tag;
                    ApplyWrap(tag[0], tag[1]);
                };
                Controls.Add(b);
                x += S(28);
            }

            Button loop = new Button();
            loop.Text = "循环";
            loop.Font = new Font("Microsoft YaHei UI", 8f);
            loop.FlatStyle = FlatStyle.Flat;
            loop.FlatAppearance.BorderColor = Color.FromArgb(200, 204, 210);
            loop.BackColor = Color.White;
            loop.Bounds = new Rectangle(x, y, S(44), h);
            loop.Click += delegate { ApplyLoopGradient(); };
            Controls.Add(loop);

            Button commit = new Button();
            commit.Text = "回填";
            commit.Font = new Font("Microsoft YaHei UI", 8f);
            commit.FlatStyle = FlatStyle.Flat;
            commit.FlatAppearance.BorderColor = Color.FromArgb(200, 204, 210);
            commit.BackColor = Color.FromArgb(232, 242, 255);
            commit.Bounds = new Rectangle(W - S(112), y, S(58), h);
            commit.Click += delegate { CommitToTarget(); };
            Controls.Add(commit);

            Button copy = new Button();
            copy.Text = "复制";
            copy.Font = new Font("Microsoft YaHei UI", 8f);
            copy.FlatStyle = FlatStyle.Flat;
            copy.FlatAppearance.BorderColor = Color.FromArgb(200, 204, 210);
            copy.BackColor = Color.White;
            copy.Bounds = new Rectangle(W - S(50), y, S(44), h);
            copy.Click += delegate
            {
                if (_txt.TextLength > 0 && SetClipboard(_txt.Text, 4)) _hint.Text = "已复制到剪贴板";
                _txt.Focus();
            };
            Controls.Add(copy);

            // ---- 提示行 ----
            _hint = new Label();
            _hint.Text = "Ctrl+Enter 回填 · Esc 关闭 · 不选文字时颜色作用于整段";
            _hint.AutoSize = false;
            _hint.Bounds = new Rectangle(S(8), S(130), W - S(16), S(16));
            _hint.TextAlign = ContentAlignment.MiddleLeft;
            _hint.ForeColor = Color.FromArgb(130, 136, 144);
            _hint.Font = new Font("Microsoft YaHei UI", 7.5f);
            Controls.Add(_hint);
        }

        private void DragPanel(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            ReleaseCapture();
            SendMessage(Handle, WM_NCLBUTTONDOWN, (IntPtr)HTCAPTION, IntPtr.Zero);
        }

        // ============================================================ 显示 / 隐藏
        internal void ShowFor(AutomationElement target, Point pt)
        {
            _target = target;
            if (_palette != null && _palette.Visible) _palette.Hide();
            if (!Visible) Show();
            PlaceAt(pt);
            if (_hint.Text.Length == 0) _hint.Text = "Ctrl+Enter 回填 · Esc 关闭";
        }

        private void PlaceAt(Point pt)
        {
            Rectangle wa = Screen.FromPoint(pt).WorkingArea;
            int x = pt.X + S(14);
            int y = pt.Y + S(20);
            if (x + Width > wa.Right) x = pt.X - Width - S(14);
            if (y + Height > wa.Bottom) y = pt.Y - Height - S(12);
            x = Math.Max(wa.Left + S(4), Math.Min(x, wa.Right - Width - S(4)));
            y = Math.Max(wa.Top + S(4), Math.Min(y, wa.Bottom - Height - S(4)));
            Location = new Point(x, y);
        }

        internal void HidePanel()
        {
            if (_palette != null) { try { _palette.Hide(); } catch { } }
            if (Visible) Hide();
        }

        // ============================================================ 编辑操作
        private void ApplyWrap(string open, string close)
        {
            int s = _txt.SelectionStart;
            int n = _txt.SelectionLength;
            string all = _txt.Text;
            string sel = all.Substring(s, n);
            string rep = open + sel + close;
            _txt.Text = all.Substring(0, s) + rep + all.Substring(s + n);
            _txt.SelectionStart = s + open.Length;
            _txt.SelectionLength = n;
            _txt.Focus();
        }

        private void ApplyColor(Color c)
        {
            int s = _txt.SelectionStart;
            int n = _txt.SelectionLength;
            if (n == 0) { s = 0; n = _txt.TextLength; }
            if (n == 0) { _hint.Text = "先输入文字，再选颜色"; return; }
            string all = _txt.Text;
            string sel = all.Substring(s, n);
            string hex = "#" + c.R.ToString("X2") + c.G.ToString("X2") + c.B.ToString("X2");
            string rep = "<color=" + hex + ">" + sel + "</color>";
            _txt.Text = all.Substring(0, s) + rep + all.Substring(s + n);
            _txt.SelectionStart = s + rep.Length;
            _txt.SelectionLength = 0;
            _hint.Text = "已套用 " + hex;
            _txt.Focus();
        }

        /// <summary>逐字符上色，5 个字符一个周期往复（与主界面的「循环渐变」一致）。</summary>
        private void ApplyLoopGradient()
        {
            string all = _txt.Text;
            int s = _txt.SelectionStart;
            int n = _txt.SelectionLength;
            if (n == 0) { s = 0; n = all.Length; }
            if (n == 0) { _hint.Text = "先输入文字，再点循环"; return; }

            string sel = all.Substring(s, n);
            StringBuilder sb = new StringBuilder();
            int k = 0;
            for (int i = 0; i < sel.Length; i++)
            {
                char ch = sel[i];
                if (ch == '\n' || ch == '\r') { sb.Append(ch); continue; }
                int step = i;
                string piece = ch.ToString();
                if (char.IsHighSurrogate(ch) && i + 1 < sel.Length && char.IsLowSurrogate(sel[i + 1]))
                {
                    piece = sel.Substring(i, 2);
                    i++;
                }
                double t = (step % 5) / 4.0;
                int r = (int)Math.Round(255 + (0 - 255) * t);
                int g = 0;
                int b = (int)Math.Round(0 + (255 - 0) * t);
                sb.Append("<color=#")
                  .Append(r.ToString("X2")).Append(g.ToString("X2")).Append(b.ToString("X2"))
                  .Append('>').Append(piece).Append("</color>");
                k++;
            }
            string rep = sb.ToString();
            _txt.Text = all.Substring(0, s) + rep + all.Substring(s + n);
            _txt.SelectionStart = s + rep.Length;
            _txt.SelectionLength = 0;
            _hint.Text = "已套用循环渐变（红→蓝，5 字符一周期）";
            _txt.Focus();
        }

        private void OpenPalette()
        {
            if (_palette == null || _palette.IsDisposed)
            {
                _palette = new PaletteForm(_s);
                _palette.Picked += delegate (Color c)
                {
                    ApplyColor(c);
                    _palette.Hide();
                    _txt.Focus();
                };
            }
            Point p = PointToScreen(new Point(_colorBtn.Left, _colorBtn.Bottom + S(4)));
            _palette.ShowAt(p);
        }

        // ============================================================ 回填
        private void CommitToTarget()
        {
            string text = _txt.Text;
            if (text.Length == 0) { HidePanel(); return; }

            AutomationElement target = _target;
            HidePanel();
            try
            {
                if (target != null)
                {
                    IntPtr root = IntPtr.Zero;
                    try
                    {
                        int h = target.Current.NativeWindowHandle;
                        if (h != 0) root = GetAncestor((IntPtr)h, GA_ROOT);
                    }
                    catch { }
                    if (root != IntPtr.Zero) SetForegroundWindow(root);
                    try { target.SetFocus(); } catch { }
                }
                Thread.Sleep(90);
                if (!SetClipboard(text, 6))
                {
                    Show();
                    _hint.Text = "剪贴板被占用，请稍后重试（文字已保留）";
                    return;
                }
                Thread.Sleep(40);
                SendCtrlV();
                _txt.Clear();
                _hint.Text = "已回填到原输入框";
            }
            catch
            {
                Show();
            }
        }

        private static bool SetClipboard(string s, int tries)
        {
            for (int i = 0; i < tries; i++)
            {
                try { Clipboard.SetText(s); return true; }
                catch { Thread.Sleep(50); }
            }
            return false;
        }

        private static void SendCtrlV()
        {
            keybd_event(0x11, 0, 0, IntPtr.Zero);   // Ctrl ↓
            keybd_event(0x56, 0, 0, IntPtr.Zero);   // V ↓
            keybd_event(0x56, 0, 2, IntPtr.Zero);   // V ↑
            keybd_event(0x11, 0, 2, IntPtr.Zero);   // Ctrl ↑
        }

        // ============================================================ 小调色盘
        private sealed class PaletteForm : Form
        {
            public event Action<Color> Picked;
            private readonly float _s;

            private static readonly string[] Hex =
            {
                "#FFFFFF", "#000000", "#808080", "#C0C0C0", "#FF0000", "#FF8000",
                "#FFFF00", "#80FF00", "#00C000", "#00FFFF", "#00A0FF", "#0000FF",
                "#8000FF", "#FF00FF", "#FF80C0", "#8B4513", "#FFD700", "#008080",
                "#000080", "#800000", "#808000", "#006400", "#8B0000", "#708090"
            };

            public PaletteForm(float s)
            {
                _s = s;
                FormBorderStyle = FormBorderStyle.None;
                ShowInTaskbar = false;
                StartPosition = FormStartPosition.Manual;
                TopMost = true;
                AutoScaleMode = AutoScaleMode.None;
                BackColor = Color.FromArgb(250, 250, 250);
                ClientSize = new Size(Px(156), Px(140));
                SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
                Paint += delegate (object o, PaintEventArgs e)
                {
                    using (Pen p = new Pen(Color.FromArgb(196, 200, 206)))
                        e.Graphics.DrawRectangle(p, 0, 0, Width - 1, Height - 1);
                };
                BuildSwatches();
            }

            protected override bool ShowWithoutActivation { get { return true; } }

            private int Px(int v) { return (int)Math.Round(v * _s); }

            private void BuildSwatches()
            {
                int size = Px(20);
                int gap = Px(4);
                int left = Px(8);
                int top = Px(8);
                for (int i = 0; i < Hex.Length; i++)
                {
                    int cx = i % 6;
                    int cy = i / 6;
                    Button b = new Button();
                    b.Bounds = new Rectangle(left + cx * (size + gap), top + cy * (size + gap), size, size);
                    b.FlatStyle = FlatStyle.Flat;
                    b.FlatAppearance.BorderColor = Color.FromArgb(180, 184, 190);
                    b.BackColor = ColorTranslator.FromHtml(Hex[i]);
                    b.Tag = Hex[i];
                    b.Click += delegate (object s, EventArgs e)
                    {
                        Color c = ColorTranslator.FromHtml((string)((Button)s).Tag);
                        Action<Color> h = Picked;
                        if (h != null) h(c);
                    };
                    Controls.Add(b);
                }

                Button custom = new Button();
                custom.Text = "自定义颜色…";
                custom.Font = new Font("Microsoft YaHei UI", 8f);
                custom.FlatStyle = FlatStyle.Flat;
                custom.FlatAppearance.BorderColor = Color.FromArgb(200, 204, 210);
                custom.BackColor = Color.White;
                custom.Bounds = new Rectangle(left, Px(108), Px(140), Px(24));
                custom.Click += delegate
                {
                    using (ColorDialog d = new ColorDialog())
                    {
                        d.FullOpen = true;
                        if (d.ShowDialog(this) == DialogResult.OK)
                        {
                            Action<Color> h = Picked;
                            if (h != null) h(d.Color);
                        }
                    }
                };
                Controls.Add(custom);
            }

            public void ShowAt(Point screen)
            {
                Rectangle wa = Screen.FromPoint(screen).WorkingArea;
                int x = screen.X;
                int y = screen.Y;
                if (x + Width > wa.Right) x = wa.Right - Width - Px(4);
                if (y + Height > wa.Bottom) y = screen.Y - Height - Px(48);
                x = Math.Max(wa.Left + Px(4), x);
                y = Math.Max(wa.Top + Px(4), y);
                Location = new Point(x, y);
                if (!Visible) Show();
            }
        }
    }
}
