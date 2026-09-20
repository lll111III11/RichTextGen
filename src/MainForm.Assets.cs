using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Windows.Forms;

namespace RichTextGen
{
    public partial class MainForm
    {
        /// <summary>用户配置目录（热键、释放出来的内置图片都放这里）</summary>
        public static string AppDataDir
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RichTextGen"); }
        }

        /// <summary>内嵌背景图释放后的路径</summary>
        public static string BuiltInBgPath
        {
            get { return Path.Combine(AppDataDir, "内置背景.png"); }
        }

        /// <summary>运行时释放 exe 内嵌资源（内置背景图），并套用 exe 自带图标</summary>
        private void ReleaseEmbeddedAssets()
        {
            try { Directory.CreateDirectory(AppDataDir); } catch { }

            try
            {
                Assembly asm = Assembly.GetExecutingAssembly();
                string hit = null;
                foreach (string n in asm.GetManifestResourceNames())
                    if (n.EndsWith("appbg.png", StringComparison.OrdinalIgnoreCase)) { hit = n; break; }
                if (hit != null)
                {
                    using (Stream s = asm.GetManifestResourceStream(hit))
                    {
                        if (s != null && s.Length > 0)
                        {
                            byte[] buf = new byte[s.Length];
                            int read = 0;
                            while (read < buf.Length)
                            {
                                int n = s.Read(buf, read, buf.Length - read);
                                if (n <= 0) break;
                                read += n;
                            }
                            bool need = true;
                            try
                            {
                                if (File.Exists(BuiltInBgPath) && new FileInfo(BuiltInBgPath).Length == buf.Length) need = false;
                            }
                            catch { }
                            if (need) File.WriteAllBytes(BuiltInBgPath, buf);
                        }
                    }
                }
            }
            catch { }

            try
            {
                Icon ico = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
                if (ico != null)
                {
                    this.Icon = ico;
                    if (_tray != null) _tray.Icon = ico;
                }
            }
            catch { }
        }
    }
}
