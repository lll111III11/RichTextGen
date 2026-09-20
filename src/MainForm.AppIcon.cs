using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Windows.Forms;

namespace RichTextGen
{
    public partial class MainForm
    {
        /// <summary>程序图标：用 exe 自身嵌入的图标（编译时 /win32icon 写入）</summary>
        private static Icon LoadAppIcon()
        {
            try
            {
                Icon ico = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
                if (ico != null) return ico;
            }
            catch { }
            return SystemIcons.Application;
        }

        /// <summary>
        /// 把编译进 exe 的图片释放到 %APPDATA%\RichTextGen\内置图片.png（首次运行时释放），
        /// 返回路径；可用于「背景图 → 使用内置图片」。找不到资源时返回最近一次释放的路径或空串。
        /// </summary>
        public static string EnsureEmbeddedBackground()
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RichTextGen");
            string path = Path.Combine(dir, "内置图片.png");
            try
            {
                Assembly asm = typeof(MainForm).Assembly;
                string res = null;
                foreach (string n in asm.GetManifestResourceNames())
                {
                    if (n.EndsWith("appbg.png", StringComparison.OrdinalIgnoreCase)) { res = n; break; }
                }
                if (res == null) return File.Exists(path) ? path : "";
                Directory.CreateDirectory(dir);
                if (!File.Exists(path))
                {
                    using (Stream s = asm.GetManifestResourceStream(res))
                    {
                        if (s == null) return File.Exists(path) ? path : "";
                        using (FileStream fs = File.Create(path)) s.CopyTo(fs);
                    }
                }
                return path;
            }
            catch { return File.Exists(path) ? path : ""; }
        }
    }
}
