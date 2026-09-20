using System;
using System.Windows.Forms;

namespace RichTextGen
{
    internal static class Program
    {
        /// <summary>无界面自检：图片 → 色块图富文本（含预算自动降精度）</summary>
        private static void BlockArtSelfTest(string img, string outFile, int cols, int budget)
        {
            System.Text.StringBuilder log = new System.Text.StringBuilder();
            using (System.Drawing.Image im = System.Drawing.Image.FromFile(img))
            {
                foreach (int c0 in new int[] { cols, 40, 64 })
                {
                    BlockArtOptions o = new BlockArtOptions();
                    o.Columns = c0;
                    o.Budget = budget;
                    o.AutoFit = true;
                    string info;
                    string code = BlockArt.Render(im, o, out info);
                    log.AppendLine("请求 " + c0 + " 列 → " + info);
                    if (c0 == cols)
                    {
                        log.AppendLine("---- 输出前 240 字符 ----");
                        log.AppendLine(code.Length > 240 ? code.Substring(0, 240) : code);
                    }
                }
            }
            System.IO.File.WriteAllText(outFile, log.ToString(), new System.Text.UTF8Encoding(true));
        }

        [STAThread]
        private static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            bool picker = false;
            bool selfTest = false;
            string selfTestOut = "";
            bool dark = false;
            bool send = false;
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i].ToLowerInvariant();
                if (a == "--picker") picker = true;
                if (a == "--dark") dark = true;
                if (a == "--send") send = true;
                if (a == "--demo") MainForm.StartupDemo = true;
                if (a == "--free") MainForm.StartupFree = true;
                if (a == "--bgtest" && i + 1 < args.Length) MainForm.StartupBgTest = args[i + 1];
                if (a == "--selftest") { selfTest = true; if (i + 1 < args.Length) selfTestOut = args[i + 1]; }
            }

            try
            {
                if (picker)
                {
                    // 自检用：直接打开拾色器（不带主窗口）
                    if (dark) Theme.Set(ThemeMode.Dark);
                    Application.Run(new ColorPickerDialog(new Rgb(0x21, 0x8A, 0xFF), ColorOutputMode.Hex));
                    return;
                }
                if (args.Length > 0)
                {
                    string a0 = args[0].ToLowerInvariant();
                    if (a0 == "--uicolors") { if (dark) Theme.Set(ThemeMode.Dark); Application.Run(new UiColorsDialog(null)); return; }
                    if (a0 == "--bg") { if (dark) Theme.Set(ThemeMode.Dark); Application.Run(new BackgroundDialog(null)); return; }
                    if (a0 == "--help") { if (dark) Theme.Set(ThemeMode.Dark); Application.Run(new HelpWindow()); return; }
                    if (a0 == "--img") { if (dark) Theme.Set(ThemeMode.Dark); Application.Run(new ImageSystemDialog(null)); return; }
                    if (a0 == "--blockart" && args.Length >= 3)
                    {
                        BlockArtSelfTest(args[1], args[2], args.Length > 3 ? int.Parse(args[3]) : 24, args.Length > 4 ? int.Parse(args[4]) : 500);
                        return;
                    }
                }
                MainForm.StartupDark = dark;
                MainForm.StartupSend = send;
                MainForm form = new MainForm();
                if (selfTest)
                {
                    Timer t = new Timer();
                    t.Interval = 1500;
                    t.Tick += delegate
                    {
                        t.Stop();
                        try { form.RunSelfTest(selfTestOut); }
                        catch (Exception ex) { System.IO.File.AppendAllText(selfTestOut, "TEST ERROR: " + ex + ""); }
                        Application.Exit();
                    };
                    t.Start();
                }
                Application.Run(form);
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.ToString(), "彩色文本生成器 v" + MainForm.Version + " 运行错误",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }
}
