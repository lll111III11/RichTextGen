using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace RichTextGen
{
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            try { Application.Run(new MainForm()); }
            catch (Exception ex)
            {
                MessageBox.Show(ex.ToString(), "运行错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
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

        public MainForm()
        {
            Text = "彩色文本生成器 v" + Version;
            Font = new Font("Microsoft YaHei UI", 9f);
            // 用 exe 内嵌图标（csproj 的 ApplicationIcon），否则 WinForms 会显示系统默认图标
            try { Icon = System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            FormBorderStyle = FormBorderStyle.None;      // 无边框：标题栏由 HTML 画
            ClientSize = new Size(1180, 800);
            MinimumSize = new Size(880, 600);
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Color.FromArgb(243, 243, 243);
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);

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
            Controls.Add(web);
            web.CoreWebView2InitializationCompleted += OnWebReady;
            try { web.EnsureCoreWebView2Async(null); } catch { }

            // 更新检测：启动后 8 秒首次，之后每 60 秒一次（自动检测保持静默，仅发现新版本时才提示）
            updateTimer = new System.Windows.Forms.Timer();
            updateTimer.Interval = 60000;
            updateTimer.Tick += delegate { CheckUpdate(false); };
            updateTimer.Start();
            SetTimeout(8000, delegate { CheckUpdate(false); });
            SetTimeout(1500, ReportUpdateResult);

            // 全局热键（Ctrl+F2 / Ctrl+Alt+O / Ctrl+Alt+G / Ctrl+Alt+F4）：句柄创建后注册，见 OnHandleCreated
            hotkeys = new HotkeyManager(this, OnHotkey);
        }

        // ============================================================ 全局热键
        protected override void WndProc(ref Message m)
        {
            if (hotkeys != null && hotkeys.HandleMessage(ref m)) return;
            base.WndProc(ref m);
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
            }
        }

        /// <summary>隐藏窗口到后台。托盘图标（双击或右键「显示窗口」）可唤回。</summary>
        private void HideWindow()
        {
            if (!Visible) return;
            Hide();
            EnsureTray();
            // 首次隐藏时提示一次，避免用户以为程序被关掉了
            if (!trayTipShown && tray != null)
            {
                trayTipShown = true;
                try
                {
                    tray.BalloonTipTitle = "彩色文本生成器仍在后台运行";
                    tray.BalloonTipText = "双击托盘图标（或右键 →「显示窗口」）可唤回窗口。退出请用 ✕ 或托盘菜单「退出」。";
                    tray.ShowBalloonTip(4000);
                }
                catch { }
            }
        }

        private void RestoreWindow()
        {
            Show();
            WindowState = FormWindowState.Normal;
            Activate();
            BringToFront();
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
                menu.Items.Add("立即检测更新", null, delegate { CheckUpdate(true); });
                menu.Items.Add(new ToolStripSeparator());
                menu.Items.Add("退出", null, delegate { Close(); });
                tray.ContextMenuStrip = menu;
                tray.DoubleClick += delegate { RestoreWindow(); };
                tray.Visible = true;
            }
            catch { tray = null; }
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
            try { if (hotkeys != null) hotkeys.UnregisterAll(); } catch { }
            try { if (updateTimer != null) { updateTimer.Stop(); updateTimer.Dispose(); } } catch { }
            base.OnFormClosed(e);
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
                Close();
                return;
            }
            web.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            web.CoreWebView2.Settings.IsStatusBarEnabled = false;
            web.CoreWebView2.WebMessageReceived += OnMessage;
            web.CoreWebView2.NavigateToString(LoadHtml());
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
                    CheckUpdate(false);
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
                case "minimize": WindowState = FormWindowState.Minimized; break;
                case "maximize": WindowState = (WindowState == FormWindowState.Maximized) ? FormWindowState.Normal : FormWindowState.Maximized; break;
                case "close": Close(); break;
                case "drag": DragWindow(); break;
                case "status": CallStr("status", arg); break;
                case "openFile": OpenFile(); break;
                case "saveFile": SaveFile(arg); break;
                case "saveAsFile": SaveAsFile(Un(arg)); break;
                case "importTxt": ImportTxt(); break;
                case "exportTxt": ExportTxt(Un(arg)); break;
                case "dropFile": DropFile(Un(arg)); break;
                case "checkUpdate": CheckUpdate(true); break;
                case "doUpdate": DoUpdate(); break;
                case "openUrl": OpenUrl(arg); break;
                case "colorLib": FetchColorLibrary(); break;
                case "blockArt": BlockArtFromFile(Un(arg)); break;
                case "translate": Translate(arg); break;
                case "chat": Chat(Un(arg)); break;
                case "easterEgg": PlayNetSound(); break;
                case "uninstall": UninstallApp(); break;
                case "exportHtml": ExportHtml(Un(arg)); break;
                case "copy": try { Clipboard.SetText(Un(arg)); CallStr("status", "已复制到剪贴板"); } catch { } break;
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

        /// <summary>用浏览器打开链接；失败时给出提示而不是静默无反应</summary>
        private void OpenUrl(string url)
        {
            url = (url ?? "").Trim().Trim('"');
            if (url.Length == 0) { CallStr("status", "链接为空"); return; }
            try { System.Diagnostics.Process.Start(url); CallStr("status", "已在浏览器打开"); }
            catch (Exception ex) { CallStr("status", "打开链接失败：" + ex.Message); }
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
                d.Filter = "文本文件|*.txt;*.md;*.log;*.csv|所有文件|*.*";
                if (d.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    string txt = ReadText(d.FileName);
                    Call("fileOpened",
                        "{\"ok\":true,\"path\":" + Quote(d.FileName) +
                        ",\"name\":" + Quote(Path.GetFileName(d.FileName)) +
                        ",\"text\":" + Quote(txt) + "}");
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
                WriteText(path, text);
                Call("fileSaved", "{\"ok\":true,\"path\":" + Quote(path) +
                     ",\"name\":" + Quote(Path.GetFileName(path)) + "}");
            }
            catch (Exception ex)
            {
                Call("fileSaved", "{\"ok\":false,\"msg\":" + Quote("保存失败：" + ex.Message) + "}");
            }
        }
        private void SaveAsFile(string text) { SaveAs(text); }
        private void SaveAs(string text)
        {
            using (SaveFileDialog d = new SaveFileDialog())
            {
                d.Filter = "文本文件|*.txt|所有文件|*.*";
                d.FilterIndex = 1;
                d.FileName = "无标题.txt";
                if (d.ShowDialog(this) != DialogResult.OK) { Call("fileSaved", "{\"ok\":false,\"msg\":\"已取消\"}"); return; }
                try
                {
                    WriteText(d.FileName, text);
                    Call("fileSaved", "{\"ok\":true,\"path\":" + Quote(d.FileName) +
                         ",\"name\":" + Quote(Path.GetFileName(d.FileName)) + "}");
                }
                catch (Exception ex)
                {
                    Call("fileSaved", "{\"ok\":false,\"msg\":" + Quote("保存失败：" + ex.Message) + "}");
                }
            }
        }
        private static void WriteText(string path, string text)
        {
            File.WriteAllText(path, text, new UTF8Encoding(true));
        }
        private void ImportTxt()
        {
            using (OpenFileDialog d = new OpenFileDialog())
            {
                d.Filter = "文本文件|*.txt;*.md;*.log;*.csv|所有文件|*.*";
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
        private static string ReadText(string path)
        {
            byte[] b = File.ReadAllBytes(path);
            if (b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF) return Encoding.UTF8.GetString(b, 3, b.Length - 3);
            if (b.Length >= 2 && b[0] == 0xFF && b[1] == 0xFE) return Encoding.Unicode.GetString(b, 2, b.Length - 2);
            if (b.Length >= 2 && b[0] == 0xFE && b[1] == 0xFF) return Encoding.BigEndianUnicode.GetString(b, 2, b.Length - 2);
            try { return new UTF8Encoding(false, true).GetString(b); }
            catch { return Encoding.GetEncoding(936).GetString(b); }
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
        /// <param name="manual">true = 用户点击「立即检测更新」，无论结果如何都要在界面上给出可见反馈</param>
        private void CheckUpdate(bool manual)
        {
            if (manual) CallStr("upInfo", "正在检测更新…");
            System.Threading.ThreadPool.QueueUserWorkItem(delegate
            {
                string ver = "", url = "", notes = "", type = "";
                bool mandatory = false;
                try
                {
                    string txt = Http("https://raw.githubusercontent.com/lll111III11/RichTextGen/main/version.json", 8000);
                    if (string.IsNullOrEmpty(txt) || txt.IndexOf("\"version\"", StringComparison.Ordinal) < 0)
                        txt = Http("https://cdn.jsdelivr.net/gh/lll111III11/RichTextGen@main/version.json", 8000);
                    if (string.IsNullOrEmpty(txt)) { FailUpdate(manual, "更新检查失败（网络不可达）"); return; }
                    ver = Field(txt, "version");
                    url = Field(txt, "installer");
                    notes = Field(txt, "notes");
                    type = Field(txt, "type"); if (type.Length == 0) type = "feature";
                    mandatory = Regex.IsMatch(txt, "\"mandatory\"\\s*:\\s*true", RegexOptions.IgnoreCase);
                }
                catch { }
                if (ver.Length == 0) { FailUpdate(manual, "更新检查失败"); return; }
                if (Newer(ver, Version))
                {
                    // 记下清单内容：更新成功后要写进 pending_update.json，供下次启动弹「更新内容」
                    pendingVer = ver; pendingNotes = notes; pendingType = type; pendingUrl = url;
                    Call("update", "{" + Quote("version") + ":" + Quote(ver) + "," + Quote("url") + ":" + Quote(url) + "," + Quote("notes") + ":" + Quote(notes) + "," + Quote("type") + ":" + Quote(type) + "," + Quote("mandatory") + ":" + (mandatory ? "true" : "false") + "}");
                    CallStr("status", "发现新版本 " + ver + "（当前 " + Version + "）");
                    if (manual) CallStr("upInfo", "发现新版本 " + ver + "（当前 " + Version + "）");
                }
                else
                {
                    CallStr("status", "已是最新版本 " + Version);
                    if (manual) CallStr("upInfo", "已是最新版本 v" + Version + "（" + DateTime.Now.ToString("HH:mm:ss") + " 检测）");
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
            string url = pendingUrl.Length > 0 ? pendingUrl : "https://github.com/lll111III11/RichTextGen/releases/latest/download/RichTextGen-Setup.exe";
            System.Threading.ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    CallStr("status", "正在下载更新包…");
                    string path = Path.Combine(Path.GetTempPath(), "RichTextGen-Setup-" + DateTime.Now.ToString("HHmmss") + ".exe");
                    using (WebClient wc = new WebClient())
                    {
                        wc.Headers.Add("User-Agent", "RichTextGen/" + Version);
                        wc.DownloadFile(url, path);
                    }
                    try
                    {
                        string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RichTextGen");
                        Directory.CreateDirectory(dir);
                        File.WriteAllText(Path.Combine(dir, "pending_update.json"), "{" + Quote("version") + ":" + Quote(pendingVer) + "," + Quote("from") + ":" + Quote(Version) + "," + Quote("notes") + ":" + Quote(pendingNotes) + "," + Quote("type") + ":" + Quote(pendingType) + "}", Encoding.UTF8);
                    }
                    catch { }
                    CallStr("status", "下载完成，正在启动安装程序…");
                    try { System.Diagnostics.Process.Start(path); } catch { }
                    try { BeginInvoke((Action)delegate { Close(); }); } catch { }
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
                string dir = Path.GetDirectoryName(Application.ExecutablePath);
                string setup = Path.Combine(dir, "RichTextGen-Setup.exe");
                string un = Path.Combine(dir, "Uninstall.exe");
                string exe = File.Exists(setup) ? setup : (File.Exists(un) ? un : "");
                if (exe.Length > 0)
                {
                    try { System.Diagnostics.Process.Start(exe, "/uninstall"); } catch { }
                }
                Close();   // 卸载器会 KillRunningApp 兜底，这里先自己优雅退出
            }
            catch { CallStr("status", "卸载启动失败"); }
        }

        /// <summary>POST JSON（用于 DeepSeek 等 OpenAI 兼容接口）</summary>
        private static string HttpPost(string url, string bearer, string jsonBody, int ms)
        {
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
            catch { return ""; }
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
}
