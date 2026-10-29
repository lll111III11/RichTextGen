using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.IO.Pipes;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Microsoft.Win32;

namespace RichTextGen
{
    internal static class Program
    {
        /// <summary>命令行传入的待打开文件（Shell 右键「用彩色文本生成器打开」等）</summary>
        public static string[] StartupFiles = new string[0];

        /// <summary>
        /// 单实例互斥体：同一版本只允许一个进程（后台只保留一个）；
        /// 名称里带版本号，因此 5.9.0.6 与 5.9.0.7 这类不同版本可以同时各跑一个，互不干扰。
        /// </summary>
        private static Mutex _instanceMutex;
        private static string InstanceKey
        {
            get
            {
                string v = MainForm.Version;
                return string.IsNullOrEmpty(v) ? "unknown" : v.Replace(' ', '_');
            }
        }
        private static string PipeName { get { return "RichTextGen_IPC_" + InstanceKey; } }

        [STAThread]
        private static void Main(string[] args)
        {
            bool created;
            try
            {
                _instanceMutex = new Mutex(true, @"Local\RichTextGen_SingleInstance_" + InstanceKey, out created);
            }
            catch { created = true; }

            if (!created)
            {
                // 已有实例在运行：把命令行里的文件（或空启动）转交给它，然后本进程直接退出。
                SendToPrimary(args);
                return;
            }

            if (args != null) StartupFiles = args;
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Thread pipe = new Thread(PipeLoop);
            pipe.IsBackground = true;
            pipe.Start();
            try { Application.Run(new MainForm()); }
            catch (Exception ex)
            {
                MessageBox.Show(ex.ToString(), "运行错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        /// <summary>把启动参数交给已在运行的实例（同名管道，仅当前用户可连）。</summary>
        private static void SendToPrimary(string[] args)
        {
            try
            {
                using (NamedPipeClientStream c = new NamedPipeClientStream(".", PipeName, PipeDirection.Out))
                {
                    c.Connect(2000);
                    using (StreamWriter w = new StreamWriter(c, new UTF8Encoding(false)))
                    {
                        w.WriteLine("__ACTIVATE__");
                        foreach (string a in args ?? new string[0])
                        {
                            string p = (a ?? "").Trim().Trim('"');
                            if (p.Length > 0 && File.Exists(p)) w.WriteLine(p);
                        }
                        w.Flush();
                    }
                }
            }
            catch { }
        }

        /// <summary>主实例上的管道监听：收到路径就在主窗口里开标签，收到 ACTIVATE 就把窗口叫到前台。</summary>
        private static void PipeLoop()
        {
            while (true)
            {
                try
                {
                    using (NamedPipeServerStream s = new NamedPipeServerStream(PipeName, PipeDirection.In, 1))
                    {
                        s.WaitForConnection();
                        List<string> msgs = new List<string>();
                        using (StreamReader r = new StreamReader(s, Encoding.UTF8))
                        {
                            string line;
                            while ((line = r.ReadLine()) != null)
                                if (line.Trim().Length > 0) msgs.Add(line.Trim());
                        }
                        if (msgs.Count > 0) MainForm.ForwardToMain(msgs);
                    }
                }
                catch { Thread.Sleep(300); }
            }
        }
    }

    /// <summary>
    /// WebView2 宿主：无边框 + DWM 圆角 + 高 DPI；HTML 做界面，C# 做核心（生成/网络/文件/更新/导出）
    /// 通信协议：JS → C#  postMessage("cmd|payload")；C# → JS  ExecuteScriptAsync("ui.onXxx(...)")
    /// </summary>
    public partial class MainForm : Form
    {
        /// <summary>版本号唯一来源：直接取 csproj 写入的程序集版本，避免与界面/更新清单不一致</summary>
        public static readonly string Version = typeof(MainForm).Assembly.GetName().Version.ToString();

        private WebView2 web;
        private System.Windows.Forms.Timer updateTimer;
        private string pendingVer = "", pendingNotes = "", pendingType = "", pendingUrl = "";
        private HotkeyManager hotkeys;
        private NotifyIcon tray;                         // 托盘图标（Ctrl+F2 隐藏后的唤回入口）
        private bool trayTipShown;
        private bool _allowClose;                        // true 才允许真正退出（仅托盘菜单「退出」置位）
        private readonly List<string> _pendingOpen = new List<string>();   // 命令行/右键传入的待打开文件

        /// <summary>true = 拖拽标签分裂出来的副窗口：不注册热键/托盘/更新检查/AI 端口，关闭即真关。</summary>
        private readonly bool _secondary;
        /// <summary>副窗口启动时要打开的文档（fileOpened JSON）。</summary>
        private string _pendingTear;
        /// <summary>主窗口引用：单实例转发时用它把文件送进来。</summary>
        private static MainForm _mainWindow;
        /// <summary>本窗口里未保存的标签数量（界面每次变化时回报，用于副窗口关闭前确认）。</summary>
        private int _dirtyCount;

        // ============================================================ 置顶 / 同步输出
        /// <summary>是否把结果自动粘贴到「最近点击的外部窗口」（游戏聊天框等）。</summary>
        private bool _syncOut;
        /// <summary>最近一次处于前台的外部窗口（不是本进程的窗口）。</summary>
        private static IntPtr _lastExternal;
        private static string _lastExternalTitle = "";
        private System.Windows.Forms.Timer _fgTimer;
        private string _lastSent = "";
        private DateTime _lastSentAt = DateTime.MinValue;
        /// <summary>等待粘贴的文字：先备好，等用户点击目标窗口让它自然获得焦点后再按 Ctrl+V。</summary>
        private string _pendingPaste;
        private DateTime _pendingAt = DateTime.MinValue;
        private IntPtr _lastFg;

        // ============================================================ 强制应用刷新
        /// <summary>界面所用的虚拟 https 源（重载时带时间戳重新请求内嵌 HTML）。</summary>
        internal const string WebOrigin = "https://richtextgen.local/";
        /// <summary>刷新前由界面打包的标签页正文；页面重载完成后回灌。</summary>
        private string _pendingRefresh;
        private bool _refreshFallback;
        private bool _refreshing;

        // ============================================================ 音乐播放列表
        private readonly List<string> _playlist = new List<string>();
        private int _musicIndex = -1;
        private bool _musicPlaying;

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int val, int size);
        [DllImport("user32.dll")]
        private static extern bool ReleaseCapture();
        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr h);
        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr h, int cmd);
        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wp, IntPtr lp);
        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")]
        private static extern int GetWindowTextLength(IntPtr h);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(IntPtr h, StringBuilder sb, int max);
        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [DllImport("user32.dll")]
        private static extern bool IsWindow(IntPtr h);
        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll")]
        private static extern bool IsIconic(IntPtr h);
        [DllImport("user32.dll")]
        private static extern void keybd_event(byte vk, byte scan, uint flags, IntPtr extra);

        // 无边框窗口的鼠标拖拽缩放：WebView2 铺满客户区时窗体收不到边上的命中测试，
        // 因此留出一圈 resizeBand 宽的「拖拽边」（WebView2 随 Padding 内缩），由 WndProc 返回 HT* 交系统缩放。
        private const int WM_NCHITTEST = 0x0084;
        private const int HTCLIENT = 1;
        private const int HTLEFT = 10, HTRIGHT = 11, HTTOP = 12, HTTOPLEFT = 13;
        private const int HTTOPRIGHT = 14, HTBOTTOM = 15, HTBOTTOMLEFT = 16, HTBOTTOMRIGHT = 17;
        private int resizeBand = 6;            // 拖拽边宽度（逻辑像素，按 DPI 放大）
        private int bandState = -1;            // 上次应用的边宽（-1=尚未应用），避免重复 Layout

        public MainForm() : this(false, null) { }

        /// <param name="secondary">true = 拖拽标签分裂出的副窗口（同一进程、独立窗口）</param>
        /// <param name="tearJson">副窗口要打开的文档（fileOpened JSON）</param>
        internal MainForm(bool secondary, string tearJson)
        {
            _secondary = secondary;
            _pendingTear = tearJson;
            if (!secondary) _mainWindow = this;

            Text = "彩色文本生成器 v" + Version;
            Font = new Font("Microsoft YaHei UI", 9f);
            // 用 exe 内嵌图标（csproj 的 ApplicationIcon），否则 WinForms 会显示系统默认图标
            try { Icon = System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            FormBorderStyle = FormBorderStyle.None;      // 无边框：标题栏由 HTML 画
            ClientSize = new Size(1180, 800);
            MinimumSize = new Size(880, 600);
            StartPosition = FormStartPosition.CenterScreen;
            if (!secondary) LoadWindowState();           // 只让主窗口记住位置与大小，副窗口由撕裂点决定
            if (WindowState == FormWindowState.Normal) _normBounds = Bounds;
            ApplyResizeBand();                           // 留出可拖拽缩放的边（WebView2 随 Padding 内缩）
            BackColor = Color.FromArgb(243, 243, 243);
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);

            // 置顶 / 同步输出：从 ui.json 恢复上次的选择（关掉再打开依然生效）
            try { TopMost = UIPrefs.Topmost; } catch { }
            _syncOut = UIPrefs.SyncOut;
            LoadMusic();
            StartForegroundWatch();

            // WebView2 用户数据目录放到 %APPDATA%，避免把缓存写进安装目录（装到只读目录时也不会启动失败）
            try
            {
                string udf = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RichTextGen", "WebView2");
                Directory.CreateDirectory(udf);
                Environment.SetEnvironmentVariable("WEBVIEW2_USER_DATA_FOLDER", udf);
            }
            catch { }

            web = new WebView2();
            web.Dock = DockStyle.Fill;
            // 与窗口同色：WebView2 起来之前先是一块干净的记事本空白，不会闪白
            try { web.DefaultBackgroundColor = Color.FromArgb(243, 243, 243); } catch { }
            Controls.Add(web);
            web.CoreWebView2InitializationCompleted += OnWebReady;
            PrepareWeb();

            if (!secondary)
            {
                // 更新检测：启动后 8 秒首次，之后每 60 秒一次（自动检测保持静默，仅发现新版本时才提示）
                updateTimer = new System.Windows.Forms.Timer();
                updateTimer.Interval = 60000;
                updateTimer.Tick += delegate { CheckUpdate(false); };
                updateTimer.Start();
                SetTimeout(8000, delegate { CheckUpdate(false); });
                SetTimeout(1500, ReportUpdateResult);

                // 全局热键（Ctrl+F2 / Ctrl+Alt+O / Ctrl+Alt+G）：句柄创建后注册，见 OnHandleCreated
                hotkeys = new HotkeyManager(this, OnHotkey);
                LoadCloseAction();                       // 关闭行为（询问 / 最小化 / 直接退出）

                // Live2D 功能已移除，不再初始化角色窗口

                // 命令行/右键传人的待打开文件（界面 ready 后逐个打开）
                foreach (string f in Program.StartupFiles)
                {
                    string p = (f ?? "").Trim().Trim('"');
                    if (p.Length > 0) _pendingOpen.Add(p);
                }
            }
        }

        // ============================================================ 单实例转发（第二个进程 → 主窗口）
        /// <summary>管道线程收到消息后切回 UI 线程处理。</summary>
        internal static void ForwardToMain(List<string> msgs)
        {
            MainForm f = _mainWindow;
            if (f == null || f.IsDisposed) return;
            try
            {
                f.BeginInvoke((Action)delegate { f.HandleIpc(msgs); });
            }
            catch { }
        }

        private void HandleIpc(List<string> msgs)
        {
            bool activate = false;
            foreach (string m in msgs)
            {
                if (m == "__ACTIVATE__") { activate = true; continue; }
                if (File.Exists(m)) { OpenPath(m); activate = true; }
            }
            if (activate) RestoreWindow();
        }

        // ============================================================ 全局热键
        protected override void WndProc(ref Message m)
        {
            if (hotkeys != null && hotkeys.HandleMessage(ref m)) return;
            // 鼠标落在留出的边上时返回 HTLEFT/HTBOTTOM… 让系统接管缩放（含四角、最小尺寸约束、Aero 贴靠）
            if (m.Msg == WM_NCHITTEST)
            {
                base.WndProc(ref m);
                if ((int)m.Result == HTCLIENT)
                {
                    int lp = m.LParam.ToInt32();
                    Point p = PointToClient(new Point((short)(lp & 0xFFFF), (short)((lp >> 16) & 0xFFFF)));
                    int ht = HitTestEdge(p);
                    if (ht != 0) m.Result = (IntPtr)ht;
                }
                return;
            }
            base.WndProc(ref m);
        }

        // ============================================================ 鼠标拖拽缩放 + 位置大小记忆
        /// <summary>按当前 DPI 计算拖拽边宽；最大化时不留边（铺满屏幕），其余状态恢复。</summary>
        private void ApplyResizeBand()
        {
            int dpi = DeviceDpi > 0 ? DeviceDpi : 96;
            resizeBand = Math.Max(4, (int)Math.Round(6.0 * dpi / 96.0));
            int want = (WindowState == FormWindowState.Normal) ? resizeBand : 0;
            if (bandState == want) return;
            bandState = want;
            Padding = new Padding(want);
        }

        /// <summary>客户区坐标 → 对应的 HT* 代码；不在边上返回 0。</summary>
        private int HitTestEdge(Point p)
        {
            if (WindowState != FormWindowState.Normal) return 0;
            int b = resizeBand, w = ClientSize.Width, h = ClientSize.Height;
            if (w <= 0 || h <= 0) return 0;
            bool l = p.X < b, r = p.X >= w - b, t = p.Y < b, bo = p.Y >= h - b;
            if (t && l) return HTTOPLEFT;
            if (t && r) return HTTOPRIGHT;
            if (bo && l) return HTBOTTOMLEFT;
            if (bo && r) return HTBOTTOMRIGHT;
            if (l) return HTLEFT;
            if (r) return HTRIGHT;
            if (t) return HTTOP;
            if (bo) return HTBOTTOM;
            return 0;
        }

        protected override void OnDpiChanged(DpiChangedEventArgs e)
        {
            base.OnDpiChanged(e);
            bandState = -1;              // 换屏后 DPI 变了，重新计算边宽
            ApplyResizeBand();
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (WindowState != FormWindowState.Minimized) ApplyResizeBand();
        }

        /// <summary>拖拽边与 HTML 主体同色，否则深色主题下会露一圈浅色边框。</summary>
        private void ApplyThemeColor(string name)
        {
            try { BackColor = (name == "dark") ? Color.FromArgb(32, 32, 32) : Color.FromArgb(243, 243, 243); }
            catch { }
        }

        private static string WindowStatePath
        {
            get
            {
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RichTextGen");
                try { Directory.CreateDirectory(dir); } catch { }
                return Path.Combine(dir, "window.json");
            }
        }

        private static int IntOf(string json, string key)
        {
            Match m = Regex.Match(json ?? "", "\"" + key + "\"\\s*:\\s*(-?\\d+)");
            int v;
            return (m.Success && int.TryParse(m.Groups[1].Value, out v)) ? v : 0;
        }

        private void LoadWindowState()
        {
            try
            {
                string path = WindowStatePath;
                if (!File.Exists(path)) return;
                string t = File.ReadAllText(path, Encoding.UTF8);
                int w = IntOf(t, "width"), h = IntOf(t, "height");
                if (w >= MinimumSize.Width && h >= MinimumSize.Height)
                {
                    Rectangle r = new Rectangle(IntOf(t, "left"), IntOf(t, "top"), w, h);
                    if (IsOnScreen(r)) { StartPosition = FormStartPosition.Manual; Bounds = r; _normBounds = r; }
                }
                if (Regex.IsMatch(t, "\"maximized\"\\s*:\\s*true")) WindowState = FormWindowState.Maximized;
            }
            catch { }
        }

        /// <summary>记忆的位置至少要有一部分落在某个屏幕工作区内，否则丢弃（显示器拔了/分辨率变了）。</summary>
        private static bool IsOnScreen(Rectangle r)
        {
            foreach (Screen s in Screen.AllScreens)
            {
                Rectangle i = Rectangle.Intersect(s.WorkingArea, r);
                if (i.Width >= 120 && i.Height >= 40) return true;
            }
            return false;
        }

        private void SaveWindowState()
        {
            try
            {
                // 最大化时用自己记下的「最大化前位置」，不能用 RestoreBounds：
                // 动画最大化后 RestoreBounds 会被记成动画终点，保存下来窗口会越来越大。
                Rectangle r = WindowState == FormWindowState.Normal ? Bounds : _normBounds;
                if (r.Width < 200 || r.Height < 150) r = RestoreBounds;
                if (r.Width < 200 || r.Height < 150) return;   // 最小化时 RestoreBounds 可能为空，跳过
                File.WriteAllText(WindowStatePath,
                    "{" + Quote("left") + ":" + r.Left + "," + Quote("top") + ":" + r.Top + "," +
                          Quote("width") + ":" + r.Width + "," + Quote("height") + ":" + r.Height + "," +
                          Quote("maximized") + ":" + (WindowState == FormWindowState.Maximized ? "true" : "false") + "}",
                    Encoding.UTF8);
            }
            catch { }
        }

        protected override void OnResizeEnd(EventArgs e)
        {
            base.OnResizeEnd(e);
            if (!_secondary) SaveWindowState();          // 鼠标拖完大小/移动到位就落盘
            if (WindowState == FormWindowState.Normal) _normBounds = Bounds;
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            // 副窗口：关闭就是真关闭（不缩托盘、不覆盖主窗口的位置记忆）
            if (_secondary)
            {
                if (_dirtyCount > 0 && !_allowClose)
                {
                    DialogResult ans = MessageBox.Show(this,
                        "这个窗口里有 " + _dirtyCount + " 个标签页尚未保存，关闭后会丢失更改。\r\n\r\n仍要关闭吗？",
                        "确认关闭", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                    if (ans != DialogResult.Yes) { e.Cancel = true; Opacity = 1.0; return; }
                }
                base.OnFormClosing(e);
                return;
            }
            SaveWindowState();
            // 未明确退出时：按「关闭行为」设置处理（询问 / 最小化 / 直接退出）
            if (!_allowClose)
            {
                e.Cancel = true;
                RequestClose();
                return;
            }
            base.OnFormClosing(e);
        }

        // ============================================================ 关闭行为（询问 / 最小化 / 直接退出）
        /// <summary>ask = 每次询问；minimize = 最小化到托盘；exit = 直接退出。</summary>
        private string _closeAction = "ask";

        private static string CloseCfgPath
        {
            get { return Path.Combine(AiConfig.Dir, "close.json"); }
        }

        private void LoadCloseAction()
        {
            try
            {
                if (!File.Exists(CloseCfgPath)) return;
                string s = File.ReadAllText(CloseCfgPath, Encoding.UTF8);
                if (s.IndexOf("\"exit\"", StringComparison.Ordinal) >= 0) _closeAction = "exit";
                else if (s.IndexOf("\"minimize\"", StringComparison.Ordinal) >= 0) _closeAction = "minimize";
                else _closeAction = "ask";
            }
            catch { }
        }

        private void SaveCloseAction()
        {
            try
            {
                Directory.CreateDirectory(AiConfig.Dir);
                File.WriteAllText(CloseCfgPath, "{\"action\":\"" + _closeAction + "\"}", new UTF8Encoding(false));
            }
            catch { }
        }

        /// <summary>点关闭/Alt+F4 时：按设置决定询问、最小化还是真退出。</summary>
        private void RequestClose()
        {
            string a = string.IsNullOrEmpty(_closeAction) ? "ask" : _closeAction;
            if (a == "exit") { _allowClose = true; Close(); return; }
            if (a == "minimize") { HideWindow(); return; }

            using (CloseChoiceForm f = new CloseChoiceForm(IsDarkTheme()))
            {
                f.ShowDialog(this);
                if (f.Cancelled) return;
                if (f.Remember)
                {
                    _closeAction = f.Minimize ? "minimize" : "exit";
                    SaveCloseAction();
                    try { CallStr("closeAction", _closeAction); } catch { }
                }
                if (f.Minimize) HideWindow();
                else { _allowClose = true; Close(); }
            }
        }

        private static bool IsDarkTheme()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    object v = k != null ? k.GetValue("AppsUseLightTheme") : null;
                    return (v is int) && ((int)v == 0);
                }
            }
            catch { return false; }
        }

        /// <summary>快捷组件里点「执行」时调用（与热键触发走同一套动作）。</summary>
        private void RunQuickAction(string action)
        {
            switch ((action ?? "").Trim().ToLowerInvariant())
            {
                case "quit": _allowClose = true; Close(); break;
                case "minimize": break;   // 最小化功能已移除（会导致窗口无法正常显示）
                case "theme": RunJs("toggleTheme()"); break;
                case "hide": if (Visible) HideWindow(); else RestoreWindow(); break;
                case "music": MusicNext(); break;
            }
        }

        private void OnHotkey(string action)
        {
            switch (action)
            {
                // Ctrl+F2：隐藏 ↔ 显示 双向切换（收后台用托盘唤回 / 再按一次直接恢复）
                case "hide":
                    if (Visible) HideWindow();
                    else RestoreWindow();
                    break;
                case "import": ImportTxt(); HideWindow(); break;        // 导入完自动收后台
                case "generate": RunJs("gen()"); HideWindow(); break;   // 重新生成完自动收后台
                case "quit": _allowClose = true; Close(); break;        // 快捷组件：退出应用
                case "minimize": break;   // 最小化功能已移除（会导致窗口无法正常显示）             // 快捷组件：最小化
                case "theme": RunJs("toggleTheme()"); break;            // 快捷组件：切换深浅色
                case "music": MusicNext(); break;                       // 切换下一首音乐
            }
        }

        /// <summary>隐藏窗口到后台。托盘图标（双击或右键「显示窗口」）可唤回。</summary>
        private void HideWindow()
        {
            if (!Visible) return;
            if (!_secondary) SaveWindowState();      // 隐藏前落盘（隐藏不是 Close，不会走 OnFormClosing）
            // 先淡出再隐藏，避免窗口「啪」地消失
            if (_winAnim == null && Opacity > 0.02)
            {
                Rectangle cur = Bounds;
                AnimateWindow(cur, cur, Opacity, 0.0, 140, delegate
                {
                    Hide();
                    Opacity = 1.0;
                    AfterHidden();
                });
                return;
            }
            Hide();
            Opacity = 1.0;
            AfterHidden();
        }

        private void AfterHidden()
        {
            EnsureTray();
            // 首次隐藏时提示一次，避免用户以为程序被关掉了
            if (!trayTipShown && tray != null)
            {
                trayTipShown = true;
                try
                {
                    tray.BalloonTipTitle = "彩色文本生成器仍在后台运行";
                    tray.BalloonTipText = "单击托盘图标即可唤回窗口。退出请右键托盘图标 →「退出」。";
                    tray.ShowBalloonTip(4000);
                }
                catch { }
            }
        }

        private void RestoreWindow()
        {
            if (!Visible) { Opacity = 0.0; Show(); }
            WindowState = FormWindowState.Normal;
            Activate();
            BringToFront();
            if (Opacity < 0.99) AnimateWindow(Bounds, Bounds, Opacity, 1.0, 140, null);
        }

        // ============================================================ 窗口动效（最小化 / 最大化 / 关闭）
        private System.Windows.Forms.Timer _winAnim;
        /// <summary>最大化之前的窗口位置（动画最大化后系统 RestoreBounds 会记成动画终点，必须自己记）。</summary>
        private Rectangle _normBounds = Rectangle.Empty;

        /// <summary>把窗口从 from 平滑过渡到 to，同时可带透明度变化；结束执行 done。</summary>
        private void AnimateWindow(Rectangle from, Rectangle to, double fromO, double toO, int ms, Action done)
        {
            try { if (_winAnim != null) { _winAnim.Stop(); _winAnim.Dispose(); _winAnim = null; } } catch { }
            int steps = Math.Max(1, ms / 12);
            int step = 0;
            _winAnim = new System.Windows.Forms.Timer();
            _winAnim.Interval = 12;
            _winAnim.Tick += delegate
            {
                step++;
                double t = (double)step / steps; if (t > 1) t = 1;
                double e = 1 - Math.Pow(1 - t, 3);   // easeOutCubic
                try
                {
                    int l = (int)Math.Round(from.Left + (to.Left - from.Left) * e);
                    int tp = (int)Math.Round(from.Top + (to.Top - from.Top) * e);
                    int w = (int)Math.Round(from.Width + (to.Width - from.Width) * e);
                    int h = (int)Math.Round(from.Height + (to.Height - from.Height) * e);
                    Bounds = new Rectangle(l, tp, Math.Max(80, w), Math.Max(60, h));
                    if (Math.Abs(fromO - toO) > 0.001) Opacity = fromO + (toO - fromO) * e;
                }
                catch { }
                if (step >= steps)
                {
                    try { _winAnim.Stop(); _winAnim.Dispose(); } catch { }
                    _winAnim = null;
                    if (done != null) done();
                }
            };
            _winAnim.Start();
        }

        /// <summary>最小化：向任务栏方向缩小并淡出，再真正最小化。</summary>
        private void MinimizeAnimated()
        {
            if (_winAnim != null) return;
            Rectangle cur = Bounds;
            Rectangle wa = Screen.FromHandle(Handle).WorkingArea;
            Rectangle to = new Rectangle(wa.Left + wa.Width / 2 - 60, wa.Bottom - 50, 120, 80);
            Size oldMin = MinimumSize;
            MinimumSize = new Size(1, 1);          // 临时解除最小尺寸限制，否则缩不下去
            AnimateWindow(cur, to, 1.0, 0.0, 170, delegate
            {
                WindowState = FormWindowState.Minimized;
                Opacity = 1.0;
                Bounds = cur;
                MinimumSize = oldMin;
            });
        }

        /// <summary>最大化 / 还原：用 190ms 缓动过渡，代替瞬间跳变。</summary>
        private void ToggleMaximizeAnimated()
        {
            if (_winAnim != null) return;
            if (WindowState == FormWindowState.Maximized)
            {
                Rectangle target = _normBounds;
                if (target.Width < MinimumSize.Width || target.Height < MinimumSize.Height) target = RestoreBounds;
                if (target.Width < MinimumSize.Width || target.Height < MinimumSize.Height)
                    target = new Rectangle(120, 80, 1180, 800);
                Rectangle cur = Bounds;
                WindowState = FormWindowState.Normal;   // 先退出最大化
                Bounds = cur;                           // 仍从最大化时的位置开始动画
                AnimateWindow(cur, target, 1.0, 1.0, 190, delegate { Bounds = target; SaveWindowState(); });
            }
            else
            {
                Rectangle cur = Bounds;
                _normBounds = cur;                      // 记住还原目标
                Rectangle target = Screen.FromHandle(Handle).WorkingArea;
                AnimateWindow(cur, target, 1.0, 1.0, 190, delegate
                {
                    Bounds = target;
                    WindowState = FormWindowState.Maximized;
                    SaveWindowState();
                });
            }
        }

        /// <summary>关闭（副窗口）：先淡出，再走正常关闭流程（未保存会弹确认）。</summary>
        private void CloseAnimated()
        {
            if (_winAnim != null) { Close(); return; }
            Rectangle cur = Bounds;
            AnimateWindow(cur, cur, 1.0, 0.0, 140, delegate { Close(); });
        }

        // ============================================================ 启动提示
        private static string TipStatePath
        {
            get { return Path.Combine(AiConfig.Dir, "welcome.json"); }
        }

        private static bool TipDismissed()
        {
            try
            {
                if (!File.Exists(TipStatePath)) return false;
                string s = File.ReadAllText(TipStatePath, Encoding.UTF8);
                return Regex.IsMatch(s, "\"dontShow\"\\s*:\\s*true", RegexOptions.IgnoreCase);
            }
            catch { return false; }
        }

        /// <summary>启动后弹一次使用提示（勾选「不再提示」后不再出现）。</summary>
        private void ShowStartupTip()
        {
            if (_secondary || TipDismissed()) return;
            bool dark = false;
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    object v = k != null ? k.GetValue("AppsUseLightTheme") : null;
                    dark = (v is int) && ((int)v == 0);
                }
            }
            catch { }
            try
            {
                using (StartupTipForm f = new StartupTipForm(dark))
                {
                    f.ShowDialog(this);
                    if (f.DontShowAgain)
                    {
                        Directory.CreateDirectory(AiConfig.Dir);
                        File.WriteAllText(TipStatePath, "{\"dontShow\":true}", new UTF8Encoding(false));
                    }
                }
            }
            catch { }
        }

        /// <summary>托盘图标：Ctrl+F2 隐藏后唯一的唤回入口，同时提供退出</summary>
        private void EnsureTray()
        {
            if (tray != null) return;
            try
            {
                tray = new NotifyIcon();
                tray.Icon = Icon != null ? Icon : SystemIcons.Application;
                tray.Text = "彩色文本生成器 v" + Version;
                ContextMenuStrip menu = new ContextMenuStrip();
                menu.Items.Add("显示窗口", null, delegate { RestoreWindow(); });
                menu.Items.Add("强制应用刷新", null, delegate { ForceRefreshApp(); });
                menu.Items.Add("立即检测更新", null, delegate { CheckUpdate(true); });
                menu.Items.Add(new ToolStripSeparator());
                menu.Items.Add("退出", null, delegate { _allowClose = true; Close(); });
                tray.ContextMenuStrip = menu;
                tray.MouseClick += delegate(object s, MouseEventArgs me)
                {
                    if (me.Button == MouseButtons.Left) RestoreWindow();   // 单击图标即展开
                };
                tray.DoubleClick += delegate { RestoreWindow(); };
                tray.Visible = true;
            }
            catch { tray = null; }
        }

        /// <summary>托盘「强制应用刷新」：先让界面把全部正文打包，再重载页面并恢复窗口。</summary>
        private void ForceRefreshApp()
        {
            if (_refreshing) return;
            _refreshing = true;
            try { RestoreWindow(); } catch { }
            _refreshFallback = true;
            RunJs("forceRefresh()");
            // 界面脚本卡死时也照样刷新（只是没有正文快照）
            SetTimeout(1600, delegate { if (_refreshFallback) DoReload(null); });
        }

        /// <summary>把快照临时落盘，然后重新请求内嵌界面（等同于一次干净的页面刷新）。</summary>
        private void DoReload(string snapshot)
        {
            _refreshFallback = false;
            if (!string.IsNullOrEmpty(snapshot))
            {
                _pendingRefresh = snapshot;
                try
                {
                    Directory.CreateDirectory(AiConfig.Dir);
                    File.WriteAllText(Path.Combine(AiConfig.Dir, "refresh-snapshot.json"),
                                      snapshot, new UTF8Encoding(false));
                }
                catch { }
            }
            else
            {
                try { RunJs("window.__rtgForceRefresh=true"); } catch { }
            }
            try
            {
                if (web != null && web.CoreWebView2 != null)
                    web.CoreWebView2.Navigate(WebOrigin + "?refresh=" + DateTime.Now.Ticks);
            }
            catch { }
        }

        /// <summary>执行页面里的全局函数（热键触发 UI 侧动作）</summary>
        private void RunJs(string script)
        {
            try
            {
                if (web == null) return;
                if (InvokeRequired) { BeginInvoke((Action)delegate { RunJs(script); }); return; }
                if (web.CoreWebView2 == null) return;
                web.CoreWebView2.ExecuteScriptAsync(script);
            }
            catch { }
        }

        private void SetTimeout(int ms, Action act)
        {
            System.Windows.Forms.Timer t = new System.Windows.Forms.Timer();
            t.Interval = ms;
            t.Tick += delegate { t.Stop(); t.Dispose(); try { act(); } catch { } };
            t.Start();
        }

        /// <summary>更新完成后：读取安装前写下的标记 → 弹"成功更新 + 更新内容"</summary>
        private void ReportUpdateResult()
        {
            try
            {
                string mark = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RichTextGen", "pending_update.json");
                if (!File.Exists(mark)) return;
                string txt = File.ReadAllText(mark, Encoding.UTF8);
                string v = Field(txt, "version"), n = Field(txt, "notes"), t = Field(txt, "type"), old = Field(txt, "from");
                File.Delete(mark);
                Call("updated", "{\"version\":" + Quote(v) + ",\"from\":" + Quote(old) + ",\"notes\":" + Quote(n) + ",\"type\":" + Quote(t) + "}");
            }
            catch { }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            try { int round = 2; DwmSetWindowAttribute(Handle, 33, ref round, 4); } catch { }
            try { if (hotkeys != null) hotkeys.RegisterAll(); } catch { }
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            // 释放全局热键与计时器，避免残留热键占用（否则重启程序会「注册失败（被占用）」）
            try { if (_winAnim != null) { _winAnim.Stop(); _winAnim.Dispose(); _winAnim = null; } } catch { }
            try { if (hotkeys != null) hotkeys.UnregisterAll(); } catch { }
            if (!_secondary) { try { Live2D.Shutdown(); } catch { } }
            try { if (updateTimer != null) { updateTimer.Stop(); updateTimer.Dispose(); } } catch { }
            try { if (_mediaListener != null) { _mediaListener.Stop(); _mediaListener = null; } } catch { }   // 关掉音乐本地服务
            base.OnFormClosed(e);
        }

        /// <summary>主窗口的 WebView2 环境，供独立对话窗口复用（同一进程只能有一个用户数据目录环境）。</summary>
        internal static CoreWebView2Environment WebEnv;

        private static Task<CoreWebView2Environment> _envTask;

        /// <summary>初始化 WebView2：副窗口复用已有环境，主窗口按「GPU 加速」偏好创建一次环境。</summary>
        private async void PrepareWeb()
        {
            try
            {
                if (_secondary && WebEnv != null) { await web.EnsureCoreWebView2Async(WebEnv); return; }
                CoreWebView2Environment env = await GetEnvironmentAsync();
                if (env != null) await web.EnsureCoreWebView2Async(env);
                else await web.EnsureCoreWebView2Async(null);
            }
            catch { }
        }

        /// <summary>
        /// 创建带渲染参数的环境。GPU 加速默认开启：
        /// 光栅化 / 零拷贝 / 忽略黑名单，让界面滚动、动效与 Live2D 走硬件合成。
        /// 若显卡驱动异常导致花屏，可在 设置 → 性能 里关闭后重启。
        /// </summary>
        private static Task<CoreWebView2Environment> GetEnvironmentAsync()
        {
            if (_envTask != null) return _envTask;
            _envTask = Task.Run(async delegate
            {
                try
                {
                    string udf = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                                              "RichTextGen", "WebView2");
                    Directory.CreateDirectory(udf);
                    string args = UIPrefs.Gpu
                        ? "--enable-gpu-rasterization --enable-zero-copy --ignore-gpu-blocklist --enable-features=CanvasOopRasterization"
                        : "--disable-gpu --disable-gpu-compositing";
                    CoreWebView2EnvironmentOptions opt = new CoreWebView2EnvironmentOptions(args);
                    return await CoreWebView2Environment.CreateAsync(null, udf, opt);
                }
                catch
                {
                    return null;   // 退回到 WebView2 默认环境，保证仍能启动
                }
            });
            return _envTask;
        }

        /// <summary>界面偏好（与前端 localStorage 同步的一份落盘副本，C# 启动时需要读 GPU 开关）。</summary>
        internal static class UIPrefs
        {
            private static string FilePath
            {
                get
                {
                    string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RichTextGen");
                    Directory.CreateDirectory(dir);
                    return Path.Combine(dir, "ui.json");
                }
            }

            public static bool Gpu { get { return ReadBool("gpu", true); } }
            public static bool Topmost { get { return ReadBool("topmost", false); } }
            public static bool SyncOut { get { return ReadBool("syncOut", false); } }
            public static bool CopyNl { get { return ReadBool("copyNl", false); } }
            public static bool Floating { get { return ReadBool("floating", false); } }

            private static readonly string[] Keys = { "gpu", "topmost", "syncOut", "copyNl", "floating" };

            private static bool ReadBool(string key, bool def)
            {
                try
                {
                    string p = FilePath;
                    if (!File.Exists(p)) return def;
                    Match m = Regex.Match(File.ReadAllText(p),
                        "\"" + key + "\"\\s*:\\s*(true|false)", RegexOptions.IgnoreCase);
                    if (!m.Success) return def;
                    return m.Groups[1].Value.Equals("true", StringComparison.OrdinalIgnoreCase);
                }
                catch { return def; }
            }

            /// <summary>只覆盖 json 里出现的键，其余保持原值（前端每次只报变化的那一项）。</summary>
            public static void Save(string json)
            {
                try
                {
                    bool changed = false;
                    Dictionary<string, bool> vals = new Dictionary<string, bool>();
                    foreach (string k in Keys) vals[k] = ReadBool(k, k == "gpu");

                    foreach (string k in Keys)
                    {
                        Match m = Regex.Match(json ?? "", "\"" + k + "\"\\s*:\\s*(true|false)", RegexOptions.IgnoreCase);
                        if (!m.Success) continue;
                        vals[k] = m.Groups[1].Value.Equals("true", StringComparison.OrdinalIgnoreCase);
                        changed = true;
                    }
                    if (!changed) return;

                    StringBuilder sb = new StringBuilder("{");
                    bool first = true;
                    foreach (string k in Keys)
                    {
                        if (!first) sb.Append(',');
                        first = false;
                        sb.Append('"').Append(k).Append("\":").Append(vals[k] ? "true" : "false");
                    }
                    sb.Append('}');
                    File.WriteAllText(FilePath, sb.ToString(), new UTF8Encoding(false));
                }
                catch { }
            }
        }

        private void OnWebReady(object sender, CoreWebView2InitializationCompletedEventArgs e)
        {
            if (!e.IsSuccess || web.CoreWebView2 == null)
            {
                string why = e.InitializationException != null ? e.InitializationException.Message : "未知原因";
                MessageBox.Show(this,
                    "WebView2 组件初始化失败，界面无法显示。\r\n\r\n原因：" + why +
                    "\r\n\r\n请确认程序目录下这三个文件完整：\r\n  Microsoft.Web.WebView2.Core.dll\r\n  Microsoft.Web.WebView2.WinForms.dll\r\n  WebView2Loader.dll" +
                    "\r\n\r\n若仍失败，请安装「Microsoft Edge WebView2 运行时」后重试。",
                    "启动失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
                _allowClose = true;                      // 启动失败必须真退出，不能缩托盘
                Close();
                return;
            }
            web.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            web.CoreWebView2.Settings.IsStatusBarEnabled = false;
            try { WebEnv = web.CoreWebView2.Environment; } catch { }
            web.CoreWebView2.WebMessageReceived += OnMessage;

            // 用一个虚拟 https 源承载界面，而不是 NavigateToString。
            // data:/about: 这类不透明源会被浏览器禁止访问 localStorage，
            // 导致主题、背景图、侧栏宽度等「记住设置」全部静默失效。
            // 这里拦截该源的请求，把内嵌 HTML 作为响应返回，即可获得可持久化的正常源。
            const string origin = WebOrigin;
            try
            {
                web.CoreWebView2.AddWebResourceRequestedFilter(origin + "*", CoreWebView2WebResourceContext.All);
                web.CoreWebView2.WebResourceRequested += delegate (object s2, CoreWebView2WebResourceRequestedEventArgs e2)
                {
                    try
                    {
                        if (!e2.Request.Uri.StartsWith(origin, StringComparison.OrdinalIgnoreCase)) return;

                        // Live2D 模型图片：https://richtextgen.local/__l2d/<模型目录>/<文件名>
                        string abs = new Uri(e2.Request.Uri).AbsolutePath;
                        if (abs.StartsWith(Live2D.UrlPrefix, StringComparison.OrdinalIgnoreCase))
                        {
                            string[] seg = abs.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
                            string file = null;
                            if (seg.Length >= 3)
                            {
                                // 支持子目录：/__l2d/<模型>/textures/xxx.png
                                StringBuilder rel = new StringBuilder();
                                for (int si = 2; si < seg.Length; si++)
                                {
                                    if (rel.Length > 0) rel.Append('/');
                                    rel.Append(Uri.UnescapeDataString(seg[si]));
                                }
                                file = Live2D.ResolveFile(Uri.UnescapeDataString(seg[1]), rel.ToString());
                            }
                            if (file != null)
                            {
                                byte[] raw = File.ReadAllBytes(file);
                                e2.Response = web.CoreWebView2.Environment.CreateWebResourceResponse(
                                    new MemoryStream(raw), 200, "OK",
                                    "Content-Type: " + MimeOf(file) + "\r\nCache-Control: no-cache");
                            }
                            else
                            {
                                e2.Response = web.CoreWebView2.Environment.CreateWebResourceResponse(
                                    new MemoryStream(new byte[0]), 404, "Not Found", "Content-Type: text/plain");
                            }
                            return;
                        }

                        // 背景图片 / 视频：https://richtextgen.local/__bg/<dark|light>
                        // 视频用 <video> 播放，必须支持 Range，否则无法循环 / 拖动进度。
                        if (abs.StartsWith("/__bg/", StringComparison.OrdinalIgnoreCase))
                        {
                            string bk = abs.Substring("/__bg/".Length).Trim('/');
                            bk = (bk == "light") ? "light" : "dark";
                            string bf = BgFile(bk);
                            if (bf == null || bf.Length == 0 || !File.Exists(bf))
                            {
                                e2.Response = web.CoreWebView2.Environment.CreateWebResourceResponse(
                                    new MemoryStream(new byte[0]), 404, "Not Found", "Content-Type: text/plain");
                                return;
                            }
                            ServeMedia(e2, bf);
                            return;
                        }

                        // 音乐播放列表：https://richtextgen.local/__music/<序号>
                        if (abs.StartsWith("/__music/", StringComparison.OrdinalIgnoreCase))
                        {
                            string tail = abs.Substring("/__music/".Length).Trim('/');
                            int mi;
                            if (int.TryParse(tail, out mi) && mi >= 0 && mi < _playlist.Count &&
                                File.Exists(_playlist[mi]))
                            {
                                ServeMedia(e2, _playlist[mi]);
                            }
                            else
                            {
                                e2.Response = web.CoreWebView2.Environment.CreateWebResourceResponse(
                                    new MemoryStream(new byte[0]), 404, "Not Found", "Content-Type: text/plain");
                            }
                            return;
                        }

                        byte[] body = Encoding.UTF8.GetBytes(LoadHtml());
                        e2.Response = web.CoreWebView2.Environment.CreateWebResourceResponse(
                            new MemoryStream(body), 200, "OK", "Content-Type: text/html; charset=utf-8");
                    }
                    catch { }
                };
                web.CoreWebView2.Navigate(origin);
            }
            catch
            {
                web.CoreWebView2.NavigateToString(LoadHtml());   // 兜底：极旧运行时走原方案
            }
        }

        private static string LoadHtml()
        {
            try
            {
                Assembly asm = Assembly.GetExecutingAssembly();
                foreach (string n in asm.GetManifestResourceNames())
                {
                    if (n.EndsWith("index.html", StringComparison.OrdinalIgnoreCase))
                        using (Stream s = asm.GetManifestResourceStream(n))
                        using (StreamReader sr = new StreamReader(s, Encoding.UTF8))
                            return sr.ReadToEnd();
                }
            }
            catch { }
            return "<html><body style='font-family:Microsoft YaHei'>界面资源缺失</body></html>";
        }

        internal static string LoadChatHtml()
        {
            try
            {
                Assembly asm = Assembly.GetExecutingAssembly();
                foreach (string n in asm.GetManifestResourceNames())
                {
                    if (n.EndsWith("chat.html", StringComparison.OrdinalIgnoreCase))
                        using (Stream s = asm.GetManifestResourceStream(n))
                        using (StreamReader sr = new StreamReader(s, Encoding.UTF8))
                            return sr.ReadToEnd();
                }
            }
            catch { }
            return "<html><body style='font-family:Microsoft YaHei'>chat.html 资源缺失</body></html>";
        }

        // ============================================================ 桥
        private void OnMessage(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            string raw = "";
            try { raw = e.TryGetWebMessageAsString(); } catch { }
            if (string.IsNullOrEmpty(raw)) return;
            int bar = raw.IndexOf('|');
            string cmd = bar < 0 ? raw : raw.Substring(0, bar);
            string arg = bar < 0 ? "" : raw.Substring(bar + 1);
            try { Dispatch(cmd, arg); }
            catch (Exception ex) { Call("toast", "出错了：" + ex.Message); }
        }

        /// <summary>调用 JS；自动切回 UI 线程（WebView2 不允许后台线程直接调用）</summary>
        private void Call(string fn, string jsonArg)
        {
            if (web == null) return;
            try
            {
                if (InvokeRequired) { BeginInvoke((Action)delegate { Call(fn, jsonArg); }); return; }
                if (web.CoreWebView2 == null) return;
                web.CoreWebView2.ExecuteScriptAsync("ui." + fn + "(" + jsonArg + ")");
            }
            catch (Exception ex) { LogOnce("界面回调失败（ui." + fn + "）：" + ex.Message); }
        }
        private void CallStr(string fn, string s) { Call(fn, Quote(s)); }

        /* ================= 外部主题文件（程序目录 theme\theme.txt） =================
           把配色/字体/缩放开放给用户：安装后程序目录下会有 theme\theme.txt，
           用户改完保存，界面点「重新加载主题」即可生效，不需要重新打包。 */

        /// <summary>主题目录：优先程序目录；不可写时退回 %APPDATA%\RichTextGen\theme</summary>
        private static string ThemeDir()
        {
            string dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "theme");
            try
            {
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                return dir;
            }
            catch
            {
                dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RichTextGen", "theme");
                try { if (!Directory.Exists(dir)) Directory.CreateDirectory(dir); } catch { }
                return dir;
            }
        }

        private static string ThemeFilePath() { return Path.Combine(ThemeDir(), "theme.txt"); }

        /// <summary>默认模板：用户第一次打开就能照着改</summary>
        private const string ThemeTemplate =
            "# 彩色文本生成器 · 外观主题\r\n" +
            "# 用法：只改等号右边；保存后到「设置 → 外观 · 字体与缩放」点「重新加载主题」。\r\n" +
            "# 以 # 开头的整行是注释；值请写到行尾，不要加行内注释。\r\n" +
            "\r\n" +
            "# ---- 亮色配色 ----\r\n" +
            "accent=#0067C0\r\n" +
            "bg=#F3F3F3\r\n" +
            "panel=#FFFFFF\r\n" +
            "input=#FFFFFF\r\n" +
            "text=#1B1B1B\r\n" +
            "sub=#5D5D5D\r\n" +
            "line=#E5E5E5\r\n" +
            "hover=#F0F0F0\r\n" +
            "\r\n" +
            "# ---- 深色配色（不填则沿用内置深色）----\r\n" +
            "dark.accent=#4CC2FF\r\n" +
            "dark.bg=#202020\r\n" +
            "dark.panel=#2B2B2B\r\n" +
            "dark.input=#262626\r\n" +
            "dark.text=#FFFFFF\r\n" +
            "dark.sub=#C5C5C5\r\n" +
            "dark.line=#3A3A3A\r\n" +
            "dark.hover=#333333\r\n" +
            "\r\n" +
            "# ---- 字体与缩放 ----\r\n" +
            "# ui.font 可填任意已安装字体名；ui.scale 取 0.8 ~ 1.6\r\n" +
            "ui.font=\r\n" +
            "ui.size=14\r\n" +
            "ui.scale=1\r\n" +
            "editor.font=\r\n" +
            "editor.size=13.5\r\n";

        /// <summary>读主题文件并转成 {path, values:{k:v}} 交给界面</summary>
        private static string ThemeJson()
        {
            string path = ThemeFilePath();
            string text = "";
            try
            {
                if (!File.Exists(path)) File.WriteAllText(path, ThemeTemplate, new UTF8Encoding(false));
                text = File.ReadAllText(path, Encoding.UTF8);
            }
            catch { }

            StringBuilder sb = new StringBuilder();
            sb.Append("{\"path\":").Append(Quote(path)).Append(",\"values\":{");
            bool first = true;
            string[] lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                if (line.Length == 0 || line[0] == '#') continue;
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                string key = line.Substring(0, eq).Trim();
                string val = line.Substring(eq + 1).Trim();
                if (key.Length == 0 || val.Length == 0) continue;
                if (!first) sb.Append(',');
                first = false;
                sb.Append(Quote(key)).Append(':').Append(Quote(val));
            }
            sb.Append("}}");
            return sb.ToString();
        }

        /// <summary>系统已安装字体清单（供下拉框使用）</summary>
        private static string FontsJson()
        {
            List<string> names = new List<string>();
            try
            {
                using (System.Drawing.Text.InstalledFontCollection col = new System.Drawing.Text.InstalledFontCollection())
                {
                    foreach (FontFamily ff in col.Families)
                    {
                        string n = ff.Name;
                        if (!string.IsNullOrEmpty(n) && !names.Contains(n)) names.Add(n);
                    }
                }
            }
            catch { }
            names.Sort(StringComparer.CurrentCulture);
            StringBuilder sb = new StringBuilder("[");
            for (int i = 0; i < names.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(Quote(names[i]));
            }
            sb.Append("]");
            return sb.ToString();
        }

        /* ================= 语言文件（程序目录 lang\*.txt） =================
           形如 SCP:SL 的语言文件：每行「键 = 值」，# 开头是注释。
           首次运行自动生成 5 个常用语言文件；用户可自行增删改，界面随即生效。 */

        private static readonly string[] LangSeed = { "zh", "en", "es", "fr", "ru" };
        private static readonly Dictionary<string, string> LangNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "zh", "简体中文" }, { "en", "English" }, { "es", "Español" },
            { "fr", "Français" }, { "ru", "Русский" }
        };

        private static string LangDir()
        {
            string dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "lang");
            try
            {
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                return dir;
            }
            catch
            {
                dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RichTextGen", "lang");
                try { if (!Directory.Exists(dir)) Directory.CreateDirectory(dir); } catch { }
                return dir;
            }
        }

        private static string LangHeader(string code)
        {
            string nm = LangNames.ContainsKey(code) ? LangNames[code] : code;
            return "# 彩色文本生成器 · 语言文件\r\n" +
                   "# name = " + nm + "\r\n" +
                   "# 左侧是界面元素标识，请勿修改；右侧是显示文字，可以随意改。\r\n" +
                   "# 以 # 开头的整行是注释；用 \\n 表示换行，\\\\ 表示反斜杠本身；留空则回退到简体中文。\r\n" +
                   "\r\n";
        }

        /// <summary>把语言文件里的 \\n / \\\\ 还原成真实字符</summary>
        private static string LangUnescape(string s)
        {
            if (s.IndexOf('\\') < 0) return s;
            StringBuilder b = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '\\' && i + 1 < s.Length)
                {
                    char n = s[i + 1];
                    if (n == 'n') { b.Append('\n'); i++; continue; }
                    if (n == 'r') { b.Append('\r'); i++; continue; }
                    if (n == '\\') { b.Append('\\'); i++; continue; }
                }
                b.Append(c);
            }
            return b.ToString();
        }

        /// <summary>写文件前把真实换行 / 反斜杠转义成 \\n / \\\\</summary>
        private static string LangEscape(string s)
        {
            return s.Replace("\\", "\\\\").Replace("\r\n", "\n").Replace("\r", "\n").Replace("\n", "\\n");
        }

        /// <summary>内置翻译（编译进 exe），没有该语言时返回 null</summary>
        private static string BuiltinLang(string code)
        {
            try
            {
                using (Stream s = System.Reflection.Assembly.GetExecutingAssembly().GetManifestResourceStream("RichTextGen.lang." + code + ".txt"))
                {
                    if (s == null) return null;
                    using (StreamReader r = new StreamReader(s, Encoding.UTF8))
                        return r.ReadToEnd();
                }
            }
            catch { }
            return null;
        }

        /// <summary>统计语言文件里的有效条目数</summary>
        private static int LangEntryCount(string text)
        {
            int n = 0;
            foreach (string raw in text.Split('\n'))
            {
                string t = raw.Trim();
                if (t.Length == 0 || t[0] == '#') continue;
                if (t.IndexOf('=') > 0) n++;
            }
            return n;
        }

        /// <summary>确保 lang 目录存在并有 5 个初始文件（用户改过的绝不覆盖）</summary>
        private static void EnsureLangFiles()
        {
            try
            {
                string dir = LangDir();
                foreach (string code in LangSeed)
                {
                    string f = Path.Combine(dir, code + ".txt");
                    if (!File.Exists(f))
                    {
                        string builtin = BuiltinLang(code);
                        File.WriteAllText(f, builtin ?? LangHeader(code), new UTF8Encoding(false));
                        continue;
                    }
                    // 早期版本生成的空壳（只有注释头）自动补上内置翻译
                    if (code == "zh") continue;
                    string cur = null;
                    try { cur = File.ReadAllText(f, Encoding.UTF8); } catch { }
                    if (cur != null && LangEntryCount(cur) == 0)
                    {
                        string builtin = BuiltinLang(code);
                        if (!string.IsNullOrEmpty(builtin)) File.WriteAllText(f, builtin, new UTF8Encoding(false));
                    }
                }
            }
            catch { }
        }

        /// <summary>语言清单：[{file,name,count}]</summary>
        private static string LangListJson()
        {
            EnsureLangFiles();
            StringBuilder sb = new StringBuilder("[");
            bool first = true;
            try
            {
                foreach (string path in Directory.GetFiles(LangDir(), "*.txt"))
                {
                    string code = Path.GetFileNameWithoutExtension(path);
                    string name = LangNames.ContainsKey(code) ? LangNames[code] : code;
                    int count = 0;
                    foreach (string line in File.ReadAllLines(path, Encoding.UTF8))
                    {
                        string t = line.Trim();
                        if (t.Length == 0 || t[0] == '#') continue;
                        if (t.IndexOf('=') > 0) count++;
                    }
                    if (!first) sb.Append(',');
                    first = false;
                    sb.Append("{\"file\":").Append(Quote(code))
                      .Append(",\"name\":").Append(Quote(name))
                      .Append(",\"count\":").Append(count).Append('}');
                }
            }
            catch { }
            sb.Append("]");
            return sb.ToString();
        }

        /// <summary>读取某个语言文件：{file, entries:{k:v}}</summary>
        private static string LangDataJson(string code)
        {
            EnsureLangFiles();
            if (string.IsNullOrEmpty(code)) code = "zh";
            code = Regex.Replace(code, "[^A-Za-z0-9_\\-]", "");
            string path = Path.Combine(LangDir(), code + ".txt");
            StringBuilder sb = new StringBuilder();
            sb.Append("{\"file\":").Append(Quote(code)).Append(",\"entries\":{");
            bool first = true;
            try
            {
                if (!File.Exists(path)) File.WriteAllText(path, LangHeader(code), new UTF8Encoding(false));
                foreach (string line in File.ReadAllLines(path, Encoding.UTF8))
                {
                    string t = line.Trim();
                    if (t.Length == 0 || t[0] == '#') continue;
                    int eq = t.IndexOf('=');
                    if (eq <= 0) continue;
                    string k = t.Substring(0, eq).Trim();
                    string v = LangUnescape(t.Substring(eq + 1).Trim());
                    if (k.Length == 0 || v.Length == 0) continue;
                    if (!first) sb.Append(',');
                    first = false;
                    sb.Append(Quote(k)).Append(':').Append(Quote(v));
                }
            }
            catch { }
            sb.Append("}}");
            return sb.ToString();
        }

        /// <summary>保存语言文件：参数为 "code\n{entries json}"</summary>
        private void LangSave(string arg)
        {
            int nl = arg.IndexOf('\n');
            if (nl <= 0) return;
            string code = Regex.Replace(arg.Substring(0, nl).Trim(), "[^A-Za-z0-9_\\-]", "");
            string json = arg.Substring(nl + 1);
            if (code.Length == 0) return;

            // 逐条解析 {k:v}，按 UTF-8 写回（保留注释头）
            StringBuilder body = new StringBuilder();
            Match m = Regex.Match(json, "\"((?:[^\"\\\\]|\\\\.)*)\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
            while (m.Success)
            {
                string k = Regex.Unescape(m.Groups[1].Value);
                string v = Regex.Unescape(m.Groups[2].Value).Replace("\r\n", "\n").Replace("\r", "\n");
                if (k.Length > 0 && v.Length > 0)
                    body.Append(k).Append(" = ").Append(LangEscape(v)).Append("\r\n");
                m = m.NextMatch();
            }
            string path = Path.Combine(LangDir(), code + ".txt");
            try { File.WriteAllText(path, LangHeader(code) + body.ToString(), new UTF8Encoding(false)); }
            catch (Exception ex) { CallStr("status", "保存语言文件失败：" + ex.Message); }
        }

        private static bool _loggedOnce;
        /// <summary>关键异常落盘到 %APPDATA%\RichTextGen\error.log（只记第一次，避免刷屏）</summary>
        internal static void LogOnce(string message)
        {
            if (_loggedOnce) return;
            _loggedOnce = true;
            try
            {
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RichTextGen");
                Directory.CreateDirectory(dir);
                File.AppendAllText(Path.Combine(dir, "error.log"),
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + message + Environment.NewLine,
                    new UTF8Encoding(true));
            }
            catch { }
        }

        /// <summary>转义成 JSON 字符串字面量（含控制字符，避免 JSON.parse 失败导致界面静默不更新）</summary>
        private static string Quote(string s)
        {
            if (s == null) s = "";
            StringBuilder sb = new StringBuilder(s.Length + 16);
            sb.Append('"');
            for (int i = 0; i < s.Length; i++)
            {
                char ch = s[i];
                switch (ch)
                {
                    case '\\': sb.Append("\\\\"); break;
                    case '"': sb.Append("\\\""); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': break;                       // 丢弃裸 CR，统一交给 \n 表示换行
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

        private void Dispatch(string cmd, string arg)
        {
            switch (cmd)
            {
                case "ready":
                    CallStr("version", Version);
                    CallStr("status", "界面就绪 · v" + Version);
                    CallStr("net", OnlineColors.IsOnline() ? "联网正常" : "网络断开");
                    CallStr("hotkeys", hotkeys != null ? hotkeys.StatusText : "热键未初始化");
                    CallStr("hotkeyDefs", hotkeys != null ? hotkeys.HotkeysJson() : "[]");
                    // 打开命令行/右键传入的文件（多标签逐个打开）
                    foreach (string p in _pendingOpen)
                    {
                        if (File.Exists(p)) OpenPath(p);
                        else CallStr("status", "文件不存在：" + p);
                    }
                    _pendingOpen.Clear();
                    AiConfig.Load();            // 先读配置，下面的端口恢复才知道该不该启动
                    if (!_secondary)
                    {
                        CheckUpdate(false);
                        CheckShellRegistration();   // 首次启动自动注册右键菜单（HKCU 免管理员）
                    }
                    CallStr("aiSettings", AiConfig.ToJson());
                    LoadBgImages();             // 把已保存的深/浅色背景水印图读回界面
                    if (!_secondary) FloatingInputForm.SetEnabled(UIPrefs.Floating);   // 浮动输入面板（系统级，可能开着）
                    Call("syncState", SyncStateJson());   // 置顶 / 同步输出 / 换行开关的当前状态
                    PushMusicState();                   // 音乐播放列表（不自动播放）
                    Call("themeFile", ThemeJson());      // 程序目录 theme\theme.txt（没有就写一份模板）

                    // 强制刷新后：把刷新前打包的标签页正文回灌（等界面脚本全部就绪）
                    if (!string.IsNullOrEmpty(_pendingRefresh))
                    {
                        string snap = _pendingRefresh;
                        _pendingRefresh = null;
                        _refreshing = false;
                        SetTimeout(300, delegate { CallStr("refreshRestore", snap); });
                    }
                    else if (_refreshing) _refreshing = false;

                    // Live2D 功能已移除

                    // 启动提示：等界面稳定后再弹，避免和首屏渲染抢焦点
                    if (!_secondary) SetTimeout(1200, ShowStartupTip);
                    if (!_secondary) CallStr("closeAction", _closeAction);

                    // 副窗口：把撕裂过来的文档作为首个标签打开
                    if (_secondary && !string.IsNullOrEmpty(_pendingTear))
                    {
                        string j = _pendingTear.Trim();
                        if (j.IndexOf("\"ok\"", StringComparison.Ordinal) < 0 && j.EndsWith("}"))
                            j = j.Substring(0, j.Length - 1) + ",\"ok\":true}";
                        _pendingTear = null;
                        Call("fileOpened", j);
                    }
                    break;
                case "themeFile":
                    Call("themeFile", ThemeJson());
                    break;
                case "themeReset":
                    try { File.WriteAllText(ThemeFilePath(), ThemeTemplate, new UTF8Encoding(false)); }
                    catch (Exception ex) { CallStr("status", "写入默认主题失败：" + ex.Message); }
                    Call("themeFile", ThemeJson());
                    CallStr("status", "已写入默认主题模板");
                    break;
                case "themeDir":
                    try { System.Diagnostics.Process.Start("explorer.exe", ThemeDir()); }
                    catch (Exception ex) { CallStr("status", "打开主题文件夹失败：" + ex.Message); }
                    break;
                case "fonts":
                    Call("fontList", FontsJson());
                    break;
                case "langList":
                    Call("langList", LangListJson());
                    break;
                case "langLoad":
                    Call("langData", LangDataJson(arg.Trim()));
                    break;
                case "langSave":
                    LangSave(arg);
                    break;
                case "langDir":
                    try { System.Diagnostics.Process.Start("explorer.exe", LangDir()); }
                    catch (Exception ex) { CallStr("status", "打开语言文件夹失败：" + ex.Message); }
                    break;
                case "hotkeys":
                    CallStr("hotkeys", hotkeys != null ? hotkeys.StatusText : "热键未初始化");
                    CallStr("hotkeyDefs", hotkeys != null ? hotkeys.HotkeysJson() : "[]");
                    break;
                case "registerHotkeys":
                    if (hotkeys != null) hotkeys.RegisterAll();
                    CallStr("hotkeys", hotkeys != null ? hotkeys.StatusText : "热键未初始化");
                    CallStr("hotkeyDefs", hotkeys != null ? hotkeys.HotkeysJson() : "[]");
                    CallStr("status", "已重新注册全局热键");
                    break;
                case "setHotkey":
                {
                    // arg: "action|keysInt"
                    int sep = arg.IndexOf('|');
                    if (hotkeys != null && sep > 0)
                    {
                        string action = arg.Substring(0, sep);
                        int v;
                        string err = null;
                        if (int.TryParse(arg.Substring(sep + 1), out v) &&
                            hotkeys.UpdateDef(action, (Keys)v, out err))
                            CallStr("status", "热键已更新并注册");
                        else
                            CallStr("status", "改键失败：" + (err ?? "参数错误"));
                        CallStr("hotkeys", hotkeys.StatusText);
                        CallStr("hotkeyDefs", hotkeys.HotkeysJson());
                    }
                    break;
                }
                case "resetHotkeys":
                    if (hotkeys != null) hotkeys.ResetDefaults();
                    CallStr("hotkeys", hotkeys != null ? hotkeys.StatusText : "热键未初始化");
                    CallStr("hotkeyDefs", hotkeys != null ? hotkeys.HotkeysJson() : "[]");
                    CallStr("status", "已恢复默认热键");
                    break;
                case "generate":
                    DoGenerate(arg);
                    break;
                case "minimize": break;   // 最小化功能已移除（会导致窗口无法正常显示）
                case "maximize": ToggleMaximizeAnimated(); break;
                case "close": if (_secondary) CloseAnimated(); else RequestClose(); break;   // 主窗口按「关闭行为」处理
                case "runAction": RunQuickAction(arg); break;
                case "setCloseAction":
                {
                    string v = (arg ?? "").Trim().ToLowerInvariant();
                    if (v == "minimize" || v == "exit" || v == "ask") _closeAction = v;
                    SaveCloseAction();
                    CallStr("closeAction", _closeAction);
                    CallStr("status", _closeAction == "exit" ? "关闭窗口 = 直接退出应用"
                        : (_closeAction == "minimize" ? "关闭窗口 = 最小化到托盘" : "关闭窗口 = 每次询问"));
                    break;
                }
                case "drag": DragWindow(); break;
                case "theme": ApplyThemeColor(arg); break;   // 拖拽边跟随界面主题色
                case "status": CallStr("status", arg); break;
                case "openFile": OpenFile(); break;
                case "saveFile": SaveFile(arg); break;
                case "saveAsFile": SaveAsFile(Un(arg)); break;
                case "importTxt": ImportTxt(); break;
                case "exportTxt": ExportTxt(Un(arg)); break;
                case "dropFile": DropFile(Un(arg)); break;
                case "tearTab": TearTab(Un(arg)); break;        // 标签拖出窗口 → 同进程新窗口
                case "newWindow": TearTab("{}"); break;         // 文件 → 新建窗口
                case "openRecent": OpenPath(Un(arg)); break;    // 文件 → 最近使用
                case "uiPrefs": UIPrefs.Save(Un(arg)); break;   // 界面偏好（GPU 开关等）
                case "setTopmost": SetTopmost(arg == "1"); break;
                case "setSyncOut": SetSyncOut(arg == "1"); break;
                case "floatingInput": SetFloatingInput(arg == "1"); break;   // 系统级浮动输入面板
                case "appRules": AppRulesForm.ShowOrActivate(); break;        // 应用名单（白/黑名单）
                case "syncTarget": RecordForeground(); Call("syncState", SyncStateJson()); break;
                case "syncSend": SyncOutput(Un(arg)); break;
                case "forceRefresh": DoReload(Un(arg)); break;   // 界面已把全部正文打包好
                // ---- 文字提取（OCR）----
                case "ocrLangs": CallStr("ocrLangs", Ocr.LanguagesJson()); break;
                case "ocrPick": OcrPick(Un(arg)); break;
                case "ocrPaste": OcrPaste(Un(arg)); break;
                case "ocrFile": OcrFile(arg); break;
                case "setCopyNl":
                    UIPrefs.Save("{\"copyNl\":" + (arg == "1" ? "true" : "false") + "}");
                    CallStr("status", arg == "1" ? "复制时会用字面量 \\n 代替换行" : "复制时保持真实换行");
                    break;
                case "dirty": { int dn; if (int.TryParse(arg, out dn)) _dirtyCount = dn; break; }
                case "musicHost": MusicAddPaths(new string[] { Un(arg) }); break;   // 拖入音乐 → 加入播放列表
                case "musicPick": MusicPick(); break;           // 点按钮选音乐文件
                case "musicAdd": MusicAddPaths(new string[] { Un(arg) }); break;
                case "musicNext": MusicNext(); break;
                case "musicPrev": MusicPrev(); break;
                case "musicToggle": MusicToggle(); break;
                case "musicState": PushMusicState(); break;
                case "musicClear": MusicClear(); break;
                case "musicPlay":
                {
                    int mi;
                    if (int.TryParse(arg, out mi)) MusicPlay(mi);
                    break;
                }
                case "musicRemove":
                {
                    int mi;
                    if (int.TryParse(arg, out mi)) MusicRemove(mi);
                    break;
                }
                case "checkUpdate": CheckUpdate(true); break;
                case "doUpdate": DoUpdate(); break;
                case "openUrl": OpenUrl(arg); break;
                case "openLink": OpenBrowser(Un(arg)); break;   // 超链接点击 → 默认浏览器（Edge/Chrome）
                case "openDoc": OpenDoc(arg); break;            // 在线文档联动
                case "registerShell": RegisterShell(); CallStr("status", "已注册：右键菜单 + 打开方式列表"); break;
                case "unregisterShell": UnregisterShell(); CallStr("status", "已从右键菜单移除"); break;
                case "openDefaultApps": OpenDefaultApps(); break;
                case "pickBg": PickBgImage(arg); break;
                case "clearBg": ClearBgImage(arg); break;
                case "bgLoad": LoadBgImages(); break;
                case "bgUrl": SetBgFromUrl(arg); break;
                case "bgDaily": BgDaily(arg); break;
                // ---- Live2D（2D 立绘 / 桌宠）----
                case "live2dScan":
                    Live2D.Load();
                    CallStr("live2dModels", Live2D.ModelsJson());
                    CallStr("live2dState", Live2D.StateJson());
                    break;
                case "live2dApply":
                    CallStr("live2dState", Live2D.Apply(Un(arg)));
                    CallStr("status", "Live2D 设置已保存");
                    break;
                case "live2dImport":
                {
                    string id = Live2D.Import(this);
                    if (id.Length > 0)
                    {
                        CallStr("live2dModels", Live2D.ModelsJson());
                        CallStr("live2dState", Live2D.Apply(Live2D.StateJson()));
                        CallStr("status", "已导入 Live2D 模型：" + id +
                            (Live2D.LastImportNote.Length > 0 ? "（" + Live2D.LastImportNote + "）" : ""));
                    }
                    break;
                }
                case "live2dOpenFolder": Live2D.OpenFolder(); break;
                case "live2dImportFolder":
                {
                    string id = Live2D.ImportFolder(this);
                    if (id.Length > 0)
                    {
                        CallStr("live2dModels", Live2D.ModelsJson());
                        CallStr("live2dState", Live2D.Apply(Live2D.StateJson()));
                        CallStr("status", "已导入 Live2D 模型包：" + id);
                    }
                    else if (Live2D.LastImportNote.Length > 0)
                        CallStr("status", Live2D.LastImportNote);
                    break;
                }
                case "live2dInstallCubism": StartCubismRuntimeDownload(); break;
                case "live2dFindModel": Live2D.FindModelOnline(Un(arg)); break;
                case "live2dOpenConfig": Live2D.OpenConfig(); break;
                case "live2dInstall3D": StartLive2dRuntimeDownload(); break;
                case "live2dAi": Live2dAiWorkshop(); break;
                case "colorLib": FetchColorLibrary(); break;
                case "blockArt": BlockArtFromFile(Un(arg)); break;
                case "translate": Translate(arg); break;
                case "chat": Chat(Un(arg)); break;
                // ---- AI 接入（Beta）----
                case "aiChat": AiChat(arg); break;
                case "aiSave": AiSaveSettings(arg); break;
                case "aiLoad": CallStr("aiSettings", AiLoadSettings()); break;
                case "aiServerStart": AiServerApply(arg); break;
                case "aiServerStatus": CallStr("aiServerStatus", AiServerStatus()); break;
                case "aiState": SetAiState(arg); break;
                case "aiActionResult": OnAiActionResult(arg); break;
                case "aiMemoryLoad": CallStr("aiMemory", AiMemoryLoad()); break;
                case "aiMemoryAdd": CallStr("aiMemory", AiMemoryAdd(arg)); break;
                case "aiMemoryRemove": CallStr("aiMemory", AiMemoryRemove(arg)); break;
                case "aiMemoryClear": CallStr("aiMemory", AiMemoryClear()); break;
                case "convLoad": CallStr("convs", AiConvsLoad()); break;
                case "convSave": AiConvsSave(arg); break;
                case "openChatPage": AiOpenChatPage(); break;
                case "chatWindowState": Call("aiWindowState", "{\"open\":" + (ChatWindow.IsOpen ? "true" : "false") + "}"); break;
                case "aiUndone": NotifyToast("已撤回", "已恢复本次 AI 修改前的状态。"); break;
                case "easterEgg": PlayNetSound(); break;
                case "uninstall": UninstallApp(); break;
                case "exportHtml": ExportHtml(Un(arg)); break;
                case "copy":
                {
                    string txt = Un(arg);
                    try { Clipboard.SetText(txt); CallStr("status", "已复制到剪贴板"); } catch { }
                    if (_syncOut) SyncOutput(txt);   // 开了同步输出时，复制同时也粘贴到目标窗口
                    break;
                }
            }
        }

        /// <summary>取在线颜色表（jsdelivr color-name → color.pizza，失败则回报状态、前端继续用内置 140 色）</summary>
        private void FetchColorLibrary()
        {
            System.Threading.ThreadPool.QueueUserWorkItem(delegate
            {
                bool ok = false;
                try { ok = OnlineColors.Fetch(); }
                catch (Exception ex) { LogOnce("在线取色失败：" + ex.Message); }

                StringBuilder sb = new StringBuilder();
                sb.Append("{\"ok\":").Append(ok ? "true" : "false");
                sb.Append(",\"source\":").Append(Quote(OnlineColors.Source));
                sb.Append(",\"list\":[");
                List<NamedColor> list = OnlineColors.Cached;
                if (ok && list != null)
                {
                    for (int i = 0; i < list.Count; i++)
                    {
                        if (i > 0) sb.Append(',');
                        sb.Append('[').Append(Quote(list[i].Name)).Append(',').Append(Quote(list[i].Color.HexSharp)).Append(']');
                    }
                }
                sb.Append("]}");
                Call("colorLib", sb.ToString());
            });
        }

        /// <summary>打开链接（在线文档/超链接共用）：优先系统默认浏览器，失败再探测 Edge/Chrome，全无则提示</summary>
        private void OpenUrl(string url)
        {
            url = (url ?? "").Trim().Trim('"');
            if (url.Length == 0) { CallStr("status", "链接为空"); return; }
            try { System.Diagnostics.Process.Start(url); CallStr("status", "已在浏览器打开"); return; }
            catch { }
            string exe = FindBrowser();
            if (exe != null)
            {
                try { System.Diagnostics.Process.Start(exe, EscapeArg(url)); CallStr("status", "已在浏览器打开"); return; }
                catch { }
            }
            CallStr("status", "未检测到可用的浏览器，请安装 Edge 或 Chrome");
        }
        private void OpenBrowser(string url) { OpenUrl(url); }

        /// <summary>打开 Windows「默认应用」设置页：把本程序设为 .txt 等格式的默认打开方式。</summary>
        private void OpenDefaultApps()
        {
            try
            {
                System.Diagnostics.Process.Start("ms-settings:defaultapps");
                CallStr("status", "已打开系统「默认应用」设置，请为本程序选择要接管的格式");
            }
            catch (Exception ex) { CallStr("status", "打开系统设置失败：" + ex.Message); }
        }

        /// <summary>探测 Edge / Chrome 可执行文件；找不到返回 null</summary>
        private static string FindBrowser()
        {
            string[] roots = Environment.Is64BitOperatingSystem
                ? new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                          Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles) }
                : new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles) };
            foreach (string root in roots)
            {
                if (string.IsNullOrEmpty(root)) continue;
                string[] cands =
                {
                    Path.Combine(root, "Microsoft", "Edge", "Application", "msedge.exe"),
                    Path.Combine(root, "Google", "Chrome", "Application", "chrome.exe")
                };
                foreach (string c in cands) { if (File.Exists(c)) return c; }
            }
            return null;
        }
        /// <summary>给进程参数加引号（防空格路径/参数注入）</summary>
        private static string EscapeArg(string s)
        {
            bool needs = s.IndexOfAny(new[] { ' ', '"', '\t' }) >= 0;
            return needs ? "\"" + s.Replace("\"", "\\\"") + "\"" : s;
        }

        // ============================================================ 在线文档联动（5.8.0.2）
        /// <summary>平台键 → (新建文档 URL, 是否免费；受限平台直接跳浏览器并提示)</summary>
        private static readonly Dictionary<string, Tuple<string, bool>> DocPlatforms =
            new Dictionary<string, Tuple<string, bool>>
            {
                { "qq",     Tuple.Create("https://docs.qq.com/desktop", true) },
                { "kdocs",  Tuple.Create("https://www.kdocs.cn/create", true) },
                { "gdocs",  Tuple.Create("https://docs.google.com/document/create", true) },
                { "shimo",  Tuple.Create("https://shimo.im/desktop/new", true) },
                { "feishu", Tuple.Create("https://www.feishu.cn/docx/create", true) },
                { "yuque",  Tuple.Create("https://www.yuque.com/newword", true) },
                { "notion", Tuple.Create("https://www.notion.so/", false) },
                { "office", Tuple.Create("https://www.office.com/launch/word", false) }
            };

        private void OpenDoc(string arg)
        {
            // arg = "key|encodeURIComponent(text)"
            int sep = arg.IndexOf('|');
            string key = sep < 0 ? arg : arg.Substring(0, sep);
            string text = sep < 0 ? "" : Un(arg.Substring(sep + 1));
            Tuple<string, bool> p;
            if (!DocPlatforms.TryGetValue(key, out p)) { CallStr("status", "未知的文档平台"); return; }
            if (text.Length > 0) { try { Clipboard.SetText(text); } catch { } }
            OpenUrl(p.Item1);
            CallStr("status", p.Item2
                ? "已复制文档内容，到浏览器新建文档后 Ctrl+V 粘贴"
                : "该平台部分功能需登录/付费，已在浏览器打开");
        }

        // ============================================================ 系统集成（HKCU 免管理员）
        private const string ShellKey = @"Software\Classes\*\shell\RichTextGenApp";
        /// <summary>文本文件 ProgId：出现在「打开方式」列表里，并作为默认应用的候选。</summary>
        private const string ProgId = "RichTextGen.TextFile";
        /// <summary>注册表结构版本：扩展名列表一变就 +1，老用户下次启动会自动按新范围重注册。</summary>
        private const string AssocMarker = "2";

        /// <summary>
        /// 支持作为「打开方式」的文本类扩展名（精简版，只保留最常用的 6 种）。
        /// 之前一次注册 30 多个扩展名会在「打开方式」里堆一大片，太打扰系统，故收窄。
        /// </summary>
        private static readonly string[] TextExts = new string[]
        {
            ".txt", ".md", ".log", ".json", ".ini", ".csv"
        };

        /// <summary>历史上注册过、现在已收窄掉的扩展名：注册时顺手清掉自己的条目，避免残留。</summary>
        private static readonly string[] LegacyExts = new string[]
        {
            ".markdown", ".xml", ".cfg", ".conf", ".tsv", ".yml", ".yaml", ".html", ".htm",
            ".css", ".js", ".ts", ".cs", ".py", ".java", ".cpp", ".h", ".bat", ".cmd",
            ".ps1", ".sh", ".sql", ".ahk", ".lua", ".toml", ".nfo", ".properties", ".gitignore"
        };

        [DllImport("shell32.dll")]
        private static extern void SHChangeNotify(uint wEventId, uint uFlags, IntPtr dwItem1, IntPtr dwItem2);

        private static void ShellNotifyChanged()
        {
            try { SHChangeNotify(0x08000000 /*SHCNE_ASSOCCHANGED*/, 0x0000, IntPtr.Zero, IntPtr.Zero); } catch { }
        }

        private static string AppExeName { get { return Path.GetFileName(Application.ExecutablePath); } }

        /// <summary>
        /// 注册系统集成：
        /// 1) 任意文件右键 →「用彩色文本生成器打开」（经典菜单）
        /// 2) ProgId + Applications 注册 → 出现在「打开方式」列表
        /// 3) 各类文本扩展名的 OpenWithProgids → 这些格式都能选本程序打开
        /// 注意：Windows 10/11 的「默认应用」不允许程序静默篡改，需要用户在系统设置里选定一次。
        /// </summary>
        private void RegisterShell()
        {
            try
            {
                string exe = Application.ExecutablePath;
                string quoted = "\"" + exe + "\"";

                // 1) 通用右键项（经典菜单 / Win11「显示更多选项」）
                using (RegistryKey k = Registry.CurrentUser.CreateSubKey(ShellKey)) { k.SetValue("", "用彩色文本生成器打开"); }
                using (RegistryKey k = Registry.CurrentUser.CreateSubKey(ShellKey + "\\command")) { k.SetValue("", quoted + " \"%1\""); }
                using (RegistryKey k = Registry.CurrentUser.CreateSubKey(ShellKey + "\\DefaultIcon")) { k.SetValue("", quoted + ",0"); }

                // 2) ProgId：打开方式的显示名 / 图标 / 命令
                using (RegistryKey k = Registry.CurrentUser.CreateSubKey(@"Software\Classes\" + ProgId)) { k.SetValue("", "文本文件（彩色文本生成器）"); k.SetValue("RegVersion", AssocMarker); }
                using (RegistryKey k = Registry.CurrentUser.CreateSubKey(@"Software\Classes\" + ProgId + "\\DefaultIcon")) { k.SetValue("", quoted + ",0"); }
                using (RegistryKey k = Registry.CurrentUser.CreateSubKey(@"Software\Classes\" + ProgId + "\\shell\\open\\command")) { k.SetValue("", quoted + " \"%1\""); }

                // 3) 应用程序注册（「打开方式」里按程序名列出）
                string appKey = @"Software\Classes\Applications\" + AppExeName;
                using (RegistryKey k = Registry.CurrentUser.CreateSubKey(appKey)) { k.SetValue("FriendlyAppName", "彩色文本生成器"); }
                using (RegistryKey k = Registry.CurrentUser.CreateSubKey(appKey + "\\DefaultIcon")) { k.SetValue("", quoted + ",0"); }
                using (RegistryKey k = Registry.CurrentUser.CreateSubKey(appKey + "\\shell\\open\\command")) { k.SetValue("", quoted + " \"%1\""); }
                // SupportedTypes 先清空重建，避免旧版本注册的一大堆扩展名残留
                try { Registry.CurrentUser.DeleteSubKeyTree(appKey + "\\SupportedTypes", false); } catch { }
                using (RegistryKey k = Registry.CurrentUser.CreateSubKey(appKey + "\\SupportedTypes"))
                {
                    foreach (string ext in TextExts) k.SetValue(ext, "");
                }

                // 4) 各扩展名的候选打开方式
                foreach (string ext in TextExts)
                {
                    using (RegistryKey k = Registry.CurrentUser.CreateSubKey(@"Software\Classes\" + ext + "\\OpenWithProgids"))
                        k.SetValue(ProgId, new byte[0], RegistryValueKind.None);
                }
                // 顺手清掉收窄前登记过的旧扩展名，避免「打开方式」里残留
                foreach (string ext in LegacyExts)
                {
                    try
                    {
                        using (RegistryKey k = Registry.CurrentUser.OpenSubKey(@"Software\Classes\" + ext + "\\OpenWithProgids", true))
                            if (k != null) k.DeleteValue(ProgId, false);
                    }
                    catch { }
                }

                // 5) 纯文本类型（无扩展名也算文本）的右键项
                using (RegistryKey k = Registry.CurrentUser.CreateSubKey(@"Software\Classes\SystemFileAssociations\text\shell\RichTextGenApp"))
                    k.SetValue("", "用彩色文本生成器打开");
                // 右键项也要有图标，否则 Win11 菜单里会显示通用空白图标、与其它项不对齐
                using (RegistryKey k = Registry.CurrentUser.CreateSubKey(@"Software\Classes\SystemFileAssociations\text\shell\RichTextGenApp\DefaultIcon"))
                    k.SetValue("", quoted + ",0");
                using (RegistryKey k = Registry.CurrentUser.CreateSubKey(@"Software\Classes\SystemFileAssociations\text\shell\RichTextGenApp\command"))
                    k.SetValue("", quoted + " \"%1\"");

                ShellNotifyChanged();
            }
            catch { }
        }

        private void UnregisterShell()
        {
            try
            {
                Registry.CurrentUser.DeleteSubKeyTree(ShellKey, false);
                Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\" + ProgId, false);
                Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\Applications\" + AppExeName, false);
                Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\SystemFileAssociations\text\shell\RichTextGenApp", false);
                foreach (string ext in TextExts)
                {
                    try
                    {
                        using (RegistryKey k = Registry.CurrentUser.OpenSubKey(@"Software\Classes\" + ext + "\\OpenWithProgids", true))
                            if (k != null) k.DeleteValue(ProgId, false);
                    }
                    catch { }
                }
                foreach (string ext in LegacyExts)
                {
                    try
                    {
                        using (RegistryKey k = Registry.CurrentUser.OpenSubKey(@"Software\Classes\" + ext + "\\OpenWithProgids", true))
                            if (k != null) k.DeleteValue(ProgId, false);
                    }
                    catch { }
                }
                ShellNotifyChanged();
            }
            catch { }
        }

        /// <summary>启动时检测：未注册或注册范围是旧版本时，自动按当前范围重注册（幂等）</summary>
        private void CheckShellRegistration()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(@"Software\Classes\" + ProgId))
                {
                    object v = k != null ? k.GetValue("RegVersion") : null;
                    if (v != null && string.Equals(v.ToString(), AssocMarker, StringComparison.Ordinal)) return;
                }
                RegisterShell();
            }
            catch { }
        }

        // ============================================================ 背景水印图（深色 / 亮色可分别设置）
        private static string BgFile(string kind)
        {
            try
            {
                if (!Directory.Exists(AiConfig.Dir)) return "";
                string[] fs = Directory.GetFiles(AiConfig.Dir, "bg-" + kind + ".*");
                return fs.Length > 0 ? fs[0] : "";
            }
            catch { return ""; }
        }

        private static string MimeOf(string path)
        {
            switch ((Path.GetExtension(path) ?? "").ToLowerInvariant())
            {
                case ".jpg":
                case ".jpeg": return "image/jpeg";
                case ".gif": return "image/gif";
                case ".webp": return "image/webp";
                case ".avif": return "image/avif";
                case ".svg": return "image/svg+xml";
                case ".bmp": return "image/bmp";
                case ".mp4":
                case ".m4v": return "video/mp4";
                case ".webm": return "video/webm";
                case ".ogv": return "video/ogg";
                case ".mov": return "video/quicktime";
                case ".mkv": return "video/x-matroska";
                case ".avi": return "video/x-msvideo";
                case ".mp3": return "audio/mpeg";
                case ".wav": return "audio/wav";
                case ".ogg":
                case ".opus": return "audio/ogg";
                case ".m4a":
                case ".aac": return "audio/mp4";
                case ".flac": return "audio/flac";
                case ".wma": return "audio/x-ms-wma";
                case ".js": return "text/javascript; charset=utf-8";
                case ".json": return "application/json; charset=utf-8";
                case ".moc3": return "application/octet-stream";
                case ".glb":
                case ".vrm":
                case ".gltf": return "model/gltf-binary";
                case ".bin": return "application/octet-stream";
                default: return "image/png";
            }
        }

        /// <summary>背景可用的视频扩展名（界面里真正能解码的取决于 WebView2 内置的 Chromium）</summary>
        private static readonly string[] BgVideoExts =
            { ".mp4", ".m4v", ".webm", ".ogv", ".ogg", ".mov", ".mkv", ".avi" };

        private static bool IsBgVideo(string path)
        {
            string e = (Path.GetExtension(path) ?? "").ToLowerInvariant();
            foreach (string v in BgVideoExts) if (e == v) return true;
            return false;
        }

        /// <summary>把当前背景发给界面：图片走 data URL，视频走本地虚拟源（可拖动、可循环，不必塞进内存字符串）。</summary>
        private void SendBgImage(string kind)
        {
            string path = BgFile(kind);
            string url = "", name = "", type = "image";
            try
            {
                if (!string.IsNullOrEmpty(path) && File.Exists(path))
                {
                    name = Path.GetFileName(path);
                    if (IsBgVideo(path))
                    {
                        type = "video";
                        // 带时间戳避免换文件后命中 WebView 的缓存
                        url = "https://richtextgen.local/__bg/" + kind + "?v=" + DateTime.Now.Ticks;
                    }
                    else
                    {
                        byte[] b = File.ReadAllBytes(path);
                        url = "data:" + MimeOf(path) + ";base64," + Convert.ToBase64String(b);
                    }
                }
            }
            catch { }
            Call("bgImage", "{\"kind\":" + Quote(kind) + ",\"url\":" + Quote(url) +
                 ",\"name\":" + Quote(name) + ",\"type\":" + Quote(type) + "}");
        }

        /// <summary>启动时把已保存的背景读回界面。</summary>
        private void LoadBgImages()
        {
            SendBgImage("dark");
            SendBgImage("light");
        }

        /// <summary>选择背景（kind = dark / light），图片或视频都行，复制到 %APPDATA%\RichTextGen 后回传。</summary>
        private void PickBgImage(string kind)
        {
            kind = (kind == "light") ? "light" : "dark";
            using (OpenFileDialog d = new OpenFileDialog())
            {
                d.Filter = "图片或视频|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp;*.avif;*.svg;" +
                           "*.mp4;*.m4v;*.webm;*.ogv;*.ogg;*.mov;*.mkv;*.avi" +
                           "|图片文件|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp;*.avif;*.svg" +
                           "|视频文件|*.mp4;*.m4v;*.webm;*.ogv;*.ogg;*.mov;*.mkv;*.avi" +
                           "|所有文件|*.*";
                d.Title = (kind == "dark" ? "选择深色模式背景（图片或视频）" : "选择亮色模式背景（图片或视频）");
                if (d.ShowDialog(this) != DialogResult.OK) return;
                string err;
                if (!ApplyBgFile(kind, d.FileName, out err)) { CallStr("status", err); return; }
                SendBgImage(kind);
                CallStr("status", "已设置" + (kind == "dark" ? "深色" : "亮色") + "背景");
            }
        }

        /// <summary>校验大小并复制成本地 bg-&lt;kind&gt;.&lt;ext&gt;（保留扩展名，界面据此区分图片/视频）。</summary>
        private static bool ApplyBgFile(string kind, string src, out string error)
        {
            error = "";
            try
            {
                bool video = IsBgVideo(src);
                long max = video ? 48L * 1024 * 1024 : 16L * 1024 * 1024;
                if (new FileInfo(src).Length > max)
                {
                    error = video ? "视频超过 48MB，请换一段小一点的" : "图片超过 16MB，请换一张小一点的";
                    return false;
                }
                Directory.CreateDirectory(AiConfig.Dir);
                foreach (string old in Directory.GetFiles(AiConfig.Dir, "bg-" + kind + ".*"))
                {
                    try { File.Delete(old); } catch { }
                }
                string dest = Path.Combine(AiConfig.Dir, "bg-" + kind + Path.GetExtension(src).ToLowerInvariant());
                File.Copy(src, dest, true);
                return true;
            }
            catch (Exception ex) { error = "设置背景失败：" + ex.Message; return false; }
        }

        private void ClearBgImage(string kind)
        {
            kind = (kind == "light") ? "light" : "dark";
            try
            {
                foreach (string old in Directory.GetFiles(AiConfig.Dir, "bg-" + kind + ".*"))
                {
                    try { File.Delete(old); } catch { }
                }
            }
            catch { }
            Call("bgImage", "{\"kind\":" + Quote(kind) + ",\"url\":\"\",\"name\":\"\",\"type\":\"image\"}");
            CallStr("status", "已清除" + (kind == "dark" ? "深色" : "亮色") + "背景");
        }

        // ------------------------------------------------------------ 网络背景图片 / 视频
        /// <summary>参数：kind|url，kind = dark / light / both</summary>
        private void SetBgFromUrl(string arg)
        {
            string kind = "dark", raw = arg ?? "";
            int bar = raw.IndexOf('|');
            if (bar >= 0) { kind = raw.Substring(0, bar); raw = raw.Substring(bar + 1); }
            string url = Un(raw).Trim();
            if (url.Length == 0) { CallStr("status", "请先填写网络地址（图片或视频直链）"); return; }
            if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) url = "https://" + url;
            FetchBgAsync(url, BgKinds(kind));
        }

        /// <summary>一键取网络壁纸（Bing 每日图），当作“自动获取网络图片”的现成来源。</summary>
        private void BgDaily(string arg)
        {
            string kind = arg == "both" ? "both" : (arg == "light" ? "light" : "dark");
            System.Threading.ThreadPool.QueueUserWorkItem(delegate
            {
                CallStr("status", "正在获取网络壁纸…");
                string json = Http("https://www.bing.com/HPImageArchive.aspx?format=js&idx=0&n=1&mkt=zh-CN", 12000);
                Match m = Regex.Match(json ?? "", "\"url\"\\s*:\\s*\"([^\"]+)\"");
                if (!m.Success) { CallStr("status", "获取网络壁纸失败，请检查网络，或改用「粘贴地址」"); return; }
                FetchBg("https://www.bing.com" + m.Groups[1].Value, BgKinds(kind));
            });
        }

        private static string[] BgKinds(string kind)
        {
            if (kind == "both") return new[] { "dark", "light" };
            return new[] { kind == "light" ? "light" : "dark" };
        }

        private void FetchBgAsync(string url, string[] kinds)
        {
            System.Threading.ThreadPool.QueueUserWorkItem(delegate { FetchBg(url, kinds); });
        }

        /// <summary>下载网络背景到临时文件，再按 kind 落到本地（离线也能用，且不受 https 页面混合内容限制）。</summary>
        private void FetchBg(string url, string[] kinds)
        {
            string tmp = null;
            try
            {
                CallStr("status", "正在从网络获取背景…");
                ServicePointManager.SecurityProtocol =
                    SecurityProtocolType.Tls12 | SecurityProtocolType.Tls11 | SecurityProtocolType.Tls;
                string ext = "";
                HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
                req.Timeout = 20000;
                req.ReadWriteTimeout = 30000;
                req.UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) RichTextGen/" + Version;
                req.AllowAutoRedirect = true;
                tmp = Path.Combine(Path.GetTempPath(), "rtg-bg-" + Guid.NewGuid().ToString("N") + ".tmp");
                using (WebResponse resp = req.GetResponse())
                {
                    ext = BgExtOfUrl(url);
                    if (ext.Length == 0) ext = BgExtOfMime(resp.ContentType);
                    using (Stream s = resp.GetResponseStream())
                    using (FileStream fs = new FileStream(tmp, FileMode.Create, FileAccess.Write))
                    {
                        byte[] buf = new byte[81920];
                        long total = 0;
                        int n;
                        while ((n = s.Read(buf, 0, buf.Length)) > 0)
                        {
                            total += n;
                            if (total > 48L * 1024 * 1024) throw new Exception("文件超过 48MB");
                            fs.Write(buf, 0, n);
                        }
                    }
                }
                if (ext.Length == 0) ext = ".jpg";
                bool video = IsBgVideo("x" + ext);
                foreach (string k in kinds)
                {
                    Directory.CreateDirectory(AiConfig.Dir);
                    foreach (string old in Directory.GetFiles(AiConfig.Dir, "bg-" + k + ".*"))
                    {
                        try { File.Delete(old); } catch { }
                    }
                    File.Copy(tmp, Path.Combine(AiConfig.Dir, "bg-" + k + ext), true);
                }
                foreach (string k in kinds) SendBgImage(k);
                CallStr("status", "已从网络设置" + (video ? "视频" : "图片") + "背景");
            }
            catch (Exception ex) { CallStr("status", "获取网络背景失败：" + ex.Message); }
            finally { if (tmp != null) { try { File.Delete(tmp); } catch { } } }
        }

        private static string BgExtOfUrl(string url)
        {
            try
            {
                string p = new Uri(url).AbsolutePath.ToLowerInvariant();
                string e = Path.GetExtension(p);
                if (e == ".png" || e == ".jpg" || e == ".jpeg" || e == ".gif" || e == ".webp" ||
                    e == ".avif" || e == ".svg" || e == ".mp4" || e == ".m4v" || e == ".webm" ||
                    e == ".ogv" || e == ".ogg" || e == ".mov" || e == ".mkv" || e == ".avi")
                    return e == ".jpeg" ? ".jpg" : e;
            }
            catch { }
            return "";
        }

        private static string BgExtOfMime(string mime)
        {
            mime = (mime ?? "").ToLowerInvariant();
            if (mime.StartsWith("image/jpeg")) return ".jpg";
            if (mime.StartsWith("image/png")) return ".png";
            if (mime.StartsWith("image/gif")) return ".gif";
            if (mime.StartsWith("image/webp")) return ".webp";
            if (mime.StartsWith("image/avif")) return ".avif";
            if (mime.StartsWith("image/svg")) return ".svg";
            if (mime.StartsWith("image/bmp")) return ".bmp";
            if (mime.StartsWith("video/mp4")) return ".mp4";
            if (mime.StartsWith("video/webm")) return ".webm";
            if (mime.StartsWith("video/ogg")) return ".ogv";
            if (mime.StartsWith("video/quicktime")) return ".mov";
            if (mime.StartsWith("video/x-matroska")) return ".mkv";
            if (mime.StartsWith("video/x-msvideo")) return ".avi";
            return "";
        }

        /// <summary>把本地文件以支持 Range 的响应送回 WebView（视频/音乐拖动进度、循环都靠它）。</summary>
        private void ServeMedia(CoreWebView2WebResourceRequestedEventArgs e2, string file)
        {
            try
            {
                byte[] raw = File.ReadAllBytes(file);
                string mime = MimeOf(file);
                string rangeHeader = "";
                try { rangeHeader = e2.Request.Headers.GetHeader("Range"); } catch { }
                long st, en;
                if (TryBgRange(rangeHeader, raw.LongLength, out st, out en))
                {
                    int len = (int)(en - st + 1);
                    byte[] part = new byte[len];
                    Array.Copy(raw, st, part, 0, len);
                    e2.Response = web.CoreWebView2.Environment.CreateWebResourceResponse(
                        new MemoryStream(part), 206, "Partial Content",
                        "Content-Type: " + mime + "\r\nAccept-Ranges: bytes\r\nCache-Control: no-cache" +
                        "\r\nContent-Range: bytes " + st + "-" + en + "/" + raw.LongLength +
                        "\r\nContent-Length: " + len);
                }
                else
                {
                    e2.Response = web.CoreWebView2.Environment.CreateWebResourceResponse(
                        new MemoryStream(raw), 200, "OK",
                        "Content-Type: " + mime + "\r\nAccept-Ranges: bytes\r\nCache-Control: no-cache" +
                        "\r\nContent-Length: " + raw.LongLength);
                }
            }
            catch
            {
                try
                {
                    e2.Response = web.CoreWebView2.Environment.CreateWebResourceResponse(
                        new MemoryStream(new byte[0]), 500, "Error", "Content-Type: text/plain");
                }
                catch { }
            }
        }

        /// <summary>解析 Range 头（视频播放器依赖它做 seek / 循环）。返回 false 表示整段返回。</summary>
        private static bool TryBgRange(string header, long total, out long start, out long end)
        {
            start = 0; end = total - 1;
            if (string.IsNullOrEmpty(header) || total <= 0) return false;
            if (!header.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase)) return false;
            string spec = header.Substring(6).Split(',')[0].Trim();
            int dash = spec.IndexOf('-');
            if (dash < 0) return false;
            string a = spec.Substring(0, dash).Trim(), b = spec.Substring(dash + 1).Trim();
            long s;
            if (a.Length == 0)
            {
                long n;
                if (!long.TryParse(b, out n) || n <= 0) return false;
                start = Math.Max(0, total - n);
            }
            else
            {
                if (!long.TryParse(a, out s) || s < 0 || s >= total) return false;
                start = s;
                long en;
                if (b.Length > 0 && long.TryParse(b, out en)) end = Math.Min(total - 1, en);
            }
            return end >= start;
        }

        // ============================================================ 文字提取（OCR）
        /// <summary>选择图片：支持常见图片格式，长截图会自动分段识别。</summary>
        private void OcrPick(string lang)
        {
            using (OpenFileDialog d = new OpenFileDialog())
            {
                d.Filter = "图片文件|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff;*.webp;*.avif;*.heic;*.heif" +
                           "|所有文件|*.*";
                d.Title = "选择要提取文字的图片（支持长截图）";
                if (d.ShowDialog(this) != DialogResult.OK) return;
                OcrRun(d.FileName, lang, false);
            }
        }

        /// <summary>读取剪贴板里的图片（截图工具复制后直接提取）。</summary>
        private void OcrPaste(string lang)
        {
            try
            {
                Image img = null;
                try
                {
                    IDataObject d = Clipboard.GetDataObject();
                    if (d != null && d.GetDataPresent("PNG"))
                    {
                        MemoryStream ms = d.GetData("PNG") as MemoryStream;
                        if (ms != null)
                        {
                            using (Image im = Image.FromStream(ms)) img = new Bitmap(im);
                        }
                    }
                }
                catch { }
                if (img == null && Clipboard.ContainsImage()) img = Clipboard.GetImage();
                if (img == null) { CallStr("status", "剪贴板里没有图片，先截图或复制一张图"); return; }

                string tmp = Path.Combine(Path.GetTempPath(), "rtg-ocr-" + Guid.NewGuid().ToString("N") + ".png");
                try { img.Save(tmp, System.Drawing.Imaging.ImageFormat.Png); }
                finally { img.Dispose(); }
                OcrRun(tmp, lang, true);
            }
            catch (Exception ex) { CallStr("status", "读取剪贴板图片失败：" + ex.Message); }
        }

        /// <summary>参数：lang|path（两段都经过 encodeURIComponent）。</summary>
        private void OcrFile(string arg)
        {
            string raw = arg ?? "";
            int bar = raw.IndexOf('|');
            string lang = bar >= 0 ? Un(raw.Substring(0, bar)) : "";
            string path = Un(bar >= 0 ? raw.Substring(bar + 1) : raw);
            OcrRun(path, lang, false);
        }

        private void OcrRun(string path, string lang, bool temp)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                CallStr("status", "找不到要识别的图片");
                if (temp) { try { File.Delete(path); } catch { } }
                return;
            }
            DateTime t0 = DateTime.Now;
            Call("ocrProgress", "{\"percent\":1,\"text\":" + Quote("正在读取图片…") + "}");
            System.Threading.ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    string engine = "";
                    string text = Ocr.Recognize(path, lang, delegate (int pct, string msg)
                    {
                        Call("ocrProgress", "{\"percent\":" + pct + ",\"text\":" + Quote(msg) + "}");
                    }, out engine);
                    Call("ocrResult", "{\"ok\":true,\"text\":" + Quote(text) +
                         ",\"ms\":" + (int)(DateTime.Now - t0).TotalMilliseconds +
                         ",\"engine\":" + Quote(engine) +
                         ",\"name\":" + Quote(Path.GetFileName(path)) + "}");
                }
                catch (Exception ex)
                {
                    Call("ocrResult", "{\"ok\":false,\"error\":" + Quote(ex.Message) + "}");
                }
                finally
                {
                    if (temp) { try { File.Delete(path); } catch { } }
                }
            });
        }

        // ============================================================ 置顶 / 同步输出
        private void SetTopmost(bool on)
        {
            try { TopMost = on; } catch { }
            UIPrefs.Save("{\"topmost\":" + (on ? "true" : "false") + "}");
            Call("syncState", SyncStateJson());
            CallStr("status", on ? "窗口已置顶：始终显示在其他窗口之前" : "已取消窗口置顶");
        }

        private void SetSyncOut(bool on)
        {
            _syncOut = on;
            UIPrefs.Save("{\"syncOut\":" + (on ? "true" : "false") + "}");
            if (on && (_lastExternal == IntPtr.Zero || !IsWindow(_lastExternal))) RecordForeground();
            Call("syncState", SyncStateJson());
            CallStr("status", on
                ? "已开启同步输出：生成后会把结果自动粘贴到最近点击的窗口"
                : "已关闭同步输出");
        }

        /// <summary>系统级浮动输入面板：点任意软件的输入框都会在光标旁弹出小面板并回填。</summary>
        private void SetFloatingInput(bool on)
        {
            FloatingInputForm.SetEnabled(on);
            UIPrefs.Save("{\"floating\":" + (on ? "true" : "false") + "}");
            Call("syncState", SyncStateJson());
            CallStr("status", on
                ? "已开启浮动输入面板：点任意软件的输入框即可弹出"
                : "已关闭浮动输入面板");
        }

        private string SyncStateJson()
        {
            return "{\"enabled\":" + (_syncOut ? "true" : "false") +
                   ",\"topmost\":" + (TopMost ? "true" : "false") +
                   ",\"copyNl\":" + (UIPrefs.CopyNl ? "true" : "false") +
                   ",\"floating\":" + (FloatingInputForm.IsEnabled ? "true" : "false") +
                   ",\"target\":" + Quote(_lastExternalTitle) + "}";
        }

        /// <summary>0.5 秒记一次前台窗口。本进程的窗口会被跳过，所以「最后点过的外部窗口」一直保留。</summary>
        private void StartForegroundWatch()
        {
            _fgTimer = new System.Windows.Forms.Timer();
            _fgTimer.Interval = 500;
            _fgTimer.Tick += delegate { RecordForeground(); };
            _fgTimer.Start();
            FormClosed += delegate
            {
                try { _fgTimer.Stop(); _fgTimer.Dispose(); } catch { }
                if (!_secondary) FloatingInputForm.Shutdown();   // 主窗口退出时卸掉全局钩子
            };
        }

        private void RecordForeground()
        {
            try
            {
                IntPtr fg = GetForegroundWindow();
                if (fg == IntPtr.Zero) return;
                bool changed = fg != _lastFg;
                _lastFg = fg;
                if (fg == Handle) return;
                uint pid;
                GetWindowThreadProcessId(fg, out pid);
                if (pid == (uint)System.Diagnostics.Process.GetCurrentProcess().Id) return;   // 本程序的另一个窗口也不算
                if (!IsWindowVisible(fg)) return;
                int len = GetWindowTextLength(fg);
                if (len <= 0) return;
                StringBuilder sb = new StringBuilder(len + 2);
                if (GetWindowText(fg, sb, sb.Capacity) <= 0) return;
                string title = sb.ToString().Trim();
                if (title.Length == 0 || title == "Program Manager") return;
                _lastExternal = fg;
                _lastExternalTitle = title;
                // 用户点了另一个窗口 → 这就是「再点一次让它聚焦」，把等待中的文字贴进去
                if (changed && _pendingPaste != null) PastePending();
            }
            catch { }
        }

        /// <summary>把文字先准备好，等用户点击目标窗口获得焦点后再粘贴（不再由程序抢焦点）。</summary>
        private void SyncOutput(string text)
        {
            if (!_syncOut) return;
            if (text == null || text.Length == 0) { CallStr("status", "同步输出：没有可发送的文字"); return; }
            // 连续改颜色时避免 0.5 秒内重复准备同一份内容
            if (text == _lastSent && (DateTime.Now - _lastSentAt).TotalMilliseconds < 1200) return;
            _lastSent = text;
            _lastSentAt = DateTime.Now;

            _pendingPaste = text;
            _pendingAt = DateTime.Now;
            try { Clipboard.SetText(text); } catch { }

            string who = _lastExternalTitle.Length > 0 ? ("「" + _lastExternalTitle + "」") : "目标窗口";
            CallStr("status", "文字已就绪：点一下" + who + "让它聚焦，随后会自动粘贴");
        }

        /// <summary>目标窗口已被用户点中并获得焦点：稍等输入框就绪，再按 Ctrl+V。</summary>
        private void PastePending()
        {
            if (_pendingPaste == null) return;
            // 超过 2 分钟还没点目标窗口，就把待粘贴内容作废，避免以后误贴
            if ((DateTime.Now - _pendingAt).TotalSeconds > 120)
            {
                _pendingPaste = null;
                return;
            }
            string text = _pendingPaste;
            _pendingPaste = null;
            string title = _lastExternalTitle;

            try { Clipboard.SetText(text); }
            catch
            {
                try { Thread.Sleep(60); Clipboard.SetText(text); }
                catch (Exception ex) { CallStr("status", "同步输出失败（剪贴板被占用）：" + ex.Message); return; }
            }

            System.Threading.ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    Thread.Sleep(220);                             // 等目标窗口把输入框准备好
                    keybd_event(0x11, 0, 0, IntPtr.Zero);          // Ctrl 按下
                    keybd_event(0x56, 0, 0, IntPtr.Zero);          // V 按下
                    keybd_event(0x56, 0, 2, IntPtr.Zero);          // V 抬起
                    keybd_event(0x11, 0, 2, IntPtr.Zero);          // Ctrl 抬起
                    CallStr("status", "已同步到窗口：" + title);
                }
                catch (Exception ex) { CallStr("status", "同步输出失败：" + ex.Message); }
            });
        }


        /// <summary>JS 侧对含中文/换行的载荷统一做了 encodeURIComponent，这里还原</summary>
        private static string Un(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            try { return Uri.UnescapeDataString(s); }
            catch { return s; }
        }

        /// <summary>HTML 标题栏按下时拖动无边框窗口（WebView2 会吞掉窗体自身的鼠标事件）</summary>
        private void DragWindow()
        {
            try
            {
                ReleaseCapture();
                SendMessage(Handle, 0xA1, (IntPtr)2, IntPtr.Zero);
            }
            catch { }
        }

        // ============================================================ 导出 HTML（取代原「发送到游戏」）
        private void ExportHtml(string code)
        {
            code = code ?? "";
            using (SaveFileDialog d = new SaveFileDialog())
            {
                d.Filter = "HTML 文件|*.html|所有文件|*.*";
                d.FileName = "彩色文本生成器.html";
                if (d.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    string page =
                        "<!DOCTYPE html><html lang=\"zh-CN\"><head><meta charset=\"utf-8\">" +
                        "<title>彩色文本生成器 · 导出</title><style>" +
                        "body{margin:0;padding:24px;background:#0b0b0f;color:#f0f0f0;" +
                        "font:16px/1.7 'Segoe UI',system-ui,'Microsoft YaHei UI',sans-serif;white-space:pre-wrap;word-break:break-word}" +
                        "</style></head><body>" + RenderPreview(code) + "</body></html>";
                    File.WriteAllText(d.FileName, page, new UTF8Encoding(false));
                    CallStr("status", "已导出：" + d.FileName);
                }
                catch (Exception ex) { CallStr("status", "导出失败：" + ex.Message); }
            }
        }

        // ============================================================ 生成
        private void DoGenerate(string opts)
        {
            string text = Field(opts, "text");
            string mode = Field(opts, "mode");
            string color = Field(opts, "color");
            string size = Field(opts, "size");
            string frame = Field(opts, "frame");
            string bracket = Field(opts, "bracket");
            string prefix = Field(opts, "prefix");
            bool b = Flag(opts, "bold"), i = Flag(opts, "italic"), u = Flag(opts, "underline"), s = Flag(opts, "strike");
            bool sub = Flag(opts, "sub"), sup = Flag(opts, "sup"), mark = Flag(opts, "mark");
            bool grad = Flag(opts, "grad"), axial = Flag(opts, "axial"), loop = Flag(opts, "loop");
            string gs = Field(opts, "gradStart"), ge = Field(opts, "gradEnd");
            string link = Field(opts, "link"), linkText = Field(opts, "linkText");
            string markColor = Field(opts, "markColor");
            string alpha = Field(opts, "alpha");

            ColorOutputMode cm = mode == "rgb" ? ColorOutputMode.Rgb : (mode == "name" ? ColorOutputMode.Name : ColorOutputMode.Hex);

            // 颜色标签值
            Rgb rgb;
            if (!ColorUtil.TryParse(color, out rgb)) rgb = new Rgb(255, 255, 255);
            string colorVal = ColorUtil.ToTagValue(rgb, cm);

            List<ActiveTag> tags = new List<ActiveTag>();
            if (b) tags.Add(new ActiveTag(TagRegistry.ById("b"), ""));
            if (i) tags.Add(new ActiveTag(TagRegistry.ById("i"), ""));
            if (u) tags.Add(new ActiveTag(TagRegistry.ById("u"), ""));
            if (s) tags.Add(new ActiveTag(TagRegistry.ById("s"), ""));
            if (sub) tags.Add(new ActiveTag(TagRegistry.ById("sub"), ""));
            if (sup) tags.Add(new ActiveTag(TagRegistry.ById("sup"), ""));
            if (size.Trim().Length > 0) tags.Add(new ActiveTag(TagRegistry.ById("size"), size.Trim()));
            if (mark) tags.Add(new ActiveTag(TagRegistry.ById("mark"), ColorUtil.TryParse(markColor.Length > 0 ? markColor : color, out rgb) ? "#" + rgb.Hex + "80" : "#FFFF0080"));
            if (alpha.Trim().Length > 0) tags.Add(new ActiveTag(TagRegistry.ById("alpha"), "#" + alpha.Trim().TrimStart('#')));
            tags.Add(new ActiveTag(TagRegistry.ById("color"), colorVal));
            if (link.Trim().Length > 0)
            {
                ActiveTag lt = new ActiveTag(TagRegistry.ById("link"), link.Trim());
                lt.Extra = linkText.Trim();
                tags.Add(lt);
            }

            string[] lines = text.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');
            StringBuilder sb = new StringBuilder();
            for (int li = 0; li < lines.Length; li++)
            {
                string line = lines[li];
                if (line.Length == 0) continue;
                string content = line;
                if (grad)
                {
                    Rgb a1, b1;
                    if (!ColorUtil.TryParse(gs, out a1)) a1 = new Rgb(255, 0, 0);
                    if (!ColorUtil.TryParse(ge, out b1)) b1 = new Rgb(0, 0, 255);
                    StringBuilder gt = new StringBuilder();
                    int len = content.Length;
                    int period = loop ? 5 : len;
                    if (period <= 0) period = len;
                    for (int k = 0; k < len; k++)
                    {
                        int pos = loop ? (k % period) : k;
                        int span = loop ? period : len;
                        double t = span > 1 ? (double)pos / (span - 1) : 0;
                        if (axial) t = 1.0 - Math.Abs(2.0 * t - 1.0);
                        byte r = (byte)Math.Round(a1.R + (b1.R - a1.R) * t);
                        byte g = (byte)Math.Round(a1.G + (b1.G - a1.G) * t);
                        byte bl = (byte)Math.Round(a1.B + (b1.B - a1.B) * t);
                        gt.Append("<color=#").Append(r.ToString("X2")).Append(g.ToString("X2")).Append(bl.ToString("X2")).Append(">").Append(content[k]).Append("</color>");
                    }
                    content = gt.ToString();
                }
                if (frame == "bar") content = "▌" + content + "▌";
                else if (frame == "star") content = "★ " + content + " ★";
                string one = TagRegistry.Generate(content, tags);
                if (bracket == "（）") one = "（" + one + "）";
                else if (bracket == "【】") one = "【" + one + "】";
                else if (bracket == "「」") one = "「" + one + "」";
                if (sb.Length > 0) sb.Append("\n");
                sb.Append(one);
            }
            string code = sb.ToString();
            if (prefix.Length > 0 && code.Length > 0) code = prefix + " " + code;

            Call("result", "{\"code\":" + Quote(code) + ",\"preview\":" + Quote(RenderPreview(code)) + ",\"length\":" + code.Length + "}");
        }

        private static string Field(string json, string key)
        {
            Match m = Regex.Match(json ?? "", "\"" + key + "\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
            if (!m.Success) return "";
            return Unescape(m.Groups[1].Value);
        }

        /// <summary>按 JSON 规则从左到右还原转义（不能直接用 Replace 串联，否则 "C:\\new" 这类会被破坏）</summary>
        private static string Unescape(string v)
        {
            if (v.IndexOf('\\') < 0) return v;
            StringBuilder sb = new StringBuilder(v.Length);
            for (int i = 0; i < v.Length; i++)
            {
                char ch = v[i];
                if (ch != '\\' || i + 1 >= v.Length) { sb.Append(ch); continue; }
                char e = v[++i];
                switch (e)
                {
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case '/': sb.Append('/'); break;
                    case '"': sb.Append('"'); break;
                    case '\\': sb.Append('\\'); break;
                    case 'u':
                        if (i + 4 < v.Length)
                        {
                            int cp;
                            try { cp = Convert.ToInt32(v.Substring(i + 1, 4), 16); }
                            catch { cp = -1; }
                            if (cp >= 0) { sb.Append((char)cp); i += 4; break; }
                        }
                        sb.Append('u');
                        break;
                    default: sb.Append(e); break;
                }
            }
            return sb.ToString();
        }
        private static bool Flag(string json, string key)
        {
            return Regex.IsMatch(json ?? "", "\"" + key + "\"\\s*:\\s*true", RegexOptions.IgnoreCase);
        }

        /// <summary>生成代码 → HTML 预览（标签映射复用 PreviewHtml）</summary>
        private static string RenderPreview(string code)
        {
            StringBuilder sb = new StringBuilder();
            // 用 List 而非 Stack：闭合时要按标签名匹配，而不是无脑弹栈顶（否则手工粘贴的
            // 非规范嵌套会让 HTML 提前闭合、样式错乱）
            List<TagDef> stack = new List<TagDef>();
            string[] lines = code.Split('\n');
            for (int li = 0; li < lines.Length; li++)
            {
                string line = lines[li];
                int p = 0;
                while (p < line.Length)
                {
                    int lt = line.IndexOf('<', p);
                    if (lt < 0) { sb.Append(Esc(line.Substring(p))); break; }
                    if (lt > p) sb.Append(Esc(line.Substring(p, lt - p)));
                    int gt = line.IndexOf('>', lt);
                    if (gt < 0) { sb.Append(Esc(line.Substring(lt))); break; }
                    string token = line.Substring(lt + 1, gt - lt - 1);
                    p = gt + 1;
                    bool closing = token.StartsWith("/");
                    string body = closing ? token.Substring(1) : token;
                    string name = body, val = "";
                    int eq = body.IndexOf('=');
                    if (eq >= 0) { name = body.Substring(0, eq); val = body.Substring(eq + 1).Trim().Trim('"'); }
                    name = name.Trim().ToLowerInvariant();
                    TagDef def = TagRegistry.ById(name);
                    if (def == null) { sb.Append(Esc("<" + token + ">")); continue; }
                    if (closing)
                    {
                        int at = -1;
                        for (int k = stack.Count - 1; k >= 0; k--)
                        {
                            if (string.Equals(stack[k].TagName, name, StringComparison.OrdinalIgnoreCase)) { at = k; break; }
                        }
                        if (at < 0) continue;                       // 没有对应开标签 → 忽略
                        for (int k = stack.Count - 1; k >= at; k--) sb.Append(PreviewHtml.Close(stack[k]));
                        stack.RemoveRange(at, stack.Count - at);
                        continue;
                    }
                    sb.Append(PreviewHtml.Open(def, val));
                    if (def.Kind == TagKind.Wrap) stack.Add(def);
                }
                sb.Append("<br>");
            }
            for (int k = stack.Count - 1; k >= 0; k--) sb.Append(PreviewHtml.Close(stack[k]));
            return sb.ToString();
        }
        private static string Esc(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
        }

        // ============================================================ 文件
        // ---------- 记事本文档（概念设计 M2）：打开 / 保存 / 另存为 ----------
        private void OpenFile()
        {
            using (OpenFileDialog d = new OpenFileDialog())
            {
                d.Filter = "文本文件|*.txt;*.md;*.markdown;*.log;*.json;*.xml;*.ini;*.cfg;*.conf;*.csv;*.tsv;*.yml;*.yaml;*.html;*.htm;*.css;*.js;*.ts;*.cs;*.py;*.java;*.cpp;*.h;*.bat;*.cmd;*.ps1;*.sh;*.sql;*.ahk;*.lua;*.toml;*.nfo|所有文件|*.*";
                if (d.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    string txt = ReadText(d.FileName);
                    Call("fileOpened",
                        "{\"ok\":true,\"path\":" + Quote(d.FileName) +
                        ",\"name\":" + Quote(Path.GetFileName(d.FileName)) +
                        ",\"text\":" + Quote(txt) + "," + MetaJson(d.FileName).Substring(1));
                    CallStr("status", "已打开 " + Path.GetFileName(d.FileName) + "（" + txt.Length + " 字符）");
                }
                catch (Exception ex)
                {
                    Call("fileOpened", "{\"ok\":false,\"msg\":" + Quote("打开失败：" + ex.Message) + "}");
                }
            }
        }
        private void SaveFile(string arg)
        {
            // arg = encodeURIComponent(path) 或 "encodeURIComponent(path)|encodeURIComponent(text)"
            //（Windows 文件名不允许含 '|'，用第一个 '|' 切分安全；无路径 → 走另存为弹窗）
            string path = "", text = "";
            int sep = arg.IndexOf('|');
            if (sep < 0) { text = Un(arg); }
            else { path = Un(arg.Substring(0, sep)); text = Un(arg.Substring(sep + 1)); }
            if (path.Length == 0) { SaveAs(text); return; }
            try
            {
                string lab = WriteText(path, text);
                Call("fileSaved", "{\"ok\":true,\"path\":" + Quote(path) +
                     ",\"name\":" + Quote(Path.GetFileName(path)) +
                     ",\"encoding\":" + Quote(lab) + "}");
            }
            catch (Exception ex)
            {
                Call("fileSaved", "{\"ok\":false,\"msg\":" + Quote("保存失败：" + ex.Message) + "}");
            }
        }
        private void SaveAsFile(string arg)
        {
            // arg = encodeURIComponent(name) + "|" + encodeURIComponent(text)
            string name = "", text;
            int sep = arg.IndexOf('|');
            if (sep < 0) { text = Un(arg); }
            else { name = Un(arg.Substring(0, sep)); text = Un(arg.Substring(sep + 1)); }
            SaveAs(text, name);
        }
        private void SaveAs(string text) { SaveAs(text, ""); }
        private void SaveAs(string text, string name)
        {
            using (SaveFileDialog d = new SaveFileDialog())
            {
                d.Filter = "文本文件|*.txt|Markdown|*.md|日志|*.log|所有文件|*.*";
                d.FilterIndex = 1;
                d.FileName = string.IsNullOrEmpty(name) ? "无标题.txt" : name;
                if (d.ShowDialog(this) != DialogResult.OK) { Call("fileSaved", "{\"ok\":false,\"msg\":\"已取消\"}"); return; }
                try
                {
                    string lab = WriteText(d.FileName, text);
                    Call("fileSaved", "{\"ok\":true,\"path\":" + Quote(d.FileName) +
                         ",\"name\":" + Quote(Path.GetFileName(d.FileName)) +
                         ",\"encoding\":" + Quote(lab) + "}");
                }
                catch (Exception ex)
                {
                    Call("fileSaved", "{\"ok\":false,\"msg\":" + Quote("保存失败：" + ex.Message) + "}");
                }
            }
        }
        /// <summary>
        /// 保存文本：已打开过的文件沿用原编码与原换行符（GBK 文件不会被悄悄改成 UTF-8）；
        /// 新文件统一 UTF-8（不带 BOM），换行符保持界面里的实际内容。返回编码标签供状态栏显示。
        /// </summary>
        private static string WriteText(string path, string text)
        {
            text = text ?? "";
            FileMeta m;
            string full;
            try { full = Path.GetFullPath(path); } catch { full = path; }
            if (!_fileMeta.TryGetValue(full, out m) || m == null)
            {
                m = new FileMeta();
                m.Enc = new UTF8Encoding(false);
                m.Bom = false;
                m.Label = "UTF-8";
                m.Eol = text.IndexOf("\r\n", StringComparison.Ordinal) >= 0 ? "CRLF" : "LF";
            }

            // 换行符回写成原文件的风格
            string body = text.Replace("\r\n", "\n").Replace('\r', '\n');
            if (m.Eol == "CRLF") body = body.Replace("\n", "\r\n");
            else if (m.Eol == "CR") body = body.Replace('\n', '\r');

            Encoding enc = m.Enc ?? new UTF8Encoding(false);
            if (m.Bom)
            {
                byte[] preamble = enc.GetPreamble();
                byte[] data = enc.GetBytes(body);
                byte[] all = new byte[preamble.Length + data.Length];
                Buffer.BlockCopy(preamble, 0, all, 0, preamble.Length);
                Buffer.BlockCopy(data, 0, all, preamble.Length, data.Length);
                File.WriteAllBytes(path, all);
            }
            else
            {
                File.WriteAllBytes(path, enc.GetBytes(body));
            }
            try { _fileMeta[full] = m; } catch { }
            return m.Label;
        }
        private void ImportTxt()
        {
            using (OpenFileDialog d = new OpenFileDialog())
            {
                d.Filter = "文本文件|*.txt;*.md;*.markdown;*.log;*.json;*.xml;*.ini;*.cfg;*.conf;*.csv;*.tsv;*.yml;*.yaml;*.html;*.htm;*.css;*.js;*.ts;*.cs;*.py;*.java;*.cpp;*.h;*.bat;*.cmd;*.ps1;*.sh;*.sql;*.ahk;*.lua;*.toml;*.nfo|所有文件|*.*";
                if (d.ShowDialog(this) != DialogResult.OK) return;
                SendText(d.FileName);
            }
        }
        private void DropFile(string path)
        {
            path = path.Trim('"');
            if (!File.Exists(path)) { CallStr("status", "文件不存在：" + path); return; }
            SendText(path);
        }
        private void SendText(string path)
        {
            try
            {
                string txt = ReadText(path);
                Call("textIn", "{\"text\":" + Quote(txt) + ",\"name\":" + Quote(Path.GetFileName(path)) + "}");
                CallStr("status", "已导入 " + Path.GetFileName(path) + "（" + txt.Length + " 字符）");
            }
            catch (Exception ex) { CallStr("status", "导入失败：" + ex.Message); }
        }
        /// <summary>把某个标签从当前窗口「撕」出来，在本进程里新建一个独立窗口承载它。</summary>
        private void TearTab(string json)
        {
            try
            {
                json = (json ?? "").Trim();
                if (json.Length < 2 || !json.StartsWith("{")) return;

                Point cursorPos = Cursor.Position;
                MainForm w = new MainForm(true, json);
                Rectangle wa = Screen.FromPoint(cursorPos).WorkingArea;
                w.StartPosition = FormStartPosition.Manual;
                int x = Math.Min(cursorPos.X + 28, wa.Right - w.Width);
                int y = Math.Min(cursorPos.Y + 28, wa.Bottom - w.Height);
                w.Location = new Point(Math.Max(wa.Left, x), Math.Max(wa.Top, y));
                w.Show();
                w.Activate();
            }
            catch (Exception ex) { CallStr("status", "新建窗口失败：" + ex.Message); }
        }

        /// <summary>按路径打开文件为记事本新标签（Shell 右键/命令行；无对话框）</summary>
        private void OpenPath(string path)
        {
            path = (path ?? "").Trim().Trim('"');
            if (path.Length == 0) return;
            if (!File.Exists(path)) { CallStr("status", "文件不存在：" + path); return; }
            try
            {
                string txt = ReadText(path);
                Call("fileOpened",
                    "{\"ok\":true,\"path\":" + Quote(path) +
                    ",\"name\":" + Quote(Path.GetFileName(path)) +
                    ",\"text\":" + Quote(txt) + "," + MetaJson(path).Substring(1));
                CallStr("status", "已打开 " + Path.GetFileName(path) + "（" + txt.Length + " 字符）");
            }
            catch (Exception ex) { CallStr("status", "打开失败：" + ex.Message); }
        }
        /// <summary>已打开文件的编码/换行信息：保存时原样写回，避免 GBK 文件被悄悄改成 UTF-8。</summary>
        private sealed class FileMeta
        {
            public Encoding Enc = new UTF8Encoding(false);
            public bool Bom;
            public string Eol = "LF";
            public string Label = "UTF-8";
        }
        private static readonly Dictionary<string, FileMeta> _fileMeta =
            new Dictionary<string, FileMeta>(StringComparer.OrdinalIgnoreCase);

        private static string ReadText(string path)
        {
            byte[] b = File.ReadAllBytes(path);
            FileMeta m = new FileMeta();
            string text;
            if (b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF)
            {
                m.Enc = new UTF8Encoding(false); m.Bom = true; m.Label = "UTF-8 BOM";
                text = Encoding.UTF8.GetString(b, 3, b.Length - 3);
            }
            else if (b.Length >= 2 && b[0] == 0xFF && b[1] == 0xFE)
            {
                m.Enc = Encoding.Unicode; m.Bom = true; m.Label = "UTF-16 LE";
                text = Encoding.Unicode.GetString(b, 2, b.Length - 2);
            }
            else if (b.Length >= 2 && b[0] == 0xFE && b[1] == 0xFF)
            {
                m.Enc = Encoding.BigEndianUnicode; m.Bom = true; m.Label = "UTF-16 BE";
                text = Encoding.BigEndianUnicode.GetString(b, 2, b.Length - 2);
            }
            else
            {
                try
                {
                    text = new UTF8Encoding(false, true).GetString(b);
                    m.Enc = new UTF8Encoding(false); m.Label = "UTF-8";
                }
                catch
                {
                    text = Encoding.GetEncoding(936).GetString(b);
                    m.Enc = Encoding.GetEncoding(936); m.Label = "GBK";
                }
            }
            m.Eol = text.IndexOf("\r\n", StringComparison.Ordinal) >= 0 ? "CRLF"
                  : (text.IndexOf('\r') >= 0 ? "CR" : "LF");
            try { _fileMeta[Path.GetFullPath(path)] = m; } catch { }
            return text;
        }

        /// <summary>取某个已打开文件的编码信息（供界面状态栏显示）。</summary>
        private static string MetaJson(string path)
        {
            FileMeta m;
            try { if (!_fileMeta.TryGetValue(Path.GetFullPath(path), out m)) m = null; }
            catch { m = null; }
            if (m == null) return "{\"encoding\":\"UTF-8\",\"eol\":\"LF\"}";
            return "{\"encoding\":" + Quote(m.Label) + ",\"eol\":" + Quote(m.Eol) + "}";
        }
        private void ExportTxt(string text)
        {
            using (SaveFileDialog d = new SaveFileDialog())
            {
                d.Filter = "文本文件|*.txt";
                d.FileName = "彩色文本生成器导出.txt";
                if (d.ShowDialog(this) != DialogResult.OK) return;
                try { File.WriteAllText(d.FileName, text, new UTF8Encoding(true)); CallStr("status", "已导出：" + d.FileName); }
                catch (Exception ex) { CallStr("status", "导出失败：" + ex.Message); }
            }
        }

        // ============================================================ 更新
        // 双通道：Cloudflare 下载站（双线路）与 GitHub Releases。
        // 自动检查时两条通道并发计时，谁快用谁；用户手动点「立即检测更新」时默认直接走 Cloudflare。
        private const string UpdateSite = "https://richtextgen-dl.pages.dev/";
        private static readonly string[] UpdateMirrors = { "https://richtextgen-dl.pages.dev/", "https://richtextgen-dl2.pages.dev/" };
        private static readonly string[] GithubManifests =
        {
            "https://raw.githubusercontent.com/lll111III11/RichTextGen/main/version.json",
            "https://cdn.jsdelivr.net/gh/lll111III11/RichTextGen@main/version.json"
        };
        private const string GithubSetup = "https://github.com/lll111III11/RichTextGen/releases/latest/download/RichTextGen-Setup.exe";
        private static string _mirror;
        private static DateTime _mirrorAt;
        /// <summary>安装包候选地址，第一个是当前通道，后面的按优先级兜底</summary>
        private static string[] _setupCandidates = { GithubSetup };

        /// <summary>对每条镜像的 files/probe.txt 计时，取最快的一条；结果缓存 10 分钟</summary>
        private static string PickMirror()
        {
            if (_mirror != null && (DateTime.UtcNow - _mirrorAt).TotalMinutes < 10) return _mirror;
            string best = UpdateMirrors[0];
            double bestMs = double.MaxValue;
            foreach (string b in UpdateMirrors)
            {
                DateTime t0 = DateTime.UtcNow;
                string probe = Http(b + "files/probe.txt?t=" + DateTime.Now.Ticks, 4000);
                double ms = (DateTime.UtcNow - t0).TotalMilliseconds;
                if (probe.Length > 0 && ms < bestMs) { bestMs = ms; best = b; }
            }
            _mirror = best; _mirrorAt = DateTime.UtcNow;
            return best;
        }

        private sealed class UpdateProbe
        {
            public string Channel;   // cloudflare / github
            public string Body;
            public int Ms;
        }

        /// <summary>拉取指定通道的更新清单并计时；不可达时返回 null</summary>
        private static UpdateProbe ProbeChannel(string channel)
        {
            string[] urls = channel == "cloudflare" ? new[] { UpdateSite + "releases.json" } : GithubManifests;
            foreach (string raw in urls)
            {
                string url = raw + (raw.IndexOf('?') >= 0 ? "&" : "?") + "t=" + DateTime.Now.Ticks;
                DateTime t0 = DateTime.UtcNow;
                string body = Http(url, 6000);
                int ms = (int)(DateTime.UtcNow - t0).TotalMilliseconds;
                if (string.IsNullOrEmpty(body) || body.IndexOf("\"version\"", StringComparison.Ordinal) < 0) continue;
                return new UpdateProbe { Channel = channel, Body = body, Ms = ms };
            }
            return null;
        }

        /// <param name="manual">true = 用户点击「立即检测更新」，无论结果如何都要在界面上给出可见反馈</param>
        private void CheckUpdate(bool manual)
        {
            if (manual) CallStr("upInfo", "正在检测更新…");
            System.Threading.ThreadPool.QueueUserWorkItem(delegate
            {
                UpdateProbe cf = null, gh = null;
                Thread tCf = new Thread(new ThreadStart(delegate { cf = ProbeChannel("cloudflare"); }));
                Thread tGh = new Thread(new ThreadStart(delegate { gh = ProbeChannel("github"); }));
                tCf.IsBackground = true; tGh.IsBackground = true;
                tCf.Start(); tGh.Start();
                tCf.Join(8000); tGh.Join(8000);

                // 手动检测默认 Cloudflare；自动检测按实测延迟取快的一条；只有一条可用时用它
                UpdateProbe best;
                if (manual) best = cf ?? gh;
                else if (cf != null && gh != null) best = cf.Ms <= gh.Ms ? cf : gh;
                else best = cf ?? gh;

                if (best == null) { FailUpdate(manual, "更新检查失败（两条通道都不可达）"); return; }

                string ver = Field(best.Body, "version");
                string notes = Field(best.Body, "notes");
                string type = Field(best.Body, "type");
                if (type.Length == 0) type = "feature";
                bool mandatory = Regex.IsMatch(best.Body, "\"mandatory\"\\s*:\\s*true", RegexOptions.IgnoreCase);
                if (ver.Length == 0) { FailUpdate(manual, "更新检查失败（清单格式异常）"); return; }

                string cfSetup = PickMirror() + "files/RichTextGen-Setup.exe";
                string ghSetup = Field(best.Body, "installer");
                if (ghSetup.Length == 0) ghSetup = GithubSetup;
                // 选中通道排第一，另一条留作下载失败时的兜底
                _setupCandidates = best.Channel == "cloudflare"
                    ? new[] { cfSetup, GithubSetup }
                    : new[] { ghSetup, cfSetup };
                string url = _setupCandidates[0];
                string via = (best.Channel == "cloudflare" ? "Cloudflare" : "GitHub") + " " + best.Ms + "ms";

                if (Newer(ver, Version))
                {
                    // 记下清单内容：更新成功后要写进 pending_update.json，供下次启动弹「更新内容」
                    pendingVer = ver; pendingNotes = notes; pendingType = type; pendingUrl = url;
                    Call("update", "{" + Quote("version") + ":" + Quote(ver) + "," + Quote("url") + ":" + Quote(url) + "," + Quote("notes") + ":" + Quote(notes) + "," + Quote("type") + ":" + Quote(type) + "," + Quote("mandatory") + ":" + (mandatory ? "true" : "false") + "}");
                    CallStr("status", "发现新版本 " + ver + "（当前 " + Version + "，走 " + via + "）");
                    if (manual) CallStr("upInfo", "发现新版本 " + ver + "（当前 " + Version + "，走 " + via + " 通道）");
                }
                else
                {
                    CallStr("status", "已是最新版本 " + Version);
                    if (manual) CallStr("upInfo", "已是最新版本 v" + Version + "（" + DateTime.Now.ToString("HH:mm:ss") + " 检测，走 " + via + "）");
                }
            });
        }
        /// <summary>更新检查失败：状态栏始终提示；手动检测时 #upInfo 也要显示，避免按钮"点了没反应"</summary>
        private void FailUpdate(bool manual, string why)
        {
            CallStr("status", why);
            if (manual) CallStr("upInfo", why + "，请检查网络后重试");
        }
        private static bool Newer(string remote, string local)
        {
            int[] a = Ver(remote), b = Ver(local);
            for (int i = 0; i < 4; i++) { if (a[i] > b[i]) return true; if (a[i] < b[i]) return false; }
            return false;
        }
        private static int[] Ver(string v)
        {
            int[] r = new int[4];
            string[] p = (v ?? "").Split('.');
            for (int i = 0; i < 4 && i < p.Length; i++) { int n; int.TryParse(Regex.Replace(p[i], "[^0-9]", ""), out n); r[i] = n; }
            return r;
        }
        private void DoUpdate()
        {
            if (pendingVer.Length == 0) { CallStr("status", "请先「检查更新」，确认有新版本后再更新"); return; }
            // 按通道优先级依次尝试：选中的通道排第一，另一条兜底
            string[] urls = (_setupCandidates != null && _setupCandidates.Length > 0)
                ? _setupCandidates
                : new[] { pendingUrl.Length > 0 ? pendingUrl : GithubSetup };
            System.Threading.ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    CallStr("status", "正在下载更新包…");
                    string path = Path.Combine(Path.GetTempPath(), "RichTextGen-Setup-" + DateTime.Now.ToString("HHmmss") + ".exe");
                    Exception last = null;
                    using (WebClient wc = new WebClient())
                    {
                        wc.Headers.Add("User-Agent", "RichTextGen/" + Version);
                        foreach (string u in urls)
                        {
                            if (u == null || u.Length == 0) continue;
                            try { wc.DownloadFile(u, path); last = null; break; }
                            catch (Exception ex) { last = ex; }
                        }
                    }
                    if (last != null) throw last;
                    try
                    {
                        string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RichTextGen");
                        Directory.CreateDirectory(dir);
                        File.WriteAllText(Path.Combine(dir, "pending_update.json"), "{" + Quote("version") + ":" + Quote(pendingVer) + "," + Quote("from") + ":" + Quote(Version) + "," + Quote("notes") + ":" + Quote(pendingNotes) + "," + Quote("type") + ":" + Quote(pendingType) + "}", Encoding.UTF8);
                    }
                    catch { }
                    CallStr("status", "下载完成，正在启动安装程序…");
                    try { System.Diagnostics.Process.Start(path); } catch { }
                    try { BeginInvoke((Action)delegate { _allowClose = true; Close(); }); } catch { }
                }
                catch (Exception ex) { CallStr("status", "更新失败：" + ex.Message); }
            });
        }
        private static string Http(string url, int ms)
        {
            try
            {
                ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12 | SecurityProtocolType.Tls11 | SecurityProtocolType.Tls;
                HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
                req.Timeout = ms; req.ReadWriteTimeout = ms; req.UserAgent = "RichTextGen/" + Version;
                using (WebResponse resp = req.GetResponse())
                using (Stream s = resp.GetResponseStream())
                using (StreamReader sr = new StreamReader(s, Encoding.UTF8))
                    return sr.ReadToEnd();
            }
            catch { return ""; }
        }

        // ============================================================ AI 对话（自定义模型，OpenAI 兼容协议）
        /// <summary>简易聊天：用户自定义 Endpoint / API Key / 模型，把历史对话发给 /chat/completions</summary>
        private void Chat(string json)
        {
            System.Threading.ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    string endpoint = Field(json, "endpoint").Trim();
                    string key = Field(json, "key").Trim();
                    string model = Field(json, "model").Trim();
                    string history = Field(json, "history");           // JS 序列化的消息数组（JSON 字符串）
                    if (history.Length == 0)
                    {
                        // 兜底：若 history 以数组形态传回，直接从原文截取
                        Match ra = Regex.Match(json ?? "", "\"history\"\\s*:\\s*(\\[[\\s\\S]*?\\])\\s*[,}]");
                        if (ra.Success) history = ra.Groups[1].Value;
                    }
                    if (history.Length == 0 || !history.TrimStart().StartsWith("["))
                    {
                        Call("chatReply", "{\"ok\":false,\"text\":" + Quote("对话内容为空，请先输入问题") + "}");
                        return;
                    }
                    if (key.Length == 0) { Call("chatReply", "{\"ok\":false,\"text\":" + Quote("尚未填写 API Key（请在「AI 对话」页填入你自己的密钥）") + "}"); return; }
                    if (endpoint.Length == 0) endpoint = "https://api.deepseek.com";
                    if (!endpoint.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
                        endpoint = endpoint.TrimEnd('/') + "/chat/completions";
                    if (model.Length == 0) model = "deepseek-chat";
                    string body = "{\"model\":" + Quote(model) + ",\"messages\":" + history + ",\"max_tokens\":2048}";
                    string resp = HttpPost(endpoint, key, body, 60000);
                    if (resp.Length == 0) { Call("chatReply", "{\"ok\":false,\"text\":" + Quote("网络不可达或 API 无响应，请检查网络与密钥") + "}"); return; }
                    string reply = Regex.Match(resp, "\"content\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"").Groups[1].Value;
                    if (reply.Length == 0)
                    {
                        // 推理模型可能思考完才给答案；无 content 时优先取接口错误信息
                        bool thinking = resp.IndexOf("reasoning_content", StringComparison.Ordinal) >= 0;
                        if (thinking) { Call("chatReply", "{\"ok\":false,\"text\":" + Quote("模型思考超长未返回答案，请缩短文本或拆成多次提问") + "}"); return; }
                        string err = Regex.Match(resp, "\"message\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"").Groups[1].Value;
                        if (err.Length == 0) err = resp.Length > 200 ? resp.Substring(0, 200) + "…" : resp;
                        Call("chatReply", "{\"ok\":false,\"text\":" + Quote("API 返回异常：" + err) + "}");
                        return;
                    }
                    Call("chatReply", "{\"ok\":true,\"text\":" + Quote(Unescape(reply)) + "}");
                    CallStr("status", "AI 回复完成");
                }
                catch (Exception ex) { Call("chatReply", "{\"ok\":false,\"text\":" + Quote("请求出错：" + ex.Message) + "}"); }
            });
        }

        // ============================================================ 小彩蛋：点联网 5 次 → 网络音效
        /// <summary>随机从网上取一个音效（下载到临时目录 → SoundPlayer 播放 → 删除）</summary>
        private void PlayNetSound()
        {
            System.Threading.ThreadPool.QueueUserWorkItem(delegate
            {
                string[] urls =
                {
                    "https://raw.githubusercontent.com/lll111III11/RichTextGen/main/sounds/ding.wav",
                    "https://raw.githubusercontent.com/lll111III11/RichTextGen/main/sounds/chirp.wav",
                    "https://raw.githubusercontent.com/lll111III11/RichTextGen/main/sounds/notify.wav"
                };
                string path = "";
                try
                {
                    string url = urls[new Random().Next(urls.Length)];
                    path = Path.Combine(Path.GetTempPath(), "rtg_net_" + DateTime.Now.ToString("HHmmssfff") + ".wav");
                    using (WebClient wc = new WebClient())
                    {
                        wc.Headers.Add("User-Agent", "RichTextGen/" + Version);
                        wc.DownloadFile(url, path);
                    }
                    using (System.Media.SoundPlayer sp = new System.Media.SoundPlayer(path)) sp.PlaySync();
                    CallStr("status", "🎵 网络音效（彩蛋达成）");
                }
                catch { CallStr("status", "音效获取失败（网络不通？）"); }
                finally { try { if (path.Length > 0 && File.Exists(path)) File.Delete(path); } catch { } }
            });
        }

        // ============================================================ 音乐本地链接服务（127.0.0.1 端口）
        // 把拖进来的音乐文件挂到本机一个端口上，生成可直接播放的本地 URL（http://127.0.0.1:端口/media/文件名）。
        // 用 TcpListener 手写 HTTP/1.1（不用 HttpListener）：免管理员、免 urlacl 预留、免防火墙弹窗（只绑回环地址）。
        private static TcpListener _mediaListener;
        private static int _mediaPort;
        private static readonly Dictionary<string, string> _mediaMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private static readonly object _mediaLock = new object();

        /// <summary>首次使用时在 127.0.0.1 上挑一个空闲端口启动服务；全部被占用返回 0</summary>
        private static int EnsureMediaServer()
        {
            if (_mediaListener != null) return _mediaPort;
            lock (_mediaLock)
            {
                if (_mediaListener != null) return _mediaPort;
                for (int port = 17861; port < 17961; port++)
                {
                    try
                    {
                        TcpListener l = new TcpListener(IPAddress.Loopback, port);
                        l.Start();
                        _mediaListener = l;
                        _mediaPort = port;
                        break;
                    }
                    catch { }   // 端口被占用 → 试下一个
                }
                if (_mediaListener == null) return 0;
                System.Threading.Thread t = new System.Threading.Thread(MediaAcceptLoop);
                t.IsBackground = true;      // 后台线程：随主进程退出，不阻塞关闭
                t.Start();
                return _mediaPort;
            }
        }

        private static void MediaAcceptLoop()
        {
            while (true)
            {
                TcpClient c;
                try { c = _mediaListener.AcceptTcpClient(); }
                catch { return; }           // 监听已停止
                System.Threading.ThreadPool.QueueUserWorkItem(delegate
                {
                    try { ServeMedia(c); }
                    catch { }
                    finally { try { c.Close(); } catch { } }
                });
            }
        }

        /// <summary>把文件登记进服务并返回可访问的本地 URL（同名文件自动加序号区分）</summary>
        private static string RegisterMedia(string path)
        {
            int port = EnsureMediaServer();
            if (port == 0) return "";
            string name = Path.GetFileName(path);
            lock (_mediaLock)
            {
                string key = name;
                for (int i = 2; _mediaMap.ContainsKey(key); i++)
                    key = Path.GetFileNameWithoutExtension(name) + "-" + i + Path.GetExtension(name);
                _mediaMap[key] = path;
                return "http://127.0.0.1:" + port + "/media/" + Uri.EscapeDataString(key);
            }
        }

        /// <summary>极简 HTTP 服务：只处理 GET/HEAD /media/xxx，支持 Range（播放器拖进度条要 206）</summary>
        private static void ServeMedia(TcpClient c)
        {
            c.ReceiveTimeout = 15000;
            c.SendTimeout = 30000;
            NetworkStream ns = c.GetStream();

            // ---- 读请求头（读到空行为止，最多 16KB，防恶意超长头） ----
            MemoryStream head = new MemoryStream();
            byte[] one = new byte[1];
            int blank = 0;
            while (head.Length < 16384)
            {
                int n = ns.Read(one, 0, 1);
                if (n <= 0) break;
                head.WriteByte(one[0]);
                if (one[0] == (byte)'\n') { if (++blank == 2) break; }
                else if (one[0] != (byte)'\r') blank = 0;
            }
            string[] lines = Encoding.ASCII.GetString(head.ToArray())
                .Split(new string[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length == 0) { WriteMediaError(ns, 400); return; }

            string[] parts = lines[0].Split(' ');
            if (parts.Length < 2) { WriteMediaError(ns, 400); return; }
            string method = parts[0];
            string rawPath = parts[1];
            int q = rawPath.IndexOf('?');
            if (q >= 0) rawPath = rawPath.Substring(0, q);

            // ---- 解析 Range: bytes=start-end ----
            long from = 0, to = -1;
            bool hasRange = false;
            foreach (string ln in lines)
            {
                if (ln.Length > 6 && ln.StartsWith("Range:", StringComparison.OrdinalIgnoreCase))
                {
                    string v = ln.Substring(6).Trim();
                    int eq = v.IndexOf('=');
                    if (eq >= 0) v = v.Substring(eq + 1);
                    string[] r = v.Split('-');
                    if (r.Length == 2)
                    {
                        if (r[0].Length > 0 && long.TryParse(r[0], out from)) hasRange = true;
                        if (r[1].Length > 0 && long.TryParse(r[1], out to)) hasRange = true;
                        if (from < 0) { from = 0; hasRange = false; }
                    }
                    break;
                }
            }

            const string prefix = "/media/";
            if (!rawPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) { WriteMediaError(ns, 404); return; }
            string key;
            try { key = Uri.UnescapeDataString(rawPath.Substring(prefix.Length)); }
            catch { key = rawPath.Substring(prefix.Length); }

            string file;
            lock (_mediaLock) { _mediaMap.TryGetValue(key, out file); }
            if (string.IsNullOrEmpty(file) || !File.Exists(file)) { WriteMediaError(ns, 404); return; }

            long total = new FileInfo(file).Length;
            long start = hasRange ? from : 0;
            if (to < 0 || to >= total) to = total - 1;
            if (hasRange && (from > to || from >= total)) { WriteMediaError(ns, 416); return; }
            long count = hasRange ? (to - start + 1) : total;

            StringBuilder sb = new StringBuilder();
            sb.Append(hasRange ? "HTTP/1.1 206 Partial Content\r\n" : "HTTP/1.1 200 OK\r\n");
            sb.Append("Content-Type: ").Append(MediaContentType(file)).Append("\r\n");
            sb.Append("Content-Length: ").Append(count).Append("\r\n");
            sb.Append("Accept-Ranges: bytes\r\n");
            if (hasRange)
                sb.Append("Content-Range: bytes ").Append(start).Append("-").Append(to).Append("/").Append(total).Append("\r\n");
            sb.Append("Access-Control-Allow-Origin: *\r\n");
            sb.Append("Cache-Control: no-cache\r\n");
            sb.Append("Connection: close\r\n\r\n");
            byte[] hb = Encoding.ASCII.GetBytes(sb.ToString());
            ns.Write(hb, 0, hb.Length);
            if (method == "HEAD") return;

            using (FileStream fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                fs.Seek(start, SeekOrigin.Begin);
                byte[] buf = new byte[64 * 1024];
                long left = count;
                while (left > 0)
                {
                    int n = fs.Read(buf, 0, (int)Math.Min(buf.Length, left));
                    if (n <= 0) break;
                    ns.Write(buf, 0, n);
                    left -= n;
                }
            }
        }

        private static void WriteMediaError(NetworkStream ns, int code)
        {
            string text = code == 404 ? "Not Found" : (code == 416 ? "Range Not Satisfiable" : "Bad Request");
            byte[] b = Encoding.UTF8.GetBytes(code + " " + text);
            string h = "HTTP/1.1 " + code + " " + text + "\r\nContent-Type: text/plain; charset=utf-8\r\n" +
                       "Content-Length: " + b.Length + "\r\nConnection: close\r\n\r\n";
            byte[] hb = Encoding.ASCII.GetBytes(h);
            ns.Write(hb, 0, hb.Length);
            ns.Write(b, 0, b.Length);
        }

        private static string MediaContentType(string path)
        {
            switch (Path.GetExtension(path).ToLowerInvariant())
            {
                case ".mp3": return "audio/mpeg";
                case ".wav": return "audio/wav";
                case ".ogg": case ".opus": return "audio/ogg";
                case ".m4a": case ".mp4": return "audio/mp4";   // m4a 本质是 MP4 容器
                case ".aac": return "audio/aac";
                case ".flac": return "audio/flac";
                case ".wma": return "audio/x-ms-wma";
                case ".webm": return "video/webm";
                default: return "application/octet-stream";
            }
        }

        // ============================================================ 音乐播放列表（含带声音的 MP4）
        private static string MusicCfgPath { get { return Path.Combine(AiConfig.Dir, "music.json"); } }

        private bool HasTrack(string p)
        {
            foreach (string t in _playlist)
                if (string.Equals(t, p, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private void LoadMusic()
        {
            try
            {
                if (!File.Exists(MusicCfgPath)) return;
                string s = File.ReadAllText(MusicCfgPath, Encoding.UTF8);
                Match mi = Regex.Match(s, "\"index\"\\s*:\\s*(-?\\d+)");
                int idx = mi.Success ? int.Parse(mi.Groups[1].Value) : -1;
                Match mt = Regex.Match(s, "\"tracks\"\\s*:\\s*\\[([\\s\\S]*?)\\]");
                if (mt.Success)
                {
                    foreach (Match m in Regex.Matches(mt.Groups[1].Value, "\"((?:[^\"\\\\]|\\\\.)*)\""))
                    {
                        string p = Unescape(m.Groups[1].Value);
                        if (p.Length > 0 && File.Exists(p) && !HasTrack(p)) _playlist.Add(p);
                    }
                }
                if (_playlist.Count > 0) _musicIndex = (idx >= 0 && idx < _playlist.Count) ? idx : 0;
            }
            catch { }
        }

        private void SaveMusic()
        {
            try
            {
                StringBuilder sb = new StringBuilder("{\"index\":").Append(_musicIndex).Append(",\"tracks\":[");
                for (int i = 0; i < _playlist.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append(Quote(_playlist[i]));
                }
                sb.Append("]}");
                Directory.CreateDirectory(AiConfig.Dir);
                File.WriteAllText(MusicCfgPath, sb.ToString(), new UTF8Encoding(false));
            }
            catch { }
        }

        /// <summary>加入播放列表：第一首加入后自动开始播放，之后只排队不打断当前曲目。</summary>
        private void MusicAddPaths(string[] paths)
        {
            int added = 0;
            if (paths != null)
            {
                foreach (string raw in paths)
                {
                    string p = (raw ?? "").Trim().Trim('"');
                    if (p.Length == 0 || !File.Exists(p) || HasTrack(p)) continue;
                    _playlist.Add(p);
                    added++;
                }
            }
            if (added > 0)
            {
                if (_musicIndex < 0) { _musicIndex = 0; _musicPlaying = true; }
                SaveMusic();
            }
            PushMusicState();
            CallStr("status", added > 0
                ? ("已添加 " + added + " 首，共 " + _playlist.Count + " 首")
                : "没有新增音乐");
        }

        private void MusicPick()
        {
            using (OpenFileDialog d = new OpenFileDialog())
            {
                d.Filter = "音乐 / 视频|*.mp3;*.wav;*.ogg;*.m4a;*.flac;*.aac;*.wma;*.opus;" +
                           "*.mp4;*.m4v;*.webm;*.mov;*.mkv;*.avi|所有文件|*.*";
                d.Multiselect = true;
                d.Title = "添加音乐（可多选；带声音的 MP4 也可以）";
                if (d.ShowDialog(this) != DialogResult.OK) return;
                MusicAddPaths(d.FileNames);
            }
        }

        private void MusicPlay(int index)
        {
            if (index < 0 || index >= _playlist.Count) return;
            _musicIndex = index;
            _musicPlaying = true;
            SaveMusic();
            PushMusicState();
            CallStr("status", "音乐：" + Path.GetFileName(_playlist[_musicIndex]));
        }

        private void MusicToggle()
        {
            if (_playlist.Count == 0) { CallStr("status", "播放列表为空：在「音乐」页添加文件"); return; }
            if (_musicIndex < 0) _musicIndex = 0;
            _musicPlaying = !_musicPlaying;
            SaveMusic();
            PushMusicState();
            CallStr("status", _musicPlaying
                ? "正在播放：" + Path.GetFileName(_playlist[_musicIndex])
                : "已暂停");
        }

        private void MusicNext()
        {
            if (_playlist.Count == 0) { CallStr("status", "播放列表为空：在「音乐」页添加文件"); return; }
            _musicIndex = _musicIndex < 0 ? 0 : (_musicIndex + 1) % _playlist.Count;
            _musicPlaying = true;
            SaveMusic();
            PushMusicState();
            CallStr("status", "音乐：" + Path.GetFileName(_playlist[_musicIndex]));
        }

        private void MusicPrev()
        {
            if (_playlist.Count == 0) { CallStr("status", "播放列表为空：在「音乐」页添加文件"); return; }
            _musicIndex = _musicIndex <= 0 ? _playlist.Count - 1 : _musicIndex - 1;
            _musicPlaying = true;
            SaveMusic();
            PushMusicState();
            CallStr("status", "音乐：" + Path.GetFileName(_playlist[_musicIndex]));
        }

        private void MusicRemove(int index)
        {
            if (index < 0 || index >= _playlist.Count) return;
            _playlist.RemoveAt(index);
            if (_playlist.Count == 0) { _musicIndex = -1; _musicPlaying = false; }
            else if (index < _musicIndex) _musicIndex--;
            else if (index == _musicIndex) _musicIndex = Math.Min(_musicIndex, _playlist.Count - 1);
            SaveMusic();
            PushMusicState();
        }

        private void MusicClear()
        {
            _playlist.Clear();
            _musicIndex = -1;
            _musicPlaying = false;
            SaveMusic();
            PushMusicState();
            CallStr("status", "已清空播放列表");
        }

        private void PushMusicState()
        {
            string url = (_musicIndex >= 0 && _musicIndex < _playlist.Count)
                ? WebOrigin + "__music/" + _musicIndex
                : "";

            StringBuilder sb = new StringBuilder();
            sb.Append("{\"index\":").Append(_musicIndex)
              .Append(",\"playing\":").Append(_musicPlaying ? "true" : "false")
              .Append(",\"count\":").Append(_playlist.Count)
              .Append(",\"url\":").Append(Quote(url))
              .Append(",\"tracks\":[");
            for (int i = 0; i < _playlist.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append("{\"name\":").Append(Quote(Path.GetFileName(_playlist[i])))
                  .Append(",\"path\":").Append(Quote(_playlist[i])).Append('}');
            }
            sb.Append("]}");
            Call("musicState", sb.ToString());
        }

        // ============================================================ 卸载
        /// <summary>设置页「卸载」：确认后调用安装程序自带的 /uninstall（结束进程 → 删文件 → 清注册表 → 删 %APPDATA%）</summary>
        private void UninstallApp()
        {
            DialogResult r = MessageBox.Show(this,
                "确定卸载「彩色文本生成器」？\r\n\r\n将查找安装位置及相关位置，删除本产品的所有文件，并启动卸载程序。",
                "卸载确认", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (r != DialogResult.Yes) { CallStr("status", "已取消卸载"); return; }
            try
            {
                UnregisterShell();   // 卸载前清理右键菜单注册
                string dir = Path.GetDirectoryName(Application.ExecutablePath);
                string setup = Path.Combine(dir, "RichTextGen-Setup.exe");
                string un = Path.Combine(dir, "Uninstall.exe");
                string exe = File.Exists(setup) ? setup : (File.Exists(un) ? un : "");
                if (exe.Length > 0)
                {
                    try { System.Diagnostics.Process.Start(exe, "/uninstall"); } catch { }
                }
                _allowClose = true;   // 卸载器会 KillRunningApp 兜底，这里先自己优雅退出
                Close();
            }
            catch { CallStr("status", "卸载启动失败"); }
        }

        /// <summary>POST JSON（用于 DeepSeek 等 OpenAI 兼容接口）</summary>
        private static string HttpPost(string url, string bearer, string jsonBody, int ms)
        {
            string err;
            return HttpPostEx(url, bearer, jsonBody, ms, out err);
        }

        /// <summary>同 HttpPost，但把 HTTP 状态码与响应体带回 error，便于提示「哪家平台哪个参数不认」。</summary>
        private static string HttpPostEx(string url, string bearer, string jsonBody, int ms, out string error)
        {
            error = "";
            try
            {
                ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12 | SecurityProtocolType.Tls11 | SecurityProtocolType.Tls;
                HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
                req.Method = "POST"; req.Timeout = ms; req.ReadWriteTimeout = ms;
                req.UserAgent = "RichTextGen/" + Version;
                req.ContentType = "application/json";
                if (bearer.Length > 0) req.Headers.Add("Authorization", "Bearer " + bearer);
                byte[] data = Encoding.UTF8.GetBytes(jsonBody);
                req.ContentLength = data.Length;
                using (Stream s = req.GetRequestStream()) s.Write(data, 0, data.Length);
                using (WebResponse resp = req.GetResponse())
                using (Stream s = resp.GetResponseStream())
                using (StreamReader sr = new StreamReader(s, Encoding.UTF8))
                    return sr.ReadToEnd();
            }
            catch (WebException we)
            {
                try
                {
                    HttpWebResponse hr = we.Response as HttpWebResponse;
                    if (hr != null)
                    {
                        using (Stream s = hr.GetResponseStream())
                        using (StreamReader sr = new StreamReader(s, Encoding.UTF8))
                        {
                            string b = sr.ReadToEnd();
                            error = "HTTP " + (int)hr.StatusCode + " " + b;
                        }
                    }
                    else error = we.Message;
                }
                catch { error = we.Message; }
                if (error.Length > 400) error = error.Substring(0, 400) + "…";
                return "";
            }
            catch (Exception ex) { error = ex.Message; return ""; }
        }

        // ============================================================ 图片色块图
        private void BlockArtFromFile(string path)
        {
            path = path.Trim('"');
            try
            {
                using (Image img = Image.FromFile(path))
                {
                    BlockArtOptions o = new BlockArtOptions();
                    o.Columns = 40; o.Budget = 3000; o.AutoFit = true; o.AspectFix = true;
                    string info;
                    string art = BlockArt.Render(img, o, out info);
                    Call("blockArt", "{\"code\":" + Quote(art) + ",\"info\":" + Quote(info) + "}");
                    CallStr("status", "色块图已生成：" + info);
                }
            }
            catch (Exception ex) { CallStr("status", "图片处理失败：" + ex.Message); }
        }

        // ============================================================ 翻译（通用可配置）
        private void Translate(string json)
        {
            System.Threading.ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    string endpoint = Field(json, "endpoint");
                    string key = Field(json, "key");
                    string from = Field(json, "from");
                    string to = Field(json, "to");
                    string text = Field(json, "text");
                    if (text.Length == 0) { CallStr("status", "没有待翻译文本"); return; }
                    // 标签保护：抽出 <...> 占位，翻译后回填
                    List<string> tags = new List<string>();
                    string masked = Regex.Replace(text, "<[^>]*>", delegate (Match m) { tags.Add(m.Value); return "\u0001" + (tags.Count - 1) + "\u0001"; });
                    bool useGet = (endpoint.Length == 0) || endpoint.Contains("?");
                    string req;
                    if (endpoint.Contains("$Q"))                       // 预装 Google 免费网页版：$F/$T/$Q 占位符由宿主替换
                        req = endpoint.Replace("$F", Uri.EscapeDataString(from)).Replace("$T", Uri.EscapeDataString(to)).Replace("$Q", Uri.EscapeDataString(masked));
                    else if (endpoint.Length > 0)
                        req = endpoint;
                    else                                             // 预装 MyMemory 免费版
                        req = "https://api.mymemory.translated.net/get?q=" + Uri.EscapeDataString(masked) + "&langpair=" + from + "|" + to;
                    string body = "{\"text\":" + Quote(masked) + ",\"source\":" + Quote(from) + ",\"target\":" + Quote(to) + "}";
                    HttpWebRequest r = (HttpWebRequest)WebRequest.Create(req);
                    r.Method = useGet ? "GET" : "POST"; r.Timeout = 20000;
                    r.UserAgent = "RichTextGen/" + Version;
                    if (key.Length > 0) r.Headers.Add("Authorization", "Bearer " + key);
                    if (!useGet)
                    {
                        r.ContentType = "application/json";
                        byte[] data = Encoding.UTF8.GetBytes(body);
                        r.ContentLength = data.Length;
                        using (Stream s = r.GetRequestStream()) s.Write(data, 0, data.Length);
                    }
                    string resp;
                    using (WebResponse w = r.GetResponse())
                    using (Stream s = w.GetResponseStream())
                    using (StreamReader sr = new StreamReader(s, Encoding.UTF8))
                        resp = sr.ReadToEnd();
                    string translated;
                    if (endpoint.Contains("$Q") || resp.TrimStart().StartsWith("[["))   // Google 结构：[[["译文","原文",...],...]]
                        translated = Unescape(Regex.Match(resp, "\\[\\[\"((?:[^\"\\\\]|\\\\.)*)\"").Groups[1].Value);
                    else
                        translated = Regex.Match(resp, "\"(?:translatedText|translation|result|text)\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"").Groups[1].Value;
                    if (translated.Length == 0) translated = resp;
                    for (int k = 0; k < tags.Count; k++) translated = translated.Replace("\u0001" + k + "\u0001", tags[k]);
                    Call("translated", "{\"text\":" + Quote(translated) + "}");
                    CallStr("status", "翻译完成（标签已保护）");
                }
                catch (Exception ex) { CallStr("status", "翻译失败：" + ex.Message); }
            });
        }
    }

    /// <summary>关闭选择窗：这次是「最小化到后台」还是「退出应用」。</summary>
    internal class CloseChoiceForm : Form
    {
        private readonly CheckBox _remember;

        public bool Minimize { get; private set; }
        public bool Cancelled { get; private set; }
        public bool Remember { get { return _remember.Checked; } }

        public CloseChoiceForm(bool dark)
        {
            Cancelled = true;
            Text = "关闭窗口";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(430, 205);
            Font = new Font("Microsoft YaHei UI", 9f);
            BackColor = dark ? Color.FromArgb(43, 43, 43) : Color.White;
            ForeColor = dark ? Color.White : Color.FromArgb(27, 27, 27);

            Label title = new Label();
            title.Text = "要关闭应用，还是最小化到后台？";
            title.Font = new Font("Microsoft YaHei UI", 11f, FontStyle.Bold);
            title.AutoSize = true;
            title.Location = new Point(20, 18);

            Label tip = new Label();
            tip.Text = "最小化后程序仍在后台运行，可从托盘图标唤回；\r\n以后想直接退出，可在「设置 → 关闭行为」里改。";
            tip.AutoSize = false;
            tip.Size = new Size(390, 48);
            tip.Location = new Point(20, 56);

            _remember = new CheckBox();
            _remember.Text = "记住我的选择（以后不再询问）";
            _remember.AutoSize = true;
            _remember.Location = new Point(20, 118);

            Button min = new Button();
            min.Text = "最小化到后台";
            min.Size = new Size(126, 34);
            min.Location = new Point(20, 150);
            min.Click += delegate { Minimize = true; Cancelled = false; Close(); };

            Button quit = new Button();
            quit.Text = "退出应用";
            quit.Size = new Size(100, 34);
            quit.Location = new Point(156, 150);
            quit.Click += delegate { Minimize = false; Cancelled = false; Close(); };

            Button cancel = new Button();
            cancel.Text = "取消";
            cancel.Size = new Size(80, 34);
            cancel.Location = new Point(266, 150);
            cancel.Click += delegate { Cancelled = true; Close(); };

            Controls.Add(title);
            Controls.Add(tip);
            Controls.Add(_remember);
            Controls.Add(min);
            Controls.Add(quit);
            Controls.Add(cancel);
            AcceptButton = min;
            CancelButton = cancel;
        }
    }

    /// <summary>启动提示窗口：说明后台常驻、标签拆分与系统集成的收窄范围。</summary>
    internal class StartupTipForm : Form
    {
        private readonly CheckBox _noMore;

        public bool DontShowAgain { get { return _noMore.Checked; } }

        public StartupTipForm(bool dark)
        {
            Text = "使用提示";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(520, 330);
            Font = new Font("Microsoft YaHei UI", 9f);
            BackColor = dark ? Color.FromArgb(43, 43, 43) : Color.White;
            ForeColor = dark ? Color.White : Color.FromArgb(27, 27, 27);

            Label title = new Label();
            title.Text = "欢迎使用 彩色文本生成器";
            title.Font = new Font("Microsoft YaHei UI", 13f, FontStyle.Bold);
            title.AutoSize = true;
            title.Location = new Point(22, 18);

            Label body = new Label();
            body.Text =
                "· 程序已在后台常驻：再次双击程序不会新开进程，只会唤回本窗口并打开你选的文件。\r\n" +
                "  不同版本可以各开一个，互不影响。\r\n\r\n" +
                "· 拖动任意标签到窗口外，可以把它拆成一个独立窗口（和记事本一样）。\r\n\r\n" +
                "· 系统集成只登记 TXT / MD / LOG / JSON / INI / CSV 六种格式；\r\n" +
                "  不需要时可在「设置 → 系统集成」里一键移除。";
            body.AutoSize = false;
            body.Size = new Size(476, 190);
            body.Location = new Point(22, 60);

            _noMore = new CheckBox();
            _noMore.Text = "不再提示";
            _noMore.AutoSize = true;
            _noMore.Location = new Point(22, 268);

            Button ok = new Button();
            ok.Text = "知道了";
            ok.DialogResult = DialogResult.OK;
            ok.Size = new Size(110, 34);
            ok.Location = new Point(388, 262);

            Controls.Add(title);
            Controls.Add(body);
            Controls.Add(_noMore);
            Controls.Add(ok);
            AcceptButton = ok;
            CancelButton = ok;
        }
    }
}
