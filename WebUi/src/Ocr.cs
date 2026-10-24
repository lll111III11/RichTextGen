#if !NO_WINRT
using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using System.Text.RegularExpressions;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace RichTextGen
{
    /// <summary>
    /// 图片文字提取（OCR）：用 Windows 10/11 自带的 Windows.Media.Ocr，离线、无需联网。
    /// 长截图按 1500px 分段识别（带 160px 重叠），再把重复行去掉拼成整段文本。
    /// </summary>
    internal static class Ocr
    {
        private const int StripHeight = 1500;     // 每段高度
        private const int StripOverlap = 160;     // 段与段之间重叠，避免文字被切在边界
        private const int MaxSide = 3200;         // 单边超过就等比缩小，兼顾精度与内存
        private const long MaxPixels = 50L * 1024 * 1024;

        /// <summary>Windows OCR 会在中文之间插空格（「测 试 成 功」），这里把纯中文之间的空格去掉。</summary>
        private static readonly Regex CjkSpace = new Regex(
            "(?<=[\\u3400-\\u9fff\\uf900-\\ufaff\\u3000-\\u303f\\uff00-\\uffef])\\s+(?=[\\u3400-\\u9fff\\uf900-\\ufaff\\u3000-\\u303f\\uff00-\\uffef])",
            RegexOptions.Compiled);

        private sealed class Line
        {
            public string Text;
            public double X;
            public double Y;
        }

        /// <summary>可用语言列表（未安装 OCR 语言包时 ok=false）。</summary>
        public static string LanguagesJson()
        {
            try
            {
                List<object> list = AvailableLanguages();
                StringBuilder sb = new StringBuilder("[");
                bool first = true;
                foreach (object l in list)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    sb.Append("{\"tag\":\"").Append(Esc(StrProp(l, "LanguageTag")))
                      .Append("\",\"name\":\"").Append(Esc(StrProp(l, "DisplayName"))).Append("\"}");
                }
                sb.Append(']');
                if (list.Count == 0)
                    return "{\"ok\":false,\"list\":[],\"error\":" + Quote("系统里没有可用的 OCR 语言包，请到 Windows 设置 → 时间和语言 → 语言，安装中文或英文语言包") + "}";
                return "{\"ok\":true,\"list\":" + sb + "}";
            }
            catch (Exception ex)
            {
                return "{\"ok\":false,\"list\":[],\"error\":" + Quote(ex.Message) + "}";
            }
        }

        /// <summary>识别一张图片，返回纯文本。progress(百分比, 提示文字)。</summary>
        public static string Recognize(string path, string langTag, Action<int, string> progress)
        {
            OcrEngine engine = CreateEngine(langTag);
            if (engine == null)
                throw new Exception("系统没有可用的 OCR 语言包。请到 Windows 设置 → 时间和语言 → 语言，安装中文或英文语言包后重试。");

            string tmp = null;
            Bitmap bmp = null;
            try
            {
                try { bmp = new Bitmap(path); }
                catch
                {
                    // GDI+ 不认识的格式（WebP / AVIF / HEIC 等）先用系统解码器转成 PNG
                    tmp = ConvertToPng(path);
                    bmp = new Bitmap(tmp);
                }

                int w = bmp.Width, h = bmp.Height;
                if ((long)w * h > MaxPixels)
                    throw new Exception("图片像素过大（" + w + "×" + h + "）。请先裁切或压缩后再试。");

                double scale = w > MaxSide ? (double)MaxSide / w : 1.0;
                int strips = (int)Math.Ceiling((double)h / StripHeight);
                List<Line> lines = new List<Line>();
                List<int> seams = new List<int>();
                for (int i = 1; i < strips; i++) seams.Add((int)Math.Round(i * StripHeight * scale));

                for (int i = 0; i < strips; i++)
                {
                    int top = Math.Max(0, i * StripHeight - (i > 0 ? StripOverlap : 0));
                    int bottom = Math.Min(h, i * StripHeight + StripHeight);
                    int srcH = bottom - top;
                    int outW = (int)Math.Round(w * scale);
                    int outH = Math.Max(1, (int)Math.Round(srcH * scale));

                    using (Bitmap part = new Bitmap(outW, outH, PixelFormat.Format32bppArgb))
                    {
                        using (Graphics g = Graphics.FromImage(part))
                        {
                            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                            g.DrawImage(bmp, new Rectangle(0, 0, outW, outH),
                                        new Rectangle(0, top, w, srcH), GraphicsUnit.Pixel);
                        }
                        using (SoftwareBitmap sb = ToSoftwareBitmap(part))
                        {
                            OcrResult res = engine.RecognizeAsync(sb).AsTask().GetAwaiter().GetResult();
                            Collect(res, lines, (int)Math.Round(top * scale));
                        }
                    }
                    if (progress != null)
                        progress((int)Math.Round((i + 1) * 100.0 / strips),
                                 "正在识别第 " + (i + 1) + "/" + strips + " 段…");
                }
                return JoinLines(lines, seams);
            }
            finally
            {
                if (bmp != null) bmp.Dispose();
                if (tmp != null) { try { File.Delete(tmp); } catch { } }
            }
        }

        private static OcrEngine CreateEngine(string langTag)
        {
            try
            {
                // Windows.Globalization.Language 在当前 SDK 元数据里不可直接引用，
                // 因此用反射创建；拿不到就退回「系统语言」。
                if (!string.IsNullOrEmpty(langTag))
                {
                    Type lt = Type.GetType("Windows.Globalization.Language, Windows.Globalization, ContentType=WindowsRuntime");
                    if (lt != null)
                    {
                        object lang = Activator.CreateInstance(lt, new object[] { langTag });
                        foreach (MethodInfo m in typeof(OcrEngine).GetMethods())
                        {
                            if (m.Name != "TryCreateFromLanguage") continue;
                            ParameterInfo[] ps = m.GetParameters();
                            if (ps.Length != 1 || !ps[0].ParameterType.IsInstanceOfType(lang)) continue;
                            OcrEngine e = m.Invoke(null, new object[] { lang }) as OcrEngine;
                            if (e != null) return e;
                        }
                    }
                }
                return OcrEngine.TryCreateFromUserProfileLanguages();
            }
            catch { return null; }
        }

        private static List<object> AvailableLanguages()
        {
            List<object> list = new List<object>();
            try
            {
                PropertyInfo p = typeof(OcrEngine).GetProperty("AvailableRecognizerLanguages");
                if (p == null) return list;
                IEnumerable en = p.GetValue(null, null) as IEnumerable;
                if (en == null) return list;
                foreach (object o in en) list.Add(o);
            }
            catch { }
            return list;
        }

        private static string StrProp(object o, string name)
        {
            try
            {
                if (o == null) return "";
                PropertyInfo p = o.GetType().GetProperty(name);
                return p == null ? "" : ((p.GetValue(o, null) as string) ?? "");
            }
            catch { return ""; }
        }

        /// <summary>把 Bitmap 转成 OCR 需要的 SoftwareBitmap（BGRA8）。</summary>
        private static SoftwareBitmap ToSoftwareBitmap(Bitmap bmp)
        {
            BitmapData data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height),
                                           ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                int len = data.Stride * bmp.Height;
                byte[] bytes = new byte[len];
                Marshal.Copy(data.Scan0, bytes, 0, len);
                return SoftwareBitmap.CreateCopyFromBuffer(
                    bytes.AsBuffer(), BitmapPixelFormat.Bgra8, bmp.Width, bmp.Height, BitmapAlphaMode.Premultiplied);
            }
            finally { bmp.UnlockBits(data); }
        }

        /// <summary>用系统解码器把任意支持的图片转成临时 PNG，交给 GDI+ 处理。</summary>
        private static string ConvertToPng(string src)
        {
            string tmp = Path.Combine(Path.GetTempPath(), "rtg-ocr-" + Guid.NewGuid().ToString("N") + ".png");
            using (FileStream fs = File.OpenRead(src))
            using (IRandomAccessStream ras = fs.AsRandomAccessStream())
            {
                BitmapDecoder dec = BitmapDecoder.CreateAsync(ras).AsTask().GetAwaiter().GetResult();
                SoftwareBitmap sb = dec.GetSoftwareBitmapAsync().AsTask().GetAwaiter().GetResult();
                using (FileStream outFs = File.Create(tmp))
                using (IRandomAccessStream outRas = outFs.AsRandomAccessStream())
                {
                    BitmapEncoder enc = BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, outRas)
                                                    .AsTask().GetAwaiter().GetResult();
                    enc.SetSoftwareBitmap(sb);
                    enc.FlushAsync().AsTask().GetAwaiter().GetResult();
                }
            }
            return tmp;
        }

        private static void Collect(OcrResult res, List<Line> lines, int offsetY)
        {
            if (res == null) return;
            foreach (OcrLine line in res.Lines)
            {
                double x = double.MaxValue, y = double.MaxValue;
                foreach (OcrWord word in line.Words)
                {
                    Windows.Foundation.Rect r = word.BoundingRect;
                    if (r.X < x) x = r.X;
                    if (r.Y < y) y = r.Y;
                }
                if (x == double.MaxValue) x = 0;
                if (y == double.MaxValue) y = 0;
                lines.Add(new Line { Text = line.Text, X = x, Y = offsetY + y });
            }
        }

        /// <summary>按纵坐标排序，再去掉分段重叠造成的重复行。</summary>
        private static string JoinLines(List<Line> lines, List<int> seams)
        {
            lines.Sort(delegate(Line a, Line b)
            {
                if (Math.Abs(a.Y - b.Y) > 12) return a.Y < b.Y ? -1 : 1;
                if (Math.Abs(a.X - b.X) < 1) return 0;
                return a.X < b.X ? -1 : 1;
            });

            StringBuilder sb = new StringBuilder();
            List<Line> kept = new List<Line>();
            foreach (Line l in lines)
            {
                string t = (l.Text ?? "").Trim();
                if (t.Length == 0) continue;
                t = CjkSpace.Replace(t, "");

                // 分段接缝附近的同一行会被识别两次：放宽纵向容差，按文字去重
                double tol = 28;
                if (seams != null)
                {
                    foreach (int s in seams)
                    {
                        if (l.Y >= s - StripOverlap - 40 && l.Y <= s + 40) { tol = 110; break; }
                    }
                }

                bool dup = false;
                for (int i = kept.Count - 1; i >= 0; i--)
                {
                    Line k = kept[i];
                    if (l.Y - k.Y > tol) break;      // 只看附近的行，避免整表扫描
                    if (Math.Abs(k.Y - l.Y) < tol && string.Equals(k.Text, t, StringComparison.Ordinal))
                    {
                        dup = true;
                        break;
                    }
                }
                if (dup) continue;
                l.Text = t;                 // 存整理后的文字，重叠去重时才比得到一起
                kept.Add(l);
                if (sb.Length > 0) sb.Append('\n');
                sb.Append(t);
            }
            return sb.ToString();
        }

        private static string Esc(string s)
        {
            if (s == null) return "";
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "").Replace("\n", "\\n");
        }

        private static string Quote(string s)
        {
            return "\"" + Esc(s) + "\"";
        }
    }
}
#else
using System;

namespace RichTextGen
{
    /// <summary>没有 Windows SDK 元数据时的占位实现：界面照常构建，只是 OCR 不可用。</summary>
    internal static class Ocr
    {
        public static string LanguagesJson()
        {
            return "{\"ok\":false,\"list\":[],\"error\":\"当前构建未包含 OCR 组件（缺少 Windows SDK）\"}";
        }

        public static string Recognize(string path, string langTag, Action<int, string> progress)
        {
            throw new Exception("当前构建未包含 OCR 组件（缺少 Windows SDK 元数据），请使用官方发布版或在装有 Windows SDK 的机器上重新构建。");
        }
    }
}
#endif
