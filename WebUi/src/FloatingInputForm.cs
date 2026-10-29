using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Automation;
using System.Windows.Automation.Text;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace RichTextGen
{
    /// <summary>
    /// 系统级浮动输入面板（HTML 界面版）。
    ///
    /// 交互流程（跟主程序一致的「选中 → 套格式」逻辑，但目标是任意软件的输入框）：
    ///   1. 在任何软件的输入框里点击 / 拖选文字（全局鼠标钩子检测）；
    ///   2. 光标旁弹出面板，并用 UI Automation 捕获目标窗口里的选中文字；
    ///   3. 点任意颜色 / 格式按钮 → 自动把标签套到那段选区上 → 粘贴回原窗口（自动回填）；
    ///      没有选区时自动进入「手动输入」模式：在面板里输入、像主程序一样选中后套格式，再点回填。
    ///
    /// 窗口用 ShowWithoutActivation 显示，弹出时绝不抢焦点；
    /// 回填时先把焦点交还目标窗口，再走剪贴板 + Ctrl+V，粘贴会自然覆盖原选区。
    /// </summary>
    public sealed class FloatingInputForm : Form
    {
        // ---------------- Win32 ----------------
        private const int WH_MOUSE_LL = 14;
        private const int WM_LBUTTONDOWN = 0x0201;
        private const int WM_LBUTTONUP = 0x0202;
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
        private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, IntPtr dwExtraInfo);

        // ---------------- 静态状态（全局只挂一个钩子 / 一个面板） ----------------
        private static FloatingInputForm _inst;
        private static bool _enabled;
        private static IntPtr _hook;
        private static LowLevelMouseProc _proc;          // 必须保活，否则回调被回收
        private static System.Windows.Forms.Timer _clickTimer;
        private static Point _clickPt;
        private static Point _downPt;
        private static bool _wasDrag;
        private static int _clickSeq;

        // ---------------- 面板实例状态 ----------------
        private readonly WebView2 _web = new WebView2();
        private AutomationElement _target;
        private string _selText;
        private string _selApp;
        private bool _webReady;
        private string _pendingJson;
        private readonly float _s;

        // ============================================================ 开关
        public static bool IsEnabled { get { return _enabled; } }

        public static void SetEnabled(bool on)
        {
            if (on == _enabled) return;
            if (on)
            {
                AppRules.Load();                         // 先读名单，第一次点击就要判断
                if (_inst == null || _inst.IsDisposed) _inst = new FloatingInputForm();
                IntPtr unused = _inst.Handle;            // 提前建句柄，后台线程才能 BeginInvoke
                _inst.InitWeb();                         // 预热 WebView2，第一次弹出更快
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
            AppRules.Flush();                            // 退出前把名单写盘
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
                if (nCode >= 0)
                {
                    int msg = (int)wParam;
                    MSLLHOOKSTRUCT info = (MSLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(MSLLHOOKSTRUCT));
                    Point pt = new Point(info.pt.X, info.pt.Y);

                    if (msg == WM_LBUTTONDOWN)
                    {
                        _downPt = pt;                    // 记下按下位置，松开时判断是不是拖选
                        _wasDrag = false;
                    }
                    else if (msg == WM_LBUTTONUP)
                    {
                        if (!IsOwnWindow(pt))
                        {
                            int dx = pt.X - _downPt.X;
                            int dy = pt.Y - _downPt.Y;
                            _wasDrag = dx * dx + dy * dy > 16;   // 拖动超过 4px 视为拖选文字
                            _clickPt = pt;
                            _clickSeq++;
                            EnsureTimer();
                            _clickTimer.Stop();
                            _clickTimer.Start();         // 90ms 后再判断，等目标窗口完成聚焦
                        }
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

        /// <summary>点在自己窗口上（面板 / 主程序 / 弹窗）不处理，避免自己弹自己。</summary>
        private static bool IsOwnWindow(Point pt)
        {
            try
            {
                IntPtr h = WindowFromPoint(new POINT { X = pt.X, Y = pt.Y });
                if (h == IntPtr.Zero) return false;
                IntPtr root = GetAncestor(h, GA_ROOT);
                uint pid;
                GetWindowThreadProcessId(root, out pid);
                return pid == (uint)Process.GetCurrentProcess().Id;
            }
            catch { return false; }
        }

        // ============================================================ 点击判定 + 选区捕获
        private static void OnClickSettled(Point pt, int seq)
        {
            if (!_enabled || _inst == null || _inst.IsDisposed) return;
            int drag = _wasDrag ? 1 : 0;
            ThreadPool.QueueUserWorkItem(delegate
            {
                // 第一次点击进入未聚焦的窗口时，窗口激活/焦点可能还没就位，
                // 检测失败就隔 220ms 再试两次 —— 修复「要点两下才弹」的问题。
                AutomationElement target = null;
                for (int i = 0; i < 3; i++)
                {
                    if (!_enabled || _clickSeq != seq) return;
                    if (i > 0) Thread.Sleep(220);
                    if (!_enabled || _clickSeq != seq) return;
                    try { target = FindEditable(pt); } catch { }
                    if (target != null) break;
                }

                string sel = "";
                string app = "";
                if (target != null)
                {
                    // 先记录名单（被排除的应用也要记，用户才能回到名单里改回来）
                    string exe = ProcessExeOf(target);
                    app = AppNameOf(target);
                    AppRules.Touch(exe, app);
                    if (!AppRules.IsAllowed(exe))
                    {
                        if (_inst != null && !_inst.IsDisposed && _inst.Visible)
                        {
                            try { _inst.BeginInvoke((MethodInvoker)delegate { _inst.HidePanel(); }); }
                            catch { }
                        }
                        return;                       // 名单不让弹 → 直接静默
                    }

                    sel = ReadSelection(target);             // 优先用 UIA 读选区
                    if (sel.Length == 0 && drag == 1) sel = ReadSelectionByCopy();   // 拖选了但 UIA 读不到 → Ctrl+C 兜底
                }

                AutomationElement t = target;
                string s2 = sel, a2 = app;
                try
                {
                    if (!_enabled || _inst == null || _inst.IsDisposed) return;
                    if (_clickSeq != seq) return;            // 又点了别处，这次作废
                    _inst.BeginInvoke((MethodInvoker)delegate
                    {
                        try
                        {
                            if (!_enabled || _inst == null || _inst.IsDisposed) return;
                            if (_clickSeq != seq) return;
                            if (t == null)
                            {
                                if (_inst.Visible) _inst.HidePanel();
                                return;
                            }
                            _inst.ShowFor(t, pt, s2, a2);
                        }
                        catch { }
                    });
                }
                catch { }
            });
        }

        /// <summary>从点击点向上找 6 层，命中 Edit / Document / ComboBox 即认为点了可输入区域。</summary>
        private static AutomationElement FindEditable(Point pt)
        {
            try
            {
                AutomationElement e = AutomationElement.FromPoint(new System.Windows.Point(pt.X, pt.Y));
                for (int i = 0; e != null && i < 6; i++)
                {
                    ControlType t = null;
                    try { t = e.GetCurrentPropertyValue(AutomationElement.ControlTypeProperty) as ControlType; } catch { }
                    if (t == ControlType.Edit || t == ControlType.Document || t == ControlType.ComboBox)
                    {
                        try
                        {
                            if (e.Current.IsEnabled) return e;   // 不再要求 IsKeyboardFocusable（首次点击时焦点未就位）
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

        /// <summary>用 TextPattern 读目标窗口当前选中的文字（不碰剪贴板、不抢焦点）。</summary>
        private static string ReadSelection(AutomationElement target)
        {
            try
            {
                TextPattern tp = target.GetCurrentPattern(TextPattern.Pattern) as TextPattern;
                if (tp == null) return "";
                TextPatternRange[] ranges = tp.GetSelection();
                if (ranges == null || ranges.Length == 0) return "";
                StringBuilder sb = new StringBuilder();
                foreach (TextPatternRange r in ranges)
                {
                    string t = r.GetText(-1);
                    if (t != null) sb.Append(t);
                }
                return sb.ToString();
            }
            catch { return ""; }
        }

        /// <summary>兜底：向当前焦点窗口发 Ctrl+C 复制选区。拷贝会保护原剪贴板内容。</summary>
        private static string ReadSelectionByCopy()
        {
            string old = "";
            try { old = Clipboard.GetText(); } catch { }
            try
            {
                SendCtrlC();
                Thread.Sleep(110);
                string cur = Clipboard.GetText();
                // 恢复用户原来的剪贴板
                try { Clipboard.SetText(old); } catch { }
                if (cur != null && cur.Length > 0 && cur != old) return cur;
            }
            catch { }
            return "";
        }

        /// <summary>目标元素所属进程的可执行文件名（如 SCPSL.exe），名单以此为准。</summary>
        private static string ProcessExeOf(AutomationElement target)
        {
            try
            {
                int pid = target.Current.ProcessId;
                using (Process p = Process.GetProcessById(pid))
                    return (p.ProcessName ?? "").ToLowerInvariant() + ".exe";
            }
            catch { return ""; }
        }

        /// <summary>取目标窗口的顶层窗口标题，作为面板里显示的来源名。</summary>
        private static string AppNameOf(AutomationElement target)
        {
            try
            {
                int h = target.Current.NativeWindowHandle;
                if (h != 0)
                {
                    IntPtr root = GetAncestor((IntPtr)h, GA_ROOT);
                    if (root != IntPtr.Zero)
                    {
                        AutomationElement top = AutomationElement.FromHandle(root);
                        if (top != null)
                        {
                            string n = top.Current.Name;
                            if (!string.IsNullOrEmpty(n)) return n;
                        }
                    }
                }
            }
            catch { }
            try
            {
                string pn = target.Current.ProcessId.ToString();
                foreach (Process p in Process.GetProcesses())
                {
                    if (p.Id.ToString() == pn)
                    {
                        string name = SafeProcessName(p.ProcessName);
                        if (name.Length > 0) return name;
                    }
                }
            }
            catch { }
            return "目标窗口";
        }

        private static string SafeProcessName(string n)
        {
            if (string.IsNullOrEmpty(n)) return "";
            if (n.Equals("notepad", StringComparison.OrdinalIgnoreCase)) return "记事本";
            if (n.Equals("msedge", StringComparison.OrdinalIgnoreCase) ||
                n.Equals("chrome", StringComparison.OrdinalIgnoreCase)) return "浏览器";
            if (n.Equals("explorer", StringComparison.OrdinalIgnoreCase)) return "资源管理器";
            return n;
        }

        private static void SendCtrlC()
        {
            keybd_event(0x11, 0, 0, IntPtr.Zero);
            keybd_event(0x43, 0, 0, IntPtr.Zero);
            keybd_event(0x43, 0, 2, IntPtr.Zero);
            keybd_event(0x11, 0, 2, IntPtr.Zero);
        }

        private static void SendCtrlV()
        {
            keybd_event(0x11, 0, 0, IntPtr.Zero);   // Ctrl ↓
            keybd_event(0x56, 0, 0, IntPtr.Zero);   // V ↓
            keybd_event(0x56, 0, 2, IntPtr.Zero);   // V ↑
            keybd_event(0x11, 0, 2, IntPtr.Zero);   // Ctrl ↑
        }

        // ============================================================ 面板本体
        private FloatingInputForm()
        {
            int dpi = 96;
            try { dpi = DeviceDpi; } catch { }
            _s = Math.Max(0.8f, Math.Min(2.5f, dpi / 96f));

            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            AutoScaleMode = AutoScaleMode.None;
            BackColor = Color.FromArgb(243, 243, 243);
            ClientSize = new Size(S(500), S(360));
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);

            _web.Dock = DockStyle.Fill;
            _web.DefaultBackgroundColor = Color.FromArgb(243, 243, 243);
            Controls.Add(_web);
        }

        protected override bool ShowWithoutActivation { get { return true; } }   // 弹出绝不抢焦点

        private int S(int v) { return (int)Math.Round(v * _s); }

        private async void InitWeb()
        {
            try
            {
                if (MainForm.WebEnv != null) await _web.EnsureCoreWebView2Async(MainForm.WebEnv);
                else await _web.EnsureCoreWebView2Async();

                _web.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
                _web.CoreWebView2.Settings.IsStatusBarEnabled = false;

                const string origin = MainForm.WebOrigin;
                string html = LoadFloatHtml();
                _web.CoreWebView2.AddWebResourceRequestedFilter(origin + "*", CoreWebView2WebResourceContext.All);
                _web.CoreWebView2.WebResourceRequested += delegate (object s2, CoreWebView2WebResourceRequestedEventArgs e2)
                {
                    try
                    {
                        if (!e2.Request.Uri.StartsWith(origin, StringComparison.OrdinalIgnoreCase)) return;
                        string abs = new Uri(e2.Request.Uri).AbsolutePath;
                        byte[] body = abs.StartsWith("/__float", StringComparison.OrdinalIgnoreCase)
                            ? Encoding.UTF8.GetBytes(html)
                            : new byte[0];
                        bool ok = body.Length > 0;
                        e2.Response = _web.CoreWebView2.Environment.CreateWebResourceResponse(
                            new MemoryStream(body), ok ? 200 : 404, ok ? "OK" : "Not Found",
                            ok ? "Content-Type: text/html; charset=utf-8" : "Content-Type: text/plain");
                    }
                    catch { }
                };

                _web.CoreWebView2.NavigationCompleted += delegate { _webReady = true; PushPending(); };
                _web.CoreWebView2.WebMessageReceived += OnFMessage;
                _web.CoreWebView2.Navigate(origin + "__float.html");
            }
            catch { }
        }

        private static string LoadFloatHtml()
        {
            try
            {
                Assembly asm = Assembly.GetExecutingAssembly();
                foreach (string n in asm.GetManifestResourceNames())
                {
                    if (n.EndsWith("floating.html", StringComparison.OrdinalIgnoreCase))
                        using (Stream s = asm.GetManifestResourceStream(n))
                        using (StreamReader sr = new StreamReader(s, Encoding.UTF8))
                            return sr.ReadToEnd();
                }
            }
            catch { }
            return "<html><body>floating.html 资源缺失</body></html>";
        }

        // ============================================================ 显示 / 隐藏
        internal void ShowFor(AutomationElement target, Point pt, string selText, string app)
        {
            _target = target;
            _selText = selText ?? "";
            _selApp = app ?? "";
            if (!Visible) Show();
            PlaceAt(pt);
            PushState();
        }

        private void PushState()
        {
            string json = StateJson();
            if (_webReady && _web.CoreWebView2 != null)
            {
                try { _web.CoreWebView2.ExecuteScriptAsync("window.fpState && window.fpState(" + JsString(json) + ")"); return; }
                catch { }
            }
            // 失败（WebView 未就绪 / 正忙）→ 记下来，稍后重试
            _pendingJson = json;
            System.Windows.Forms.Timer t = new System.Windows.Forms.Timer();
            t.Interval = 500;
            t.Tick += delegate
            {
                try
                {
                    t.Stop(); t.Dispose();
                    if (_webReady && _web.CoreWebView2 != null && _pendingJson == json)
                    {
                        try
                        {
                            _web.CoreWebView2.ExecuteScriptAsync("window.fpState && window.fpState(" + JsString(json) + ")");
                            _pendingJson = null;
                        }
                        catch { }
                    }
                }
                catch { }
            };
            t.Start();
        }

        private void PushPending()
        {
            if (_pendingJson == null || _web.CoreWebView2 == null) return;
            string j = _pendingJson;
            _pendingJson = null;
            try { _web.CoreWebView2.ExecuteScriptAsync("window.fpState && window.fpState(" + JsString(j) + ")"); }
            catch { }
        }

        private string StateJson()
        {
            int len = _selText.Length;
            return "{\"mode\":\"" + (len > 0 ? "sel" : "type") + "\"" +
                   ",\"len\":" + len +
                   ",\"text\":" + Quote(_selText) +
                   ",\"app\":" + Quote(_selApp) + "}";
        }

        private void PlaceAt(Point pt)
        {
            Rectangle wa = Screen.FromPoint(pt).WorkingArea;
            int x = pt.X + S(16);
            int y = pt.Y + S(24);
            if (x + Width > wa.Right) x = pt.X - Width - S(16);
            if (y + Height > wa.Bottom) y = pt.Y - Height - S(12);
            x = Math.Max(wa.Left + S(4), Math.Min(x, wa.Right - Width - S(4)));
            y = Math.Max(wa.Top + S(4), Math.Min(y, wa.Bottom - Height - S(4)));
            Location = new Point(x, y);
        }

        internal void HidePanel()
        {
            if (Visible) Hide();
        }

        // ============================================================ 面板 → 宿主 消息
        private void OnFMessage(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            string raw = "";
            try { raw = e.TryGetWebMessageAsString(); } catch { }
            if (string.IsNullOrEmpty(raw)) return;
            string[] parts = raw.Split('|');
            string cmd = parts[0];
            try
            {
                switch (cmd)
                {
                    case "f":
                        DispatchF(parts);
                        break;
                }
            }
            catch { }
        }

        /// <summary>parts[1]=子命令，后面是参数（base64 编码，避免 | 转义问题）。</summary>
        private void DispatchF(string[] parts)
        {
            string sub = parts.Length > 1 ? parts[1] : "";
            switch (sub)
            {
                case "close":
                    HidePanel();
                    break;

                // 前端已经算好最终文本（含正确的标签嵌套），这里只负责粘回原窗口
                case "paste":
                    if (parts.Length >= 3) PasteBack(B64(parts[2]));
                    break;

                case "pickSelColor":
                    PickSelColor();
                    break;

                case "pickColorTyp":
                    PickTypColor();
                    break;

                case "type":
                    if (parts.Length >= 3) PasteBack(B64(parts[2]));
                    break;

                case "copy":
                    if (parts.Length >= 3) SetClipboard(B64(parts[2]), 4);
                    break;

                case "move":
                    if (parts.Length >= 4)
                    {
                        int dx, dy;
                        if (int.TryParse(parts[2], out dx) && int.TryParse(parts[3], out dy))
                        {
                            if (Math.Abs(dx) <= 400 && Math.Abs(dy) <= 400)
                                Location = new Point(Left + dx, Top + dy);
                        }
                    }
                    break;
            }
        }

        private static string B64(string s)
        {
            try { return Encoding.UTF8.GetString(Convert.FromBase64String(s ?? "")); }
            catch { return ""; }
        }

        private void PasteBack(string text)
        {
            if (text.Length == 0) return;
            AutomationElement t = _target;
            HidePanel();
            try
            {
                if (t != null)
                {
                    IntPtr root = IntPtr.Zero;
                    try
                    {
                        int h = t.Current.NativeWindowHandle;
                        if (h != 0) root = GetAncestor((IntPtr)h, GA_ROOT);
                    }
                    catch { }
                    if (root != IntPtr.Zero) SetForegroundWindow(root);
                    try { t.SetFocus(); } catch { }
                }
                Thread.Sleep(90);
                if (!SetClipboard(text, 6)) return;
                Thread.Sleep(40);
                SendCtrlV();
            }
            catch { }
        }

        /// <summary>选区模式的自定义颜色：色板选完把 hex 交给前端套色（前端统一算嵌套）。</summary>
        private void PickSelColor()
        {
            using (ColorDialog d = new ColorDialog())
            {
                d.FullOpen = true;
                if (d.ShowDialog(this) == DialogResult.OK)
                {
                    string hex = "#" + d.Color.R.ToString("X2") + d.Color.G.ToString("X2") + d.Color.B.ToString("X2");
                    if (_webReady && _web.CoreWebView2 != null)
                        try { _web.CoreWebView2.ExecuteScriptAsync("window.fpSelColor && window.fpSelColor(" + JsString(hex) + ")"); }
                        catch { }
                }
                if (Visible) Activate();
            }
        }

        private void PickTypColor()
        {
            using (ColorDialog d = new ColorDialog())
            {
                d.FullOpen = true;
                if (d.ShowDialog(this) == DialogResult.OK)
                {
                    string hex = "#" + d.Color.R.ToString("X2") + d.Color.G.ToString("X2") + d.Color.B.ToString("X2");
                    if (_webReady && _web.CoreWebView2 != null)
                        try { _web.CoreWebView2.ExecuteScriptAsync("window.fpColorTyp && window.fpColorTyp(" + JsString(hex) + ")"); }
                        catch { }
                }
                if (Visible) Activate();
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

        // ---------------- 小工具 ----------------
        private static string Quote(string s)
        {
            if (s == null) return "\"\"";
            StringBuilder sb = new StringBuilder("\"");
            foreach (char ch in s)
            {
                switch (ch)
                {
                    case '\\': sb.Append("\\\\"); break;
                    case '"': sb.Append("\\\""); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (ch < ' ') sb.Append("\\u").Append(((int)ch).ToString("x4"));
                        else sb.Append(ch);
                        break;
                }
            }
            sb.Append('"');
            return sb.ToString();
        }

        /// <summary>把 JSON 字符串包成 JS 字符串字面量（用于 ExecuteScriptAsync）。</summary>
        private static string JsString(string json)
        {
            StringBuilder sb = new StringBuilder("\"");
            foreach (char ch in json)
            {
                switch (ch)
                {
                    case '\\': sb.Append("\\\\"); break;
                    case '"': sb.Append("\\\""); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': break;
                    default: sb.Append(ch); break;
                }
            }
            sb.Append('"');
            return sb.ToString();
        }
    }
}