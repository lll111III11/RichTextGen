using System;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace RichTextGen
{
    /// <summary>
    /// 程序内自动更新：读仓库的 version.json → 比对版本 → 下载安装包
    /// 清单优先走 jsdelivr（国内更稳），失败回退 raw.githubusercontent
    /// </summary>
    public static class Updater
    {
        public const string Primary = "https://cdn.jsdelivr.net/gh/lll111III11/RichTextGen@main/version.json";
        public const string Fallback = "https://raw.githubusercontent.com/lll111III11/RichTextGen/main/version.json";
        public const string ReleasePage = "https://github.com/lll111III11/RichTextGen/releases/latest";

        public class Info
        {
            public string Version = "";
            public bool Mandatory;
            public string Installer = "";
            public string Portable = "";
            public string Notes = "";
            public string Source = "";
        }

        private static string HttpGet(string url, int timeoutMs)
        {
            try
            {
                ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12 | SecurityProtocolType.Tls11 | SecurityProtocolType.Tls;
                HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
                req.Timeout = timeoutMs;
                req.ReadWriteTimeout = timeoutMs;
                req.UserAgent = "RichTextGen/" + MainForm.Version;
                using (WebResponse resp = req.GetResponse())
                using (Stream s = resp.GetResponseStream())
                using (StreamReader sr = new StreamReader(s, Encoding.UTF8))
                    return sr.ReadToEnd();
            }
            catch { return ""; }
        }

        /// <summary>拉取更新清单（jsdelivr → raw）</summary>
        public static Info Check(out string error)
        {
            error = "";
            string txt = HttpGet(Primary, 8000);
            string src = "jsdelivr";
            if (string.IsNullOrEmpty(txt) || txt.IndexOf("\"version\"", StringComparison.Ordinal) < 0)
            {
                txt = HttpGet(Fallback, 8000);
                src = "raw.githubusercontent";
            }
            if (string.IsNullOrEmpty(txt))
            {
                error = "无法获取更新清单（网络不可达）";
                return null;
            }
            Info info = new Info();
            info.Source = src;
            info.Version = Field(txt, "version");
            info.Installer = Field(txt, "installer");
            info.Portable = Field(txt, "portable");
            info.Notes = Field(txt, "notes");
            info.Mandatory = Regex.IsMatch(txt, "\"mandatory\"\\s*:\\s*true", RegexOptions.IgnoreCase);
            if (info.Version.Length == 0) { error = "清单格式异常"; return null; }
            return info;
        }

        private static string Field(string json, string key)
        {
            Match m = Regex.Match(json, "\"" + key + "\"\\s*:\\s*\"([^\"]*)\"");
            return m.Success ? m.Groups[1].Value : "";
        }

        /// <summary>远端版本是否比本地新（按 4 段数字比较）</summary>
        public static bool IsNewer(string remote, string local)
        {
            int[] a = Parts(remote), b = Parts(local);
            for (int i = 0; i < 4; i++)
            {
                if (a[i] > b[i]) return true;
                if (a[i] < b[i]) return false;
            }
            return false;
        }

        private static int[] Parts(string v)
        {
            int[] r = new int[4];
            if (string.IsNullOrEmpty(v)) return r;
            string[] p = v.Split('.');
            for (int i = 0; i < 4 && i < p.Length; i++)
            {
                int n;
                int.TryParse(Regex.Replace(p[i], "[^0-9]", ""), out n);
                r[i] = n;
            }
            return r;
        }

        /// <summary>下载更新包到临时目录，返回文件路径</summary>
        public static string Download(string url, Action<int, string> progress)
        {
            try
            {
                string name = "RichTextGen-Setup-" + DateTime.Now.ToString("HHmmss") + ".exe";
                string path = Path.Combine(Path.GetTempPath(), name);
                ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12 | SecurityProtocolType.Tls11 | SecurityProtocolType.Tls;
                HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
                req.Timeout = 30000;
                req.UserAgent = "RichTextGen/" + MainForm.Version;
                using (WebResponse resp = req.GetResponse())
                using (Stream s = resp.GetResponseStream())
                using (FileStream fs = File.Create(path))
                {
                    long total = resp.ContentLength;
                    byte[] buf = new byte[65536];
                    long done = 0;
                    int n;
                    while ((n = s.Read(buf, 0, buf.Length)) > 0)
                    {
                        fs.Write(buf, 0, n);
                        done += n;
                        if (progress != null)
                        {
                            int pct = total > 0 ? (int)(done * 100 / total) : 50;
                            progress(Math.Min(100, pct), "已下载 " + (done / 1024) + " KB" + (total > 0 ? " / " + (total / 1024) + " KB" : ""));
                        }
                    }
                }
                return path;
            }
            catch { return ""; }
        }
    }
}
