using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Windows.Forms;
using Microsoft.Win32;

namespace RichTextGenSetup
{
    /// <summary>
    /// 彩色文本生成器 安装程序
    ///  · 内嵌程序资源（exe + exe.config），双击即装
    ///  · 向导：欢迎/许可 → 选择安装路径 + 桌面快捷方式 → 安装进度 → 完成
    ///  · 复制自身为 Uninstall.exe，支持 /uninstall 卸载（删文件/快捷方式/注册表）
    ///  · 静默安装：RichTextGen-Setup.exe --silent "D:\路径"
    /// </summary>
    internal static class Setup
    {
        internal const string AppName = "彩色文本生成器";
        internal const string Product = "Rich text & multifunctional tool";
        private const string Version = "5.2.0.0";
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
            Assembly asm = Assembly.GetExecutingAssembly();
            string[] names = asm.GetManifestResourceNames();
            int n = 0;
            foreach (string res in names)
            {
                if (res.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase)) continue;
                string outName = res;
                // 资源名形如 "RichTextGen.exe" / "RichTextGen.exe.config"
                if (outName.IndexOf('.') > 0 && !outName.EndsWith(".config", StringComparison.OrdinalIgnoreCase))
                {
                    // 去掉可能的命名空间前缀，保留最后两段
                    string[] parts = outName.Split('.');
                    if (parts.Length > 2) outName = parts[parts.Length - 2] + "." + parts[parts.Length - 1];
                }
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

            string exePath = Path.Combine(target, "RichTextGen.exe");
            if (desktopShortcut) MakeShortcut(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), AppName + ".lnk"), exePath, target);
            if (startMenu) MakeShortcut(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), AppName + ".lnk"), exePath, target);
            WriteUninstallInfo(target, exePath);

            if (progress != null) progress(100, "完成");
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

    internal class SetupForm : Form
    {
        private readonly TextBox txtDir;
        private readonly CheckBox chkDesktop;
        private readonly CheckBox chkStart;
        private readonly CheckBox chkRun;
        private readonly Button btnInstall;
        private readonly ProgressBar bar;
        private readonly Label lblStatus;

        public SetupForm()
        {
            Text = Setup.AppName + " 安装向导  v" + "5.2.0.0";
            Font = new Font("Microsoft YaHei UI", 9f);
            ClientSize = new Size(560, 420);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false; MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Color.FromArgb(243, 243, 243);

            Label title = new Label();
            title.Text = Setup.AppName + "   ·   " + "Rich text & multifunctional tool";
            title.Font = new Font("Microsoft YaHei UI", 14f, FontStyle.Bold);
            title.Location = new Point(20, 18);
            title.AutoSize = true;
            Controls.Add(title);

            Label sub = new Label();
            sub.Text = "版本 5.2.0.0   ·   安装向导将把程序安装到你的电脑";
            sub.Location = new Point(22, 52);
            sub.AutoSize = true;
            sub.ForeColor = Color.FromArgb(93, 93, 93);
            Controls.Add(sub);

            TextBox lic = new TextBox();
            lic.Multiline = true; lic.ReadOnly = true; lic.ScrollBars = ScrollBars.Vertical;
            lic.Location = new Point(22, 80); lic.Size = new Size(516, 120);
            lic.BackColor = Color.White;
            lic.Text = "许可协议\r\n\r\n" +
                "1. 本程序按“原样”提供，用于生成 Unity/TextMeshPro 富文本并发送到游戏。\r\n" +
                "2. 请勿用于违反游戏服务条款的用途；因使用本程序产生的后果由使用者自负。\r\n" +
                "3. 程序会联网检查更新（GitHub），并在你点击“更新”时下载新版本。\r\n" +
                "4. 本程序不收集你的任何个人信息。\r\n\r\n" +
                "继续安装即表示你同意以上条款。";
            Controls.Add(lic);

            Label ldir = new Label();
            ldir.Text = "安装位置：";
            ldir.Location = new Point(22, 214); ldir.AutoSize = true;
            Controls.Add(ldir);

            txtDir = new TextBox();
            txtDir.Location = new Point(96, 211); txtDir.Size = new Size(352, 25);
            txtDir.Text = Setup.DefaultDir();
            Controls.Add(txtDir);

            Button browse = new Button();
            browse.Text = "浏览…"; browse.Size = new Size(80, 26);
            browse.Location = new Point(456, 210);
            browse.Click += delegate
            {
                using (FolderBrowserDialog d = new FolderBrowserDialog())
                {
                    d.Description = "选择安装位置";
                    if (d.ShowDialog(this) == DialogResult.OK) txtDir.Text = d.SelectedPath;
                }
            };
            Controls.Add(browse);

            chkDesktop = new CheckBox();
            chkDesktop.Text = "创建桌面快捷方式"; chkDesktop.Location = new Point(96, 246);
            chkDesktop.AutoSize = true; chkDesktop.Checked = true;
            Controls.Add(chkDesktop);

            chkStart = new CheckBox();
            chkStart.Text = "在开始菜单创建快捷方式"; chkStart.Location = new Point(300, 246);
            chkStart.AutoSize = true; chkStart.Checked = true;
            Controls.Add(chkStart);

            chkRun = new CheckBox();
            chkRun.Text = "安装完成后启动程序"; chkRun.Location = new Point(96, 274);
            chkRun.AutoSize = true; chkRun.Checked = true;
            Controls.Add(chkRun);

            bar = new ProgressBar();
            bar.Location = new Point(96, 306); bar.Size = new Size(440, 16);
            bar.Style = ProgressBarStyle.Continuous;
            Controls.Add(bar);

            lblStatus = new Label();
            lblStatus.Text = "准备就绪，点击“立即安装”开始。";
            lblStatus.Location = new Point(96, 328); lblStatus.AutoSize = true;
            lblStatus.ForeColor = Color.FromArgb(93, 93, 93);
            Controls.Add(lblStatus);

            btnInstall = new Button();
            btnInstall.Text = "立即安装"; btnInstall.Size = new Size(120, 34);
            btnInstall.Location = new Point(300, 362);
            btnInstall.BackColor = Color.FromArgb(0, 103, 192);
            btnInstall.ForeColor = Color.White;
            btnInstall.FlatStyle = FlatStyle.Flat;
            btnInstall.Click += delegate { DoInstall(); };
            Controls.Add(btnInstall);

            Button cancel = new Button();
            cancel.Text = "取消"; cancel.Size = new Size(90, 34);
            cancel.Location = new Point(430, 362);
            cancel.Click += delegate { Close(); };
            Controls.Add(cancel);
        }

        private void DoInstall()
        {
            string dir = txtDir.Text.Trim();
            if (dir.Length == 0) { MessageBox.Show(this, "请选择安装位置。", "提示"); return; }
            try
            {
                btnInstall.Enabled = false;
                Setup.Install(dir, chkDesktop.Checked, chkStart.Checked,
                    delegate (int pct, string msg) { bar.Value = Math.Min(100, pct); lblStatus.Text = msg; Application.DoEvents(); });
                lblStatus.Text = "安装完成： " + dir;
                if (chkRun.Checked)
                {
                    try { System.Diagnostics.Process.Start(Path.Combine(dir, "RichTextGen.exe")); } catch { }
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
                btnInstall.Enabled = true;
                MessageBox.Show(this, "安装失败：" + ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }
}
