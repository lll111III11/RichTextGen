using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

namespace RichTextGenSetup
{
    /// <summary>
    /// 彩色文本生成器 安装程序
    ///  · 内嵌程序资源（exe + exe.config + WebView2 组件），双击即装
    ///  · 单窗口向导：安装位置 + 快捷方式 + 进度
    ///  · 外观：渐变沿左上→右下流动 + 一条黑色斜杠扫过；Win11 22H2 以上可切换毛玻璃（亚克力）
    ///  · 复制自身为 Uninstall.exe，支持 /uninstall 卸载（删文件/快捷方式/注册表）
    ///  · 静默安装：RichTextGen-Setup.exe --silent "D:\路径"
    /// </summary>
    internal static class Setup
    {
        internal const string AppName = "彩色文本生成器";
        internal const string Product = "Rich text & multifunctional tool";
        /// <summary>版本号取自构建时写入的程序集版本（installer\build.bat 的 /version:），与应用 csproj 同源</summary>
        internal static readonly string Version = typeof(Setup).Assembly.GetName().Version.ToString();
        private const string Publisher = "3576220975@qq.com";
        private const string RegKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\RichTextGen";

        [STAThread]
        private static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            string dir = null; bool silent = false; bool uninstall = false;
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i].ToLowerInvariant();
                if (a == "--silent") { silent = true; if (i + 1 < args.Length) dir = args[i + 1]; }
                if (a == "/uninstall" || a == "--uninstall") uninstall = true;
            }

            if (uninstall)
            {
                DoUninstall();
                return;
            }
            if (silent)
            {
                try
                {
                    string target = string.IsNullOrEmpty(dir) ? DefaultDir() : dir;
                    Install(target, true, false, null);
                    Console.WriteLine("OK -> " + target);
                }
                catch (Exception ex) { Console.WriteLine("FAIL " + ex.Message); Environment.Exit(1); }
                return;
            }
            Application.Run(new SetupForm());
        }

        internal static string DefaultDir()
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs\\RichTextGen");
        }

        /// <summary>把内嵌资源释放到目标目录 + 建快捷方式 + 写卸载信息</summary>
        internal static void Install(string target, bool desktopShortcut, bool startMenu, Action<int, string> progress)
        {
            Directory.CreateDirectory(target);
            // 直接删除旧版本文件（保留卸载器自身）
            try
            {
                string selfPath = Assembly.GetExecutingAssembly().Location;
                foreach (string old in Directory.GetFiles(target))
                {
                    if (string.Equals(old, selfPath, StringComparison.OrdinalIgnoreCase)) continue;
                    if (Path.GetFileName(old).Equals("Uninstall.exe", StringComparison.OrdinalIgnoreCase)) continue;
                    try { File.Delete(old); } catch { }
                }
            }
            catch { }
            Assembly asm = Assembly.GetExecutingAssembly();
            string[] names = asm.GetManifestResourceNames();
            int n = 0;
            foreach (string res in names)
            {
                if (res.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase)) continue;
                string outName = res;
                // 资源名即目标文件名（含 "Microsoft.Web.WebView2.Core.dll" 这类多段名，必须原样保留）
                string outPath = Path.Combine(target, outName);
                using (Stream s = asm.GetManifestResourceStream(res))
                using (FileStream fs = File.Create(outPath))
                {
                    if (s != null) s.CopyTo(fs);
                }
                n++;
                if (progress != null) progress(Math.Min(90, 20 + n * 20), "正在释放 " + outName);
            }

            // 复制自身作为卸载程序
            try
            {
                string self = Assembly.GetExecutingAssembly().Location;
                string un = Path.Combine(target, "Uninstall.exe");
                if (!string.Equals(self, un, StringComparison.OrdinalIgnoreCase)) File.Copy(self, un, true);
            }
            catch { }

            string exePath = PickMainExe(target);
            if (desktopShortcut) MakeShortcut(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), AppName + ".lnk"), exePath, target);
            if (startMenu) MakeShortcut(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), AppName + ".lnk"), exePath, target);
            WriteUninstallInfo(target, exePath);

            if (progress != null) progress(100, "完成");
        }

        /// <summary>安装目录里挑出主程序（排除卸载器与安装器自身）</summary>
        internal static string PickMainExe(string target)
        {
            string exePath = Path.Combine(target, "RichTextGen.exe");
            try
            {
                foreach (string cand in Directory.GetFiles(target, "*.exe"))
                {
                    string fn = Path.GetFileName(cand);
                    if (fn.Equals("Uninstall.exe", StringComparison.OrdinalIgnoreCase)) continue;
                    if (fn.IndexOf("Setup", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                    return cand;
                }
            }
            catch { }
            return exePath;
        }

        private static void MakeShortcut(string lnkPath, string targetExe, string workDir)
        {
            try
            {
                Type t = Type.GetTypeFromProgID("WScript.Shell");
                object shell = Activator.CreateInstance(t);
                object lnk = t.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { lnkPath });
                Type lt = lnk.GetType();
                lt.InvokeMember("TargetPath", BindingFlags.SetProperty, null, lnk, new object[] { targetExe });
                lt.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, lnk, new object[] { workDir });
                lt.InvokeMember("Description", BindingFlags.SetProperty, null, lnk, new object[] { Product });
                lt.InvokeMember("Save", BindingFlags.InvokeMethod, null, lnk, null);
            }
            catch { }
        }

        private static void WriteUninstallInfo(string target, string exePath)
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.CreateSubKey(RegKey))
                {
                    k.SetValue("DisplayName", AppName + " · " + Product);
                    k.SetValue("DisplayVersion", Version);
                    k.SetValue("Publisher", Publisher);
                    k.SetValue("InstallLocation", target);
                    k.SetValue("DisplayIcon", exePath);
                    k.SetValue("UninstallString", "\"" + Path.Combine(target, "Uninstall.exe") + "\" /uninstall");
                    k.SetValue("NoModify", 1, RegistryValueKind.DWord);
                    k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                }
            }
            catch { }
        }

        internal static void DoUninstall()
        {
            string target = null;
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RegKey))
                    if (k != null) target = Convert.ToString(k.GetValue("InstallLocation"));
            }
            catch { }
            if (string.IsNullOrEmpty(target) || !Directory.Exists(target))
                target = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);

            try { File.Delete(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), AppName + ".lnk")); } catch { }
            try { File.Delete(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), AppName + ".lnk")); } catch { }
            try { Registry.CurrentUser.DeleteSubKeyTree(RegKey, false); } catch { }

            // 删除目录内容（卸载器自身最后删）
            try
            {
                string self = Assembly.GetExecutingAssembly().Location;
                foreach (string f in Directory.GetFiles(target))
                    if (!string.Equals(f, self, StringComparison.OrdinalIgnoreCase)) { try { File.Delete(f); } catch { } }
                foreach (string d in Directory.GetDirectories(target)) { try { Directory.Delete(d, true); } catch { } }
                try { File.Delete(self); } catch { }
                try { Directory.Delete(target, true); } catch { }
                if (Directory.Exists(target))   // 自己锁着自己 → 交给 cmd 稍后删
                {
                    try
                    {
                        string q = ((char)34).ToString();
                        System.Diagnostics.ProcessStartInfo si = new System.Diagnostics.ProcessStartInfo("cmd.exe", "/c ping -n 3 127.0.0.1 >nul & rmdir /s /q " + q + target + q);
                        si.CreateNoWindow = true;
                        si.UseShellExecute = false;
                        System.Diagnostics.Process.Start(si);
                    }
                    catch { }
                }
            }
            catch { }
        }
    }

    // ==================================================================== 外观基础
    internal static class Look
    {
        private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
        private const int DWMWA_SYSTEMBACKDROP_TYPE = 38;

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int val, int size);
        [DllImport("dwmapi.dll")]
        private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref Margins m);
        [StructLayout(LayoutKind.Sequential)]
        private struct Margins { public int Left, Right, Top, Bottom; }

        internal static int BuildNumber()
        {
            try
            {
                using (RegistryKey k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion"))
                {
                    if (k != null)
                    {
                        object o = k.GetValue("CurrentBuildNumber");
                        int n;
                        if (o != null && int.TryParse(o.ToString(), out n)) return n;
                    }
                }
            }
            catch { }
            return 0;
        }

        /// <summary>Win11 22H2（22621）起才支持系统背景材质</summary>
        internal static bool BackdropSupported { get { return BuildNumber() >= 22621; } }

        internal static void RoundCorners(IntPtr hwnd)
        {
            try { int v = 2; DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref v, 4); } catch { }
        }

        /// <summary>开启系统亚克力背景（毛玻璃）。失败返回 false，调用方需回退。</summary>
        internal static bool Acrylic(IntPtr hwnd, bool on)
        {
            try
            {
                int v = on ? 3 : 0;      // DWMSBT_TRANSIENTWINDOW=亚克力 / DWMSBT_AUTO
                if (DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref v, 4) != 0) return false;
                Margins m = new Margins();
                m.Left = m.Right = m.Top = m.Bottom = on ? -1 : 0;
                DwmExtendFrameIntoClientArea(hwnd, ref m);
                return on;
            }
            catch { return false; }
        }

        internal static GraphicsPath RoundPath(Rectangle r, int radius)
        {
            GraphicsPath p = new GraphicsPath();
            int d = Math.Max(1, radius * 2);
            if (d > Math.Min(r.Width, r.Height)) d = Math.Min(r.Width, r.Height);
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        internal static void Round(Control c, int radius)
        {
            try
            {
                if (c.Width <= 0 || c.Height <= 0) return;
                using (GraphicsPath p = RoundPath(new Rectangle(0, 0, c.Width, c.Height), radius))
                {
                    Region old = c.Region;
                    c.Region = new Region(p);
                    if (old != null) old.Dispose();
                }
            }
            catch { }
        }

        internal static Color Mix(Color a, Color b, double t)
        {
            if (t < 0) t = 0; if (t > 1) t = 1;
            return Color.FromArgb(
                (int)Math.Round(a.R + (b.R - a.R) * t),
                (int)Math.Round(a.G + (b.G - a.G) * t),
                (int)Math.Round(a.B + (b.B - a.B) * t));
        }
    }

    /// <summary>半透明圆角卡片（WinForms 默认不支持带 Alpha 的背景色，必须开 SupportsTransparentBackColor）</summary>
    internal class GlassPanel : Panel
    {
        public GlassPanel()
        {
            SetStyle(ControlStyles.SupportsTransparentBackColor | ControlStyles.OptimizedDoubleBuffer
                   | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
        }
        protected override void OnResize(EventArgs e) { base.OnResize(e); Look.Round(this, 12); }
    }

    // ==================================================================== 安装向导
    internal class SetupForm : Form
    {
        // 渐变调色盘：会随时间沿对角线流动
        private static readonly Color[] Palette =
        {
            Color.FromArgb(0x0B, 0x2A, 0x5B), Color.FromArgb(0x00, 0x67, 0xC0),
            Color.FromArgb(0x2F, 0xA8, 0xFF), Color.FromArgb(0x0A, 0x2B, 0x55),
            Color.FromArgb(0x6C, 0xD4, 0xFF), Color.FromArgb(0x00, 0x3A, 0x8C)
        };

        private readonly GlassPanel card;
        private readonly TextBox txtDir;
        private readonly CheckBox chkDesktop;
        private readonly CheckBox chkStart;
        private readonly CheckBox chkRun;
        private readonly CheckBox chkGlass;
        private readonly Button btnInstall;
        private readonly Button btnCancel;
        private readonly ProgressBar bar;
        private readonly Label lblStatus;
        private readonly System.Windows.Forms.Timer anim;

        private double phase;      // 渐变色相位 0-1
        private double sweep;      // 黑色斜杠扫描 0-1
        private bool glass;        // 是否毛玻璃生效
        private bool installing;

        public SetupForm()
        {
            Text = Setup.AppName + " 安装向导  v" + Setup.Version;
            Font = new Font("Microsoft YaHei UI", 9f);
            // 布局按 96 DPI 设计；用 Dpi 缩放让坐标/尺寸随显示器 DPI 等比放大，
            // 否则（默认的 Font 模式在纯代码建窗体时会缩成 0.8 倍，导致右侧/下方被裁掉）
            AutoScaleDimensions = new SizeF(96f, 96f);
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(720, 500);
            FormBorderStyle = FormBorderStyle.None;
            MaximizeBox = false; MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            KeyPreview = true;
            DoubleBuffered = true;
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
            BackColor = Palette[0];

            // ---------------- 标题栏 ----------------
            Label title = MkText(Setup.AppName + " 安装向导", 46, 13, 12.5f, FontStyle.Bold, Color.White);
            Controls.Add(title);
            Label subTitle = MkText("v" + Setup.Version + " · HTML 界面版（WebView2）", 46, 33, 8.5f, FontStyle.Regular, Color.FromArgb(206, 226, 250));
            Controls.Add(subTitle);

            Label btnMin = MkTitleButton("—", 630);
            btnMin.Click += delegate { WindowState = FormWindowState.Minimized; };
            Controls.Add(btnMin);
            Label btnClose = MkTitleButton("✕", 672);
            btnClose.Click += delegate { Close(); };
            Controls.Add(btnClose);

            DragMove(title); DragMove(subTitle); DragMove(this);

            // ---------------- 卡片 ----------------
            card = new GlassPanel();
            card.Location = new Point(24, 62);
            card.Size = new Size(672, 414);
            card.BackColor = Color.FromArgb(236, 252, 252, 252);
            Controls.Add(card);

            Label licTitle = MkInk("许可协议", 20, 14, 10.5f, FontStyle.Bold, Color.FromArgb(27, 27, 27), card);
            licTitle.AutoSize = true;

            TextBox lic = new TextBox();
            lic.Multiline = true; lic.ReadOnly = true; lic.ScrollBars = ScrollBars.Vertical;
            lic.TabStop = false;                  // 只读多行框获得焦点会自动全选高亮，跳过它
            lic.Location = new Point(20, 40); lic.Size = new Size(632, 92);
            lic.BorderStyle = BorderStyle.FixedSingle;
            lic.BackColor = Color.White; lic.ForeColor = Color.FromArgb(60, 60, 60);
            lic.Text = "1. 本程序按“原样”提供，用于生成 Unity/TextMeshPro 富文本并发送到游戏。\r\n" +
                       "2. 请勿用于违反游戏服务条款的用途；因使用本程序产生的后果由使用者自负。\r\n" +
                       "3. 程序会联网检查更新（GitHub），并在你点击“更新”时下载新版本。\r\n" +
                       "4. 本程序不收集你的任何个人信息。\r\n\r\n继续安装即表示你同意以上条款。";
            card.Controls.Add(lic);

            MkInk("安装位置", 20, 148, 9f, FontStyle.Regular, Color.FromArgb(93, 93, 93), card);
            txtDir = new TextBox();
            txtDir.Location = new Point(92, 145); txtDir.Size = new Size(466, 25);
            txtDir.BorderStyle = BorderStyle.FixedSingle;
            txtDir.Text = Setup.DefaultDir();
            card.Controls.Add(txtDir);

            Button browse = new Button();
            browse.Text = "浏览…"; browse.Size = new Size(82, 26);
            browse.Location = new Point(570, 144);
            browse.FlatStyle = FlatStyle.Flat;
            browse.FlatAppearance.BorderColor = Color.FromArgb(214, 214, 214);
            browse.BackColor = Color.FromArgb(251, 251, 251);
            browse.Click += delegate
            {
                using (FolderBrowserDialog d = new FolderBrowserDialog())
                {
                    d.Description = "选择安装位置";
                    if (d.ShowDialog(this) == DialogResult.OK) txtDir.Text = d.SelectedPath;
                }
            };
            card.Controls.Add(browse);

            chkDesktop = MkCheck("创建桌面快捷方式", 20, 184, true, card);
            chkStart = MkCheck("在开始菜单创建快捷方式", 230, 184, true, card);
            chkRun = MkCheck("安装完成后启动程序", 470, 184, true, card);

            chkGlass = MkCheck("毛玻璃背景（Win11 22H2，关闭则显示渐变流动）", 20, 212, false, card);
            chkGlass.Enabled = Look.BackdropSupported;
            chkGlass.CheckedChanged += delegate { ApplyGlass(chkGlass.Checked); };

            bar = new ProgressBar();
            bar.Location = new Point(20, 252); bar.Size = new Size(632, 14);
            bar.Style = ProgressBarStyle.Continuous;
            card.Controls.Add(bar);

            lblStatus = MkInk("准备就绪，点击“立即安装”开始。", 20, 274, 9f, FontStyle.Regular, Color.FromArgb(93, 93, 93), card);

            btnInstall = new Button();
            btnInstall.Text = "立即安装"; btnInstall.Size = new Size(126, 36);
            btnInstall.Location = new Point(428, 352);
            btnInstall.FlatStyle = FlatStyle.Flat;
            btnInstall.FlatAppearance.BorderSize = 0;
            btnInstall.BackColor = Color.FromArgb(0, 103, 192);
            btnInstall.ForeColor = Color.White;
            btnInstall.Font = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Bold);
            btnInstall.Click += delegate { DoInstall(); };
            card.Controls.Add(btnInstall);

            btnCancel = new Button();
            btnCancel.Text = "取消"; btnCancel.Size = new Size(96, 36);
            btnCancel.Location = new Point(562, 352);
            btnCancel.FlatStyle = FlatStyle.Flat;
            btnCancel.FlatAppearance.BorderColor = Color.FromArgb(214, 214, 214);
            btnCancel.BackColor = Color.FromArgb(251, 251, 251);
            btnCancel.Click += delegate { Close(); };
            card.Controls.Add(btnCancel);

            if (!Look.BackdropSupported)
                lblStatus.Text = "当前系统不支持系统级毛玻璃（需 Win11 22H2 及以上），已使用渐变流动背景。";

            // ---------------- 动画 ----------------
            anim = new System.Windows.Forms.Timer();
            anim.Interval = 33;             // ≈30 FPS
            anim.Tick += delegate
            {
                phase += 0.0045; if (phase >= 1) phase -= 1;
                sweep += 0.0075; if (sweep >= 1) sweep -= 1;
                Invalidate(true);
            };
            anim.Start();

            Shown += delegate
            {
                Look.RoundCorners(Handle);
                Look.Round(btnInstall, 6);
                Look.Round(btnCancel, 6);
                Look.Round(card, 12);
                // 焦点交给路径输入框，避免启动时出现蓝色全选块
                txtDir.Focus();
                txtDir.SelectionStart = txtDir.TextLength;
            };
            Resize += delegate { Look.Round(card, 12); Look.Round(btnInstall, 6); Look.Round(btnCancel, 6); Invalidate(); };
        }

        // ============================================================ 背景绘制
        protected override void OnPaintBackground(PaintEventArgs e)
        {
            int w = ClientSize.Width, h = ClientSize.Height;
            if (w <= 0 || h <= 0) return;
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            int alpha = glass ? 128 : 255;
            PaintGradient(g, w, h, alpha);
            if (!glass) PaintSweep(g, w, h);
            PaintLogo(g);
        }

        /// <summary>
        /// 45°（左上→右下）渐变：几何固定、色带颜色按相位循环，视觉上就是渐变沿对角线朝右下流动。
        /// （不移动渐变矩形，否则会让色带缩放/在循环处跳变）
        /// </summary>
        private void PaintGradient(Graphics g, int w, int h, int alpha)
        {
            int n = Palette.Length;
            double sp = phase * n;
            int k = (int)Math.Floor(sp);
            double f = sp - k;
            Color[] stops = new Color[n + 1];
            float[] pos = new float[n + 1];
            for (int i = 0; i <= n; i++)
            {
                Color c = Look.Mix(Palette[(i + k) % n], Palette[(i + k + 1) % n], f);
                stops[i] = (alpha < 255) ? Color.FromArgb(alpha, c) : c;
                pos[i] = (float)i / n;
            }
            RectangleF r = new RectangleF(-2f, -2f, w + 4f, h + 4f);
            // 首尾颜色按调色盘循环后是同色，构造函数不能传两个相同颜色，故换个占位色；实际由 InterpolationColors 决定
            using (LinearGradientBrush br = new LinearGradientBrush(r, stops[0], stops[1], 45f))
            {
                ColorBlend cb = new ColorBlend(n + 1);
                cb.Positions = pos;
                cb.Colors = stops;
                br.InterpolationColors = cb;
                g.FillRectangle(br, ClientRectangle);
            }
        }

        /// <summary>一条黑色斜杠（与渐变方向垂直）沿对角线从左上角外扫到右下角外，两端羽化</summary>
        private void PaintSweep(Graphics g, int w, int h)
        {
            int diag = (int)Math.Sqrt((double)w * w + (double)h * h);
            GraphicsState st = g.Save();
            try
            {
                g.TranslateTransform(w / 2f, h / 2f);
                g.RotateTransform(45f);          // 局部 x 轴 = 左上→右下方向
                // 行程按对角线长度 1.0 倍：让斜杠大部分时间都留在窗口内，视觉上像一条扫过的黑杠
                int cx = (int)(-diag * 0.5 + sweep * diag * 1.0);
                Rectangle band = new Rectangle(cx - 62, -diag, 124, diag * 2);
                // 注意：LinearGradientBrush 的两个颜色不能相同（都传 Color.Transparent 会抛异常），
                // 结束后再用 InterpolationColors 做两端羽化
                using (LinearGradientBrush bg = new LinearGradientBrush(band, Color.FromArgb(0, 0, 0, 0), Color.FromArgb(150, 0, 0, 0), 0f))
                {
                    ColorBlend cb = new ColorBlend(4);
                    cb.Positions = new float[] { 0f, 0.3f, 0.7f, 1f };
                    cb.Colors = new Color[] { Color.FromArgb(0, 0, 0, 0), Color.FromArgb(150, 0, 0, 0), Color.FromArgb(150, 0, 0, 0), Color.FromArgb(0, 0, 0, 0) };
                    bg.InterpolationColors = cb;
                    g.FillRectangle(bg, band);
                }
            }
            catch { }
            finally { g.Restore(st); }
        }

        /// <summary>左上角 Logo：圆角渐变方块</summary>
        private void PaintLogo(Graphics g)
        {
            try
            {
                Rectangle r = new Rectangle(18, 15, 20, 20);
                using (GraphicsPath p = Look.RoundPath(r, 5))
                using (LinearGradientBrush b = new LinearGradientBrush(r, Color.FromArgb(0x6C, 0xD4, 0xFF), Color.FromArgb(0x00, 0x67, 0xC0), 45f))
                {
                    g.FillPath(b, p);
                }
            }
            catch { }
        }

        // ============================================================ 交互
        private void ApplyGlass(bool on)
        {
            if (on && !Look.BackdropSupported)
            {
                chkGlass.Checked = false;
                lblStatus.Text = "当前系统不支持毛玻璃背景（需 Win11 22H2 及以上），已使用渐变流动背景。";
                return;
            }
            bool ok = Look.Acrylic(Handle, on);      // 关闭时也要调用，才能把亚克力/扩展边框撤掉
            glass = on && ok;
            if (on && !glass)
            {
                chkGlass.Checked = false;
                lblStatus.Text = "毛玻璃启用失败，已回退到渐变流动背景。";
            }
            else
            {
                lblStatus.Text = glass ? "毛玻璃背景已开启（亚克力）。" : "渐变流动背景（黑色斜杠扫描）。";
            }
            anim.Enabled = !glass;              // 毛玻璃时停掉动画，静态更省电
            Invalidate(true);
        }

        private void DragMove(Control c)
        {
            c.MouseDown += delegate (object s, MouseEventArgs e)
            {
                if (e.Button != MouseButtons.Left) return;
                try
                {
                    ReleaseCapture();
                    SendMessage(Handle, 0xA1, (IntPtr)2, IntPtr.Zero);
                }
                catch { }
            };
        }

        [DllImport("user32.dll")]
        private static extern bool ReleaseCapture();
        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wp, IntPtr lp);

        private void DoInstall()
        {
            if (installing) return;
            string dir = txtDir.Text.Trim();
            if (dir.Length == 0) { MessageBox.Show(this, "请选择安装位置。", "提示"); return; }
            try
            {
                installing = true;
                btnInstall.Enabled = false;
                btnCancel.Enabled = false;
                anim.Enabled = false;                 // 安装期间停动画，避免抢 CPU
                Setup.Install(dir, chkDesktop.Checked, chkStart.Checked,
                    delegate (int pct, string msg) { bar.Value = Math.Min(100, pct); lblStatus.Text = msg; Application.DoEvents(); });
                lblStatus.Text = "安装完成： " + dir;
                string exe = Setup.PickMainExe(dir);
                if (chkRun.Checked)
                {
                    try { System.Diagnostics.Process.Start(exe); } catch { }
                }
                MessageBox.Show(this,
                    "安装完成！\r\n\r\n位置：" + dir +
                    (chkDesktop.Checked ? "\r\n桌面快捷方式已创建" : "") +
                    "\r\n\r\n卸载：开始菜单搜索“彩色文本生成器”或运行安装目录下的 Uninstall.exe",
                    "完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
                Close();
            }
            catch (Exception ex)
            {
                installing = false;
                btnInstall.Enabled = true;
                btnCancel.Enabled = true;
                anim.Enabled = !glass;
                MessageBox.Show(this, "安装失败：" + ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        // ============================================================ 小工厂
        private static Label MkText(string text, int x, int y, float size, FontStyle style, Color fore)
        {
            Label l = new Label();
            l.Text = text;
            l.Location = new Point(x, y);
            l.AutoSize = true;
            l.BackColor = Color.Transparent;
            l.ForeColor = fore;
            l.Font = new Font("Microsoft YaHei UI", size, style);
            return l;
        }

        private static Label MkInk(string text, int x, int y, float size, FontStyle style, Color fore, Control parent)
        {
            Label l = MkText(text, x, y, size, style, fore);
            l.MaximumSize = new Size(parent.ClientSize.Width - x - 20, 0);
            parent.Controls.Add(l);
            return l;
        }

        private static Label MkTitleButton(string glyph, int x)
        {
            Label l = new Label();
            l.Text = glyph;
            l.Location = new Point(x, 0);
            l.Size = new Size(42, 40);
            l.TextAlign = ContentAlignment.MiddleCenter;
            l.BackColor = Color.Transparent;
            l.ForeColor = Color.FromArgb(232, 240, 252);
            l.Font = new Font("Segoe UI", 10f);
            l.Cursor = Cursors.Hand;
            l.MouseEnter += delegate { l.BackColor = Color.FromArgb(48, 255, 255, 255); };
            l.MouseLeave += delegate { l.BackColor = Color.Transparent; };
            return l;
        }

        private static CheckBox MkCheck(string text, int x, int y, bool @checked, Control parent)
        {
            CheckBox c = new CheckBox();
            c.Text = text;
            c.Location = new Point(x, y);
            c.AutoSize = true;
            c.Checked = @checked;
            c.BackColor = Color.Transparent;
            c.ForeColor = Color.FromArgb(27, 27, 27);
            parent.Controls.Add(c);
            return c;
        }
    }
}
