using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace RichTextGen
{
    public enum WorkMode
    {
        Minimal = 0,    // 极简：功能齐全但只收成小窗
        SemiFull = 1,   // 半全：默认展开常用组 + 基础热键
        Full = 2        // 全工：全部展开 + 全部热键
    }

    public class HotkeyDef
    {
        public string Id;
        public string Label;
        public string Def;
        public bool AlwaysOn;
        public HotkeyDef(string id, string label, string def, bool alwaysOn)
        { Id = id; Label = label; Def = def; AlwaysOn = alwaysOn; }
    }

    /// <summary>热键配置（存 %APPDATA%\RichTextGen\hotkeys.ini）</summary>
    public static class HotkeyStore
    {
        public static readonly List<HotkeyDef> Defs = new List<HotkeyDef>
        {
            new HotkeyDef("toggleWindow",  "显示 / 隐藏窗口",        "Ctrl+F3", true),
            new HotkeyDef("sendOnce",      "一次性发送（生成→游戏）", "Ctrl+F4", false),
            new HotkeyDef("stopSend",      "立即停止发送",            "Ctrl+F5", false),
            new HotkeyDef("refreshOutput", "输出区立即显示输出",      "Ctrl+F6", false),
            new HotkeyDef("togglePreview", "开启 / 关闭预览",         "Ctrl+F7", false),
            new HotkeyDef("toggleOutMode", "输出区预览模式切换",      "Ctrl+F8", false),
            new HotkeyDef("exitApp",       "结束进程（退出）",        "Ctrl+F9", false)
        };

        private static readonly Dictionary<string, string> _map = new Dictionary<string, string>();
        private static string PathFile
        {
            get
            {
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RichTextGen");
                Directory.CreateDirectory(dir);
                return Path.Combine(dir, "hotkeys.ini");
            }
        }

        public static string Get(string id)
        {
            if (_map.Count == 0) Load();
            string v;
            if (_map.TryGetValue(id, out v)) return v;
            foreach (HotkeyDef d in Defs) if (d.Id == id) return d.Def;
            return "";
        }

        public static void Set(string id, string value)
        {
            if (_map.Count == 0) Load();
            _map[id] = value;
            Save();
        }

        public static void ResetAll()
        {
            _map.Clear();
            foreach (HotkeyDef d in Defs) _map[d.Id] = d.Def;
            Save();
        }

        public static void Load()
        {
            _map.Clear();
            try
            {
                if (File.Exists(PathFile))
                {
                    foreach (string line in File.ReadAllLines(PathFile, Encoding.UTF8))
                    {
                        int eq = line.IndexOf('=');
                        if (eq > 0) _map[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
                    }
                }
            }
            catch { }
            foreach (HotkeyDef d in Defs) if (!_map.ContainsKey(d.Id)) _map[d.Id] = d.Def;
        }

        public static void Save()
        {
            try
            {
                StringBuilder sb = new StringBuilder();
                foreach (KeyValuePair<string, string> kv in _map) sb.AppendLine(kv.Key + "=" + kv.Value);
                File.WriteAllText(PathFile, sb.ToString(), Encoding.UTF8);
            }
            catch { }
        }

        /// <summary>"Ctrl+Alt+S" → MOD_* + 虚拟键码</summary>
        public static bool Parse(string text, out uint mods, out uint vk)
        {
            mods = 0; vk = 0;
            if (string.IsNullOrEmpty(text)) return false;
            string[] parts = text.Split('+');
            foreach (string raw in parts)
            {
                string p = raw.Trim();
                if (p.Length == 0) continue;
                string low = p.ToLowerInvariant();
                if (low == "ctrl" || low == "control") { mods |= 0x2; continue; }
                if (low == "alt") { mods |= 0x1; continue; }
                if (low == "shift") { mods |= 0x4; continue; }
                if (low == "win") { mods |= 0x8; continue; }
                Keys k;
                if (!Enum.TryParse<Keys>(p, true, out k)) return false;
                vk = (uint)k;
            }
            return vk != 0;
        }

        public static string Describe(Keys key, bool ctrl, bool alt, bool shift)
        {
            if (key == Keys.None) return "";
            string s = "";
            if (ctrl) s += "Ctrl+";
            if (alt) s += "Alt+";
            if (shift) s += "Shift+";
            string name = key.ToString();
            if (name.StartsWith("D") && name.Length == 2 && char.IsDigit(name[1])) name = name.Substring(1);
            if (name.StartsWith("NumPad")) return "";
            return s + name;
        }
    }

    /// <summary>热键设置：捕获期间窗口保持焦点（用 KeyDown 捕获，不抢系统热键）</summary>
    public class HotkeyDialog : Form
    {
        private readonly Dictionary<string, TextBox> _boxes = new Dictionary<string, TextBox>();
        private readonly Dictionary<string, Label> _state = new Dictionary<string, Label>();

        public HotkeyDialog()
        {
            Text = "热键设置（捕获时请保持本窗口在前台）";
            Font = new Font("Microsoft YaHei", 9f);
            ClientSize = new Size(560, 380);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;

            Label hint = new Label();
            hint.Text = "点进输入框后直接按组合键即可记录（例如 Ctrl+F3）。用「应用」生效；「重置」恢复默认。";
            hint.Location = new Point(14, 12);
            hint.Size = new Size(520, 36);
            hint.Tag = "hint";
            Controls.Add(hint);

            int y = 54;
            foreach (HotkeyDef d in HotkeyStore.Defs)
            {
                Label l = new Label();
                l.Text = d.Label + (d.AlwaysOn ? "（常驻）" : "");
                l.Location = new Point(14, y + 6);
                l.AutoSize = true;
                Controls.Add(l);

                TextBox box = new TextBox();
                box.Location = new Point(240, y + 2);
                box.Size = new Size(150, 24);
                box.ReadOnly = true;
                box.Text = HotkeyStore.Get(d.Id);
                string id = d.Id;
                box.KeyDown += delegate (object s, KeyEventArgs e)
                {
                    e.SuppressKeyPress = true;
                    e.Handled = true;
                    Keys k = e.KeyCode;
                    if (k == Keys.ControlKey || k == Keys.ShiftKey || k == Keys.Menu || k == Keys.LWin || k == Keys.RWin) return;
                    if (k == Keys.Escape || k == Keys.Back)
                    {
                        box.Text = "";
                        return;
                    }
                    string txt = HotkeyStore.Describe(k, e.Control, e.Alt, e.Shift);
                    if (txt.Length > 0) box.Text = txt;
                };
                Controls.Add(box);
                _boxes[d.Id] = box;

                Label st = new Label();
                st.Location = new Point(400, y + 6);
                st.Size = new Size(150, 20);
                st.Tag = "hint";
                st.Text = "";
                Controls.Add(st);
                _state[d.Id] = st;

                y += 32;
            }

            Button apply = new Button();
            apply.Text = "应用";
            apply.Size = new Size(100, 30);
            apply.Location = new Point(14, y + 10);
            apply.Tag = "primary";
            apply.Click += delegate
            {
                foreach (KeyValuePair<string, TextBox> kv in _boxes) HotkeyStore.Set(kv.Key, kv.Value.Text.Trim());
                HotkeyStore.Save();
                MessageBox.Show(this, "已保存。热键会在主窗口重新注册（部分组合可能被系统或其他程序占用，占用会显示在状态列）。",
                    "热键已保存", MessageBoxButtons.OK, MessageBoxIcon.Information);
                DialogResult = DialogResult.OK;
                Close();
            };
            Controls.Add(apply);

            Button reset = new Button();
            reset.Text = "重置默认";
            reset.Size = new Size(100, 30);
            reset.Location = new Point(122, y + 10);
            reset.Click += delegate
            {
                HotkeyStore.ResetAll();
                foreach (KeyValuePair<string, TextBox> kv in _boxes) kv.Value.Text = HotkeyStore.Get(kv.Key);
            };
            Controls.Add(reset);

            Button close = new Button();
            close.Text = "关闭";
            close.Size = new Size(90, 30);
            close.Location = new Point(230, y + 10);
            close.Click += delegate { DialogResult = DialogResult.Cancel; Close(); };
            Controls.Add(close);

            ClientSize = new Size(560, y + 56);
            Theme.Apply(this);
        }
    }

    // ==================================================================== 主窗体：托盘 / 模式 / 热键
    public partial class MainForm
    {
        [DllImport("user32.dll")]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
        [DllImport("user32.dll")]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        private const int WM_HOTKEY = 0x0312;

        private NotifyIcon _tray;
        private bool _reallyExit;
        private readonly List<int> _hotkeyIds = new List<int>();
        private readonly Dictionary<int, string> _hotkeyById = new Dictionary<int, string>();
        private WorkMode _mode = WorkMode.SemiFull;
        private volatile bool _stopRequested;
        private WebBrowser _wbOut;              // 输出区预览模式用
        private bool _outPreviewMode;
        private bool uiReady;
        private Button btnUpdate;
        private Label lblNetState;
        private Timer timerNet;
        private Updater.Info pendingUpdate;

        private void InitNetAndUpdate()
        {
            timerNet = new Timer();
            timerNet.Interval = 3000;
            timerNet.Tick += delegate
            {
                bool on = OnlineColors.IsOnline();
                if (lblNetState != null)
                {
                    lblNetState.Text = on ? "● 联网正常" : "● 网络断开";
                    lblNetState.ForeColor = on ? Color.FromArgb(0, 140, 60) : Color.FromArgb(190, 60, 60);
                }
            };
            timerNet.Start();
            SetTimerOnce(1200, CheckUpdateAsync);
        }

        private void SetTimerOnce(int ms, Action act)
        {
            Timer t = new Timer();
            t.Interval = ms;
            t.Tick += delegate { t.Stop(); t.Dispose(); try { act(); } catch { } };
            t.Start();
        }

        private void CheckUpdateAsync()
        {
            System.Threading.ThreadPool.QueueUserWorkItem(delegate
            {
                string err;
                Updater.Info info = Updater.Check(out err);
                try
                {
                    BeginInvoke((Action)delegate
                    {
                        if (info == null) { status("更新检查失败：" + err); return; }
                        if (Updater.IsNewer(info.Version, Version))
                        {
                            pendingUpdate = info;
                            if (btnUpdate != null)
                            {
                                btnUpdate.Visible = true;
                                btnUpdate.Text = "⬆ 更新到 " + info.Version + (info.Mandatory ? "（强制）" : "");
                            }
                            status("发现新版本 " + info.Version + "（当前 " + Version + "）");
                            if (info.Mandatory)
                            {
                                MessageBox.Show(this, "检测到必须更新的新版本 " + info.Version + "，将立即开始下载安装。", "强制更新", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                                DoUpdate();
                            }
                        }
                        else status("已是最新版本（" + Version + "）· 清单来源 " + info.Source);
                    });
                }
                catch { }
            });
        }

        private void DoUpdate()
        {
            if (pendingUpdate == null) return;
            string url = pendingUpdate.Installer.Length > 0 ? pendingUpdate.Installer : pendingUpdate.Portable;
            if (url.Length == 0) { MessageBox.Show(this, "清单里没有下载地址。", "提示"); return; }
            status("开始下载 " + pendingUpdate.Version + " …");
            System.Threading.ThreadPool.QueueUserWorkItem(delegate
            {
                string file = Updater.Download(url, delegate (int pct, string msg)
                {
                    try { BeginInvoke((Action)delegate { status("下载中 " + pct + "% · " + msg); }); } catch { }
                });
                try
                {
                    BeginInvoke((Action)delegate
                    {
                        if (file.Length == 0) { MessageBox.Show(this, "下载失败，请稍后重试或到 Releases 页手动下载。", "更新失败"); return; }
                        DialogResult r = MessageBox.Show(this, "已下载完成，现在运行安装程序吗？（会覆盖旧版本）", "运行安装程序", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                        if (r == DialogResult.Yes) { try { System.Diagnostics.Process.Start(file); } catch { } RealExit(); }
                    });
                }
                catch { }
            });
        }   // 界面构建完成前禁止生成（否则空引用崩溃）
        private ComboBox _cboMode;

        // ---------------------------------------------------------------- 托盘
        private void InitTray()
        {
            _tray = new NotifyIcon();
            _tray.Icon = LoadAppIcon();
            _tray.Text = "彩色文本生成器 v" + Version;
            _tray.Visible = true;

            ContextMenuStrip menu = new ContextMenuStrip();
            menu.Items.Add("显示 / 隐藏窗口", null, delegate { ToggleWindowVisible(); });
            menu.Items.Add("一次性发送", null, delegate { SendOnce(); });
            menu.Items.Add("刷新输出", null, delegate { UpdatePreviewNow(); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("打开使用教程", null, delegate { new HelpWindow().Show(); });
            menu.Items.Add("图片系统", null, delegate { new ImageSystemDialog(this).Show(this); });
            menu.Items.Add("热键设置", null, delegate { new HotkeyDialog().ShowDialog(this); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("退出（结束进程）", null, delegate { RealExit(); });
            _tray.ContextMenuStrip = menu;
            _tray.DoubleClick += delegate { ToggleWindowVisible(); };
        }

        private void ToggleWindowVisible()
        {
            if (Visible && WindowState != FormWindowState.Minimized)
            {
                Hide();
                _tray.Visible = true;
            }
            else
            {
                Show();
                WindowState = FormWindowState.Normal;
                ShowInTaskbar = true;
                Activate();
                BringToFront();
            }
        }

        /// <summary>正规退出：Ctrl+F9 / 托盘菜单 → 先弹确认，确认后才结束进程</summary>
        private void RealExit()
        {
            if (!_reallyExit)
            {
                DialogResult r = MessageBox.Show(this, "您确定退出该工具吗？", "退出确认",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
                if (r != DialogResult.Yes) { status("已取消退出"); return; }
            }
            _reallyExit = true;
            if (_tray != null) { _tray.Visible = false; _tray.Dispose(); }
            Close();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!_reallyExit && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;                 // 关窗口 = 收到托盘，不结束进程
                Hide();
                ShowInTaskbar = false;
                if (_tray != null) _tray.ShowBalloonTip(2500, "仍在后台运行",
                    "已最小化到托盘。用热键 " + HotkeyStore.Get("toggleWindow") + " 或双击托盘图标唤回；托盘菜单可退出。",
                    ToolTipIcon.Info);
                return;
            }
            if (_tray != null) { _tray.Visible = false; _tray.Dispose(); }
            UnregisterAllHotkeys();
            base.OnFormClosing(e);
        }

        // ---------------------------------------------------------------- 热键
        private void RegisterAllHotkeys()
        {
            UnregisterAllHotkeys();
            int id = 0xA000;
            foreach (HotkeyDef d in HotkeyStore.Defs)
            {
                if (!d.AlwaysOn && _mode == WorkMode.Minimal) continue;
                if (!d.AlwaysOn && _mode == WorkMode.SemiFull &&
                    !(d.Id == "refreshOutput" || d.Id == "togglePreview" || d.Id == "stopSend")) continue;
                string txt = HotkeyStore.Get(d.Id);
                uint mods, vk;
                if (!HotkeyStore.Parse(txt, out mods, out vk))
                {
                    SetHotkeyState(d.Id, "未设置/无法解析");
                    continue;
                }
                bool ok = RegisterHotKey(Handle, id, mods, vk);
                SetHotkeyState(d.Id, ok ? "已注册" : "注册失败（被占用）");
                if (ok) { _hotkeyIds.Add(id); _hotkeyById[id] = d.Id; }
                id++;
            }
        }

        private void UnregisterAllHotkeys()
        {
            foreach (int i in _hotkeyIds) { try { UnregisterHotKey(Handle, i); } catch { } }
            _hotkeyIds.Clear();
            _hotkeyById.Clear();
        }

        private readonly Dictionary<string, string> _hotkeyState = new Dictionary<string, string>();
        public string GetHotkeyState(string id)
        {
            string v;
            return _hotkeyState.TryGetValue(id, out v) ? v : "";
        }
        private void SetHotkeyState(string id, string s) { _hotkeyState[id] = s; }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_HOTKEY)
            {
                int id = m.WParam.ToInt32();
                string action;
                if (_hotkeyById.TryGetValue(id, out action)) DoHotkeyAction(action);
            }
            base.WndProc(ref m);
        }

        private void DoHotkeyAction(string action)
        {
            switch (action)
            {
                case "toggleWindow": ToggleWindowVisible(); break;
                case "sendOnce": SendOnce(); break;
                case "stopSend": _stopRequested = true; status("已请求停止发送"); break;
                case "refreshOutput": UpdatePreviewNow(); status("输出已刷新"); break;
                case "togglePreview": TogglePreview(); break;
                case "toggleOutMode": ToggleOutputPreviewMode(); break;
                case "exitApp": RealExit(); break;
            }
        }

        /// <summary>一次性发送（生成页当前结果 → 游戏）</summary>
        private void SendOnce()
        {
            if (Visible == false) { /* 允许后台触发 */ }
            string code = BuildFullCode();
            if (code.Length == 0) { status("没有可发送的内容"); return; }
            SetGeneratedCode(code);
            tabs.SelectedTab = pageSend;
            chkUseGen.Checked = false;
            if (!Visible) { Show(); WindowState = FormWindowState.Normal; }
            Activate();
            DoSend();
        }

        // ---------------------------------------------------------------- 显示开关
        private void TogglePreview()
        {
            if (rightSplit == null) return;
            rightSplit.Panel1Collapsed = !rightSplit.Panel1Collapsed;
            status(rightSplit.Panel1Collapsed ? "预览已关闭（热键可再开）" : "预览已开启");
        }

        private void ToggleOutputPreviewMode()
        {
            _outPreviewMode = !_outPreviewMode;
            if (_wbOut == null)
            {
                _wbOut = new WebBrowser();
                _wbOut.Dock = DockStyle.Fill;
                _wbOut.ScriptErrorsSuppressed = true;
                _wbOut.IsWebBrowserContextMenuEnabled = false;
                txtResult.Parent.Controls.Add(_wbOut);
            }
            if (_outPreviewMode)
            {
                _wbOut.Visible = true;
                _wbOut.BringToFront();
                RenderOutPreview();
                status("输出区：预览模式（渲染效果）");
            }
            else
            {
                _wbOut.Visible = false;
                txtResult.Visible = true;
                txtResult.BringToFront();
                status("输出区：代码模式");
            }
        }

        private void RenderOutPreview()
        {
            if (_wbOut == null) return;
            try
            {
                string back = Theme.PreviewBack.R + "," + Theme.PreviewBack.G + "," + Theme.PreviewBack.B;
                string html = "<html><head><meta charset='utf-8'><style>body{margin:0;padding:8px;background:rgb("
                    + back + ");color:rgb(" + Theme.PreviewText.R + "," + Theme.PreviewText.G + "," + Theme.PreviewText.B
                    + ");font-family:'Microsoft YaHei';font-size:" + (int)(14 * UiZoom.Factor) + "px;word-break:break-all}</style></head><body>"
                    + HtmlFromCode(txtResult.Text) + "</body></html>";
                _wbOut.DocumentText = html;
            }
            catch { }
        }

        // ---------------------------------------------------------------- 工作模式
        private void ApplyWorkMode(WorkMode m)
        {
            _mode = m;
            switch (m)
            {
                case WorkMode.Minimal:
                    SetAllGroupsExpanded(false);
                    SetWindowSize(900, 600);
                    if (chkFreeEdit != null) chkFreeEdit.Checked = false;
                    status("极简模式：窗口最小化，功能仍完整（组可手动展开）");
                    break;
                case WorkMode.SemiFull:
                    SetAllGroupsExpanded(false);
                    SetWindowSize(1120, 780);
                    if (chkFreeEdit != null) chkFreeEdit.Checked = false;
                    status("半全模式：常用分组展开 + 基础热键（刷新输出/预览开关/停止发送）");
                    break;
                default:
                    if (chkFreeEdit != null) chkFreeEdit.Checked = true;   // 触发全部展开
                    SetWindowSize(1320, 900);
                    status("全工模式：全部展开 + 全部热键（发送/停止/输出/预览/退出）");
                    break;
            }
            RegisterAllHotkeys();
        }

        private void SetWindowSize(int w, int h)
        {
            try
            {
                Rectangle wa = Screen.FromControl(this).WorkingArea;
                if (w > wa.Width - 20) w = wa.Width - 20;
                if (h > wa.Height - 20) h = wa.Height - 20;
                if (WindowState == FormWindowState.Maximized) WindowState = FormWindowState.Normal;
                ClientSize = new Size(w, h);
            }
            catch { }
        }
    }
}
