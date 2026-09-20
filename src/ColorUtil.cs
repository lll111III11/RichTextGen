using System;
using System.Collections.Generic;
using System.Drawing;

namespace RichTextGen
{
    /// <summary>纯 RGB 颜色值（与 System.Drawing 解耦，方便在标签表里传递）</summary>
    public struct Rgb
    {
        public byte R;
        public byte G;
        public byte B;

        public Rgb(byte r, byte g, byte b) { R = r; G = g; B = b; }

        public Color ToColor() { return Color.FromArgb(255, R, G, B); }

        public string Hex { get { return string.Format("{0:X2}{1:X2}{2:X2}", R, G, B); } }
        public string HexSharp { get { return "#" + Hex; } }
        public string RgbText { get { return R + "," + G + "," + B; } }

        public override string ToString() { return HexSharp; }
    }

    /// <summary>颜色解析 / 转换 / CSS 色名（三种表达方式：Hex、RGB、英文色名）</summary>
    public static class ColorUtil
    {
        // 标准 CSS/HTML 140 色（含重复色名，用于「色名模式」与最近色匹配）
        private const string CssTable =
            "indianred=CD5C5C;lightcoral=F08080;salmon=FA8072;darksalmon=E9967A;lightsalmon=FFA07A;" +
            "crimson=DC143C;red=FF0000;firebrick=B22222;darkred=8B0000;coral=FF7F50;tomato=FF6347;" +
            "orangered=FF4500;gold=FFD700;orange=FFA500;darkorange=FF8C00;lightyellow=FFFFE0;" +
            "lemonchiffon=FFFACD;lightgoldenrodyellow=FAFAD2;papayawhip=FFEFD5;moccasin=FFE4B5;" +
            "peachpuff=FFDAB9;palegoldenrod=EEE8AA;khaki=F0E68C;darkkhaki=BDB76B;yellow=FFFF00;" +
            "cornsilk=FFF8DC;blanchedalmond=FFEBCD;bisque=FFE4C4;navajowhite=FFDEAD;wheat=F5DEB3;" +
            "burlywood=DEB887;tan=D2B48C;rosybrown=BC8F8F;sandybrown=F4A460;goldenrod=DAA520;" +
            "darkgoldenrod=B8860B;peru=CD853F;chocolate=D2691E;saddlebrown=8B4513;sienna=A0522D;" +
            "brown=A52A2A;maroon=800000;pink=FFC0CB;lightpink=FFB6C1;hotpink=FF69B4;deeppink=FF1493;" +
            "palevioletred=DB7093;mediumvioletred=C71585;lavenderblush=FFF0F5;mistyrose=FFE4E1;" +
            "lavender=E6E6FA;thistle=D8BFD8;plum=DDA0DD;violet=EE82EE;orchid=DA70D6;fuchsia=FF00FF;" +
            "magenta=FF00FF;mediumorchid=BA55D3;mediumpurple=9370DB;blueviolet=8A2BE2;darkviolet=9400D3;" +
            "darkorchid=9932CC;darkmagenta=8B008B;purple=800080;indigo=4B0082;lawngreen=7CFC00;" +
            "chartreuse=7FFF00;limegreen=32CD32;lime=00FF00;forestgreen=228B22;green=008000;" +
            "darkgreen=006400;greenyellow=ADFF2F;yellowgreen=9ACD32;springgreen=00FF7F;" +
            "mediumspringgreen=00FA9A;lightgreen=90EE90;palegreen=98FB98;darkseagreen=8FBC8F;" +
            "mediumseagreen=3CB371;seagreen=2E8B57;olive=808000;darkolivegreen=556B2F;olivedrab=6B8E23;" +
            "lightcyan=E0FFFF;cyan=00FFFF;aqua=00FFFF;aquamarine=7FFFD4;mediumaquamarine=66CDAA;" +
            "paleturquoise=AFEEEE;turquoise=40E0D0;mediumturquoise=48D1CC;darkturquoise=00CED1;" +
            "lightseagreen=20B2AA;cadetblue=5F9EA0;darkcyan=008B8B;teal=008080;powderblue=B0E0E6;" +
            "lightblue=ADD8E6;lightskyblue=87CEFA;skyblue=87CEEB;deepskyblue=00BFFF;" +
            "lightsteelblue=B0C4DE;dodgerblue=1E90FF;cornflowerblue=6495ED;steelblue=4682B4;" +
            "royalblue=4169E1;blue=0000FF;mediumblue=0000CD;darkblue=00008B;navy=000080;" +
            "midnightblue=191970;mediumslateblue=7B68EE;slateblue=6A5ACD;darkslateblue=483D8B;" +
            "gainsboro=DCDCDC;lightgray=D3D3D3;silver=C0C0C0;darkgray=A9A9A9;gray=808080;" +
            "dimgray=696969;lightslategray=778899;slategray=708090;darkslategray=2F4F4F;black=000000;" +
            "white=FFFFFF;snow=FFFAFA;honeydew=F0FFF0;mintcream=F5FFFA;azure=F0FFFF;aliceblue=F0F8FF;" +
            "ghostwhite=F8F8FF;whitesmoke=F5F5F5;seashell=FFF5EE;beige=F5F5DC;oldlace=FDF5E6;" +
            "floralwhite=FFFAF0;ivory=FFFFF0;antiquewhite=FAEBD7;linen=FAF0E6;";

        private static Dictionary<string, Rgb> _byName;
        private static List<KeyValuePair<string, Rgb>> _all;

        public static List<KeyValuePair<string, Rgb>> AllCss
        {
            get { EnsureTable(); return _all; }
        }

        private static void EnsureTable()
        {
            if (_byName != null) return;
            Dictionary<string, Rgb> byName = new Dictionary<string, Rgb>(StringComparer.OrdinalIgnoreCase);
            List<KeyValuePair<string, Rgb>> all = new List<KeyValuePair<string, Rgb>>();
            string[] parts = CssTable.Split(';');
            for (int i = 0; i < parts.Length; i++)
            {
                string p = parts[i];
                if (p.Length == 0) continue;
                int eq = p.IndexOf('=');
                if (eq <= 0) continue;
                string name = p.Substring(0, eq);
                string hex = p.Substring(eq + 1);
                Rgb c;
                if (TryParseHex6(hex, out c))
                {
                    if (!byName.ContainsKey(name)) byName[name] = c;
                    all.Add(new KeyValuePair<string, Rgb>(name, c));
                }
            }
            _byName = byName;
            _all = all;
        }

        private static bool TryParseHex6(string hex, out Rgb c)
        {
            c = new Rgb(0, 0, 0);
            if (hex == null || hex.Length != 6) return false;
            try
            {
                c = new Rgb(
                    Convert.ToByte(hex.Substring(0, 2), 16),
                    Convert.ToByte(hex.Substring(2, 2), 16),
                    Convert.ToByte(hex.Substring(4, 2), 16));
                return true;
            }
            catch { return false; }
        }

        /// <summary>解析 #RRGGBB / #RGB / RRGGBB / 英文色名 / "255,128,0" / "rgb(255,128,0)"</summary>
        public static bool TryParse(string input, out Rgb c)
        {
            c = new Rgb(255, 255, 255);
            if (input == null) return false;
            string s = input.Trim();
            if (s.Length == 0) return false;

            string low = s.ToLowerInvariant();
            if (low.StartsWith("rgb"))
            {
                int lp = low.IndexOf('(');
                int rp = low.IndexOf(')');
                if (lp >= 0 && rp > lp) s = s.Substring(lp + 1, rp - lp - 1);
            }
            if (s.StartsWith("#")) s = s.Substring(1);

            // 十六进制（#RRGGBB / #RGB / #RRGGBBAA）—— 必须先确认整串都是十六进制字符，
            // 否则 "255,136,0" 这类 RGB 输入会被误判并提前返回
            string hexOnly = s.Trim();
            bool allHex = hexOnly.Length > 0;
            for (int i = 0; i < hexOnly.Length; i++)
            {
                char ch = hexOnly[i];
                bool ok = (ch >= '0' && ch <= '9') || (ch >= 'a' && ch <= 'f') || (ch >= 'A' && ch <= 'F');
                if (!ok) { allHex = false; break; }
            }
            if (allHex && hexOnly.Length == 3)
            {
                hexOnly = new string(new char[] { hexOnly[0], hexOnly[0], hexOnly[1], hexOnly[1], hexOnly[2], hexOnly[2] });
                return TryParseHex6(hexOnly, out c);
            }
            if (allHex && hexOnly.Length == 6)
                return TryParseHex6(hexOnly, out c);
            if (allHex && hexOnly.Length >= 8)
                return TryParseHex6(hexOnly.Substring(0, 6), out c);   // #RRGGBBAA：取前 6 位

            // R,G,B / R G B
            string[] nums = s.Split(new char[] { ',', '，', ' ', '\t', ';' }, StringSplitOptions.RemoveEmptyEntries);
            if (nums.Length >= 3)
            {
                int r, g, b;
                if (int.TryParse(nums[0], out r) && int.TryParse(nums[1], out g) && int.TryParse(nums[2], out b))
                {
                    if (r >= 0 && r <= 255 && g >= 0 && g <= 255 && b >= 0 && b <= 255)
                    {
                        c = new Rgb((byte)r, (byte)g, (byte)b);
                        return true;
                    }
                }
            }

            // 英文色名
            EnsureTable();
            Rgb named;
            if (_byName.TryGetValue(s, out named))
            {
                c = named;
                return true;
            }
            return false;
        }

        /// <summary>该颜色是否有精确的 CSS 英文色名</summary>
        public static bool HasExactName(Rgb c, out string name)
        {
            EnsureTable();
            foreach (KeyValuePair<string, Rgb> kv in _all)
            {
                if (kv.Value.R == c.R && kv.Value.G == c.G && kv.Value.B == c.B)
                {
                    name = kv.Key;
                    return true;
                }
            }
            name = "";
            return false;
        }

        /// <summary>最接近的 CSS 英文色名</summary>
        public static string NearestName(Rgb c, out int distance)
        {
            EnsureTable();
            string best = "white";
            int bestD = int.MaxValue;
            foreach (KeyValuePair<string, Rgb> kv in _all)
            {
                int dr = kv.Value.R - c.R;
                int dg = kv.Value.G - c.G;
                int db = kv.Value.B - c.B;
                int d = dr * dr + dg * dg + db * db;
                if (d < bestD) { bestD = d; best = kv.Key; }
            }
            distance = bestD;
            return best;
        }

        public static Rgb FromHsv(double h, double s, double v)
        {
            h = h % 360.0;
            if (h < 0) h += 360.0;
            s = Clamp01(s);
            v = Clamp01(v);
            double c = v * s;
            double x = c * (1 - Math.Abs((h / 60.0) % 2 - 1));
            double m = v - c;
            double r = 0, g = 0, b = 0;
            if (h < 60) { r = c; g = x; }
            else if (h < 120) { r = x; g = c; }
            else if (h < 180) { g = c; b = x; }
            else if (h < 240) { g = x; b = c; }
            else if (h < 300) { r = x; b = c; }
            else { r = c; b = x; }
            return new Rgb(
                (byte)Math.Round((r + m) * 255),
                (byte)Math.Round((g + m) * 255),
                (byte)Math.Round((b + m) * 255));
        }

        public static void ToHsv(Rgb c, out double h, out double s, out double v)
        {
            double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
            double max = Math.Max(r, Math.Max(g, b));
            double min = Math.Min(r, Math.Min(g, b));
            double d = max - min;
            h = 0;
            if (d > 0)
            {
                if (max == r) h = 60 * (((g - b) / d) % 6);
                else if (max == g) h = 60 * (((b - r) / d) + 2);
                else h = 60 * (((r - g) / d) + 4);
            }
            if (h < 0) h += 360;
            s = (max <= 0) ? 0 : (d / max);
            v = max;
        }

        private static double Clamp01(double x) { return x < 0 ? 0 : (x > 1 ? 1 : x); }

        /// <summary>按输出模式生成 Unity/TMP 的颜色参数文本</summary>
        public static string ToTagValue(Rgb c, ColorOutputMode mode)
        {
            if (mode == ColorOutputMode.Name)
            {
                string exact;
                if (HasExactName(c, out exact)) return exact;
                int dist;
                string near = NearestName(c, out dist);
                return near; // 名称模式：无精确名时用最近色名，界面会给出提示
            }
            return c.HexSharp;
        }
    }

    public enum ColorOutputMode
    {
        Hex = 0,
        Rgb = 1,
        Name = 2
    }
}
