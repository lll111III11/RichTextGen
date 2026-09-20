using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace RichTextGen
{
    public class NamedColor
    {
        public string Name;
        public Rgb Color;
        public NamedColor(string name, Rgb color) { Name = name; Color = color; }
    }

    /// <summary>在线取色（联网时优先联网，失败回退内置 140 色）+ 联网状态检测</summary>
    public static class OnlineColors
    {
        [DllImport("wininet.dll")]
        private static extern bool InternetGetConnectedState(out int flags, int reserved);

        public static List<NamedColor> Cached = new List<NamedColor>();
        public static string Source = "未获取";
        public static bool LastFetchOk = false;

        public static bool IsOnline()
        {
            try
            {
                int flags;
                return InternetGetConnectedState(out flags, 0);
            }
            catch { return true; }
        }

        public static List<NamedColor> BuiltIn()
        {
            List<NamedColor> list = new List<NamedColor>();
            foreach (KeyValuePair<string, Rgb> kv in ColorUtil.AllCss) list.Add(new NamedColor(kv.Key, kv.Value));
            return list;
        }

        /// <summary>当前应显示的颜色集合：有在线结果就用在线，否则内置</summary>
        public static List<NamedColor> Current()
        {
            return (Cached.Count > 0) ? Cached : BuiltIn();
        }

        public static bool Fetch()
        {
            List<NamedColor> list = null;
            string src = "";
            try
            {
                string txt = HttpGet("https://cdn.jsdelivr.net/npm/color-name@1.1.4/index.js", 6000);
                if (!string.IsNullOrEmpty(txt))
                {
                    list = ParseCssRgb(txt);
                    if (list.Count > 0) src = "jsdelivr color-name (" + list.Count + " 色)";
                }
                if ((list == null || list.Count == 0))
                {
                    txt = HttpGet("https://api.color.pizza/v1/?goodnamesonly=true", 9000);
                    if (!string.IsNullOrEmpty(txt))
                    {
                        list = ParsePizza(txt);
                        if (list.Count > 0) src = "color.pizza (" + list.Count + " 色)";
                    }
                }
            }
            catch { }

            if (list != null && list.Count > 0)
            {
                if (list.Count > 260) list = list.GetRange(0, 260);
                Cached = list;
                Source = src;
                LastFetchOk = true;
                return true;
            }
            Source = "获取失败（网络不可达）· 已回退内置 140 色";
            LastFetchOk = false;
            return false;
        }

        private static string HttpGet(string url, int timeoutMs)
        {
            try
            {
                ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12 | SecurityProtocolType.Tls11 | SecurityProtocolType.Tls;
                HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
                req.Timeout = timeoutMs;
                req.ReadWriteTimeout = timeoutMs;
                req.UserAgent = "RichTextGen/5.0";
                using (WebResponse resp = req.GetResponse())
                using (Stream s = resp.GetResponseStream())
                using (StreamReader sr = new StreamReader(s, Encoding.UTF8))
                    return sr.ReadToEnd();
            }
            catch { return ""; }
        }

        // 形如 "aliceblue": [240, 248, 255],
        private static List<NamedColor> ParseCssRgb(string txt)
        {
            List<NamedColor> list = new List<NamedColor>();
            Regex re = new Regex("\"([A-Za-z][A-Za-z0-9 _-]*)\"\\s*:\\s*\\[\\s*(\\d+)\\s*,\\s*(\\d+)\\s*,\\s*(\\d+)\\s*\\]");
            foreach (Match m in re.Matches(txt))
            {
                int r, g, b;
                if (int.TryParse(m.Groups[2].Value, out r) && int.TryParse(m.Groups[3].Value, out g) && int.TryParse(m.Groups[4].Value, out b))
                {
                    if (r <= 255 && g <= 255 && b <= 255)
                        list.Add(new NamedColor(m.Groups[1].Value, new Rgb((byte)r, (byte)g, (byte)b)));
                }
            }
            return list;
        }

        // 形如 {"name":"100 Mph","hex":"#c93f38", ...}
        private static List<NamedColor> ParsePizza(string txt)
        {
            List<string> names = new List<string>();
            List<string> hexes = new List<string>();
            foreach (Match m in new Regex("\"name\"\\s*:\\s*\"([^\"]+)\"").Matches(txt)) names.Add(m.Groups[1].Value);
            foreach (Match m in new Regex("\"hex\"\\s*:\\s*\"#?([0-9A-Fa-f]{6})\"").Matches(txt)) hexes.Add(m.Groups[1].Value);

            List<NamedColor> list = new List<NamedColor>();
            int n = Math.Min(names.Count, hexes.Count);
            for (int i = 0; i < n; i++)
            {
                Rgb c;
                if (ColorUtil.TryParse(hexes[i], out c)) list.Add(new NamedColor(names[i], c));
            }
            return list;
        }
    }
}
