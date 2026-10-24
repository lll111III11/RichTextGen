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
using Tesseract;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace RichTextGen
{
    /// <summary>
    /// 图片文字提取：主引擎为开源 Tesseract 5（自带中英文模型，离线），
    /// 引擎不可用时回退 Windows 10/11 自带 OCR。
    /// 长截图按 1500px 分段识别（段间重叠 160px），自动去掉接缝处重复行。
    /// </summary>
    internal static class Ocr
    {
        private const int StripHeight = 1500;
        private const int StripOverlap = 160;
        private const int MaxSide = 3200;
        private const long MaxPixels = 50L * 1024 * 1024;

        private static readonly object TessLock = new object();
        private static TesseractEngine _tess;
        private static string _tessDir;

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

        /// <summary>可用语言列表（Windows OCR 的语言包；Tesseract 内置中英文）。</summary>
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
                return "{\"ok\":true,\"list\":" + sb + "}";
            }
            catch (Exception ex)
            {
                return "{\"ok\":false,\"list\":[],\"error\":" + Quote(ex.Message) + "}";
            }
        }

        /// <summary>识别一张图片，返回纯文本。progress(百分比, 提示文字)。</summary>
        public static string Recognize(string path, string langTag, Action<int, string> progress, out string engine)
        {
            engine = "";
            Exception tessError = null;
            try
            {
                string text = RecognizeTesseract(path, langTag, progress);
                engine = "tesseract";
                return text;
            }
            catch (Exception ex) { tessError = ex; }

            try
            {
                string text = RecognizeWindows(path, langTag, progress);
                engine = "windows";
                return text;
            }
            catch (Exception ex)
            {
                throw new Exception("Tesseract 不可用（" + tessError.Message + "）；系统 OCR 也失败：" + ex.Message);
            }
        }

        // ============================================================ Tesseract（主引擎）
        private static string RecognizeTesseract(string path, string langTag, Action<int, string> progress)
        {
            string dataDir = EnsureTessData();
            if (dataDir == null) throw new Exception("缺少内置语言模型");

            lock (TessLock)
            {
                if (_tess == null)
                {
                    if (progress != null) progress(1, "正在加载识别引擎…");
                    _tess = new TesseractEngine(dataDir, TessLang(langTag), EngineMode.LstmOnly);
                    _tess.SetVariable("user_defined_dpi", "300");
                    _tess.SetVariable("preserve_interword_spaces", "1");
                }

                using (Bitmap bmp = LoadBitmap(path))
                {
                    int w = bmp.Width, h = bmp.Height;
                    if ((long)w * h > MaxPixels)
                        throw new Exception("图片像素过大（" + w + "×" + h + "）。请先裁切或压缩后再试。");

                    // 截图字号通常偏小：宽度不超过 1600 时放大 2 倍，Tesseract 识别率明显更好
                    double scale = (w <= 1600) ? 2.0 : 1.0;
                    if (w * scale > MaxSide) scale = (double)MaxSide / w;

                    int strips = (int)Math.Ceiling((double)h / StripHeight);
                    List<string> merged = new List<string>();

                    for (int i = 0; i < strips; i++)
                    {
                        int top = Math.Max(0, i * StripHeight - (i > 0 ? StripOverlap : 0));
                        int bottom = Math.Min(h, i * StripHeight + StripHeight);
                        int srcH = bottom - top;

                        using (Bitmap part = MakePart(bmp, new Rectangle(0, top, w, srcH), scale))
                        {
                            NormalizeForOcr(part);
                            using (Pix pix = PixConverter.ToPix(part))
                            using (Page page = _tess.Process(pix, PageSegMode.SingleBlock))
                            {
                                MergeStrip(merged, page.GetText());
                            }
                        }
                        if (progress != null)
                            progress((int)Math.Round((i + 1) * 100.0 / strips),
                                     "正在识别第 " + (i + 1) + "/" + strips + " 段…");
                    }
                    return string.Join("\n", merged.ToArray());
                }
            }
        }

        /// <summary>按比例裁剪一段并绘制成 32bpp 位图。</summary>
        private static Bitmap MakePart(Bitmap src, Rectangle rect, double scale)
        {
            int outW = Math.Max(1, (int)Math.Round(rect.Width * scale));
            int outH = Math.Max(1, (int)Math.Round(rect.Height * scale));
            Bitmap part = new Bitmap(outW, outH, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(part))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.DrawImage(src, new Rectangle(0, 0, outW, outH), rect, GraphicsUnit.Pixel);
            }
            return part;
        }

        /// <summary>深色底（夜间模式截图）先反色成浅底深字，Tesseract 对浅底更准。</summary>
        private static void NormalizeForOcr(Bitmap bmp)
        {
            try
            {
                if (!IsDark(bmp)) return;
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    ColorMatrix cm = new ColorMatrix(new float[][]
                    {
                        new float[] { -1, 0, 0, 0, 0 },
                        new float[] { 0, -1, 0, 0, 0 },
                        new float[] { 0, 0, -1, 0, 0 },
                        new float[] { 0, 0, 0, 1, 0 },
                        new float[] { 1, 1, 1, 0, 1 }
                    });
                    using (ImageAttributes ia = new ImageAttributes())
                    {
                        ia.SetColorMatrix(cm);
                        g.DrawImage(bmp, new Rectangle(0, 0, bmp.Width, bmp.Height),
                                    0, 0, bmp.Width, bmp.Height, GraphicsUnit.Pixel, ia);
                    }
                }
            }
            catch { }
        }

        private static bool IsDark(Bitmap bmp)
        {
            try
            {
                BitmapData data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height),
                                               ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                try
                {
                    int len = data.Stride * bmp.Height;
                    byte[] buf = new byte[len];
                    Marshal.Copy(data.Scan0, buf, 0, len);
                    long sum = 0;
                    int count = 0;
                    for (int i = 0; i + 3 < len; i += 4 * 41)
                    {
                        int b = buf[i], g = buf[i + 1], r = buf[i + 2];
                        sum += (r * 299 + g * 587 + b * 114) / 1000;
                        count++;
                    }
                    return count > 0 && (sum / count) < 118;
                }
                finally { bmp.UnlockBits(data); }
            }
            catch { return false; }
        }

        /// <summary>把新一段文本并进结果：去掉与上一段末尾重复的行（重叠区）。</summary>
        private static void MergeStrip(List<string> merged, string text)
        {
            string[] cur = SplitLines(text);
            int max = Math.Min(6, Math.Min(merged.Count, cur.Length));
            int skip = 0;
            for (int n = max; n >= 1; n--)
            {
                bool same = true;
                for (int j = 0; j < n; j++)
                {
                    if (!string.Equals(merged[merged.Count - n + j], cur[j], StringComparison.Ordinal))
                    { same = false; break; }
                }
                if (same) { skip = n; break; }
            }
            for (int j = skip; j < cur.Length; j++) merged.Add(cur[j]);
        }

        private static string[] SplitLines(string text)
        {
            List<string> list = new List<string>();
            string s = (text ?? "").Replace("\r\n", "\n").Replace('\r', '\n');
            foreach (string raw in s.Split('\n'))
            {
                string t = raw.Trim();
                if (t.Length == 0) continue;
                t = CjkSpace.Replace(t, "");
                list.Add(t);
            }
            return list.ToArray();
        }

        /// <summary>中英文模型随程序内嵌，首次识别时释放到 %APPDATA%\RichTextGen\tessdata。</summary>
        private static string EnsureTessData()
        {
            if (_tessDir != null) return _tessDir;
            try
            {
                string dir = Path.Combine(AiConfig.Dir, "tessdata");
                Directory.CreateDirectory(dir);
                Assembly asm = Assembly.GetExecutingAssembly();
                string[] names = asm.GetManifestResourceNames();
                foreach (string want in new string[] { "chi_sim.traineddata", "eng.traineddata" })
                {
                    string dest = Path.Combine(dir, want);
                    FileInfo fi = new FileInfo(dest);
                    if (fi.Exists && fi.Length > 100000) continue;
                    string res = null;
                    foreach (string n in names)
                    {
                        if (n.EndsWith(want, StringComparison.OrdinalIgnoreCase)) { res = n; break; }
                    }
                    if (res == null) continue;
                    using (Stream s = asm.GetManifestResourceStream(res))
                    using (FileStream fs = File.Create(dest))
                        s.CopyTo(fs);
                }
                _tessDir = dir;
                return dir;
            }
            catch { return null; }
        }

        private static string TessLang(string langTag)
        {
            if (!string.IsNullOrEmpty(langTag) &&
                langTag.StartsWith("en", StringComparison.OrdinalIgnoreCase)) return "eng+chi_sim";
            return "chi_sim+eng";
        }

        private static Bitmap LoadBitmap(string path)
        {
            try { return new Bitmap(path); }
            catch
            {
                // GDI+ 不认识的格式（WebP / AVIF / HEIC 等）先用系统解码器转成 PNG
                string tmp = ConvertToPng(path);
                try { return new Bitmap(tmp); }
                finally { try { File.Delete(tmp); } catch { } }
            }
        }

        // ============================================================ Windows OCR（回退）
        private static string RecognizeWindows(string path, string langTag, Action<int, string> progress)
        {
            OcrEngine engine = CreateEngine(langTag);
            if (engine == null)
                throw new Exception("系统没有可用的 OCR 语言包。请到 Windows 设置 → 时间和语言 → 语言，安装中文或英文语言包后重试。");

            using (Bitmap bmp = LoadBitmap(path))
            {
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
                    int outW = Math.Max(1, (int)Math.Round(w * scale));
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
        }

        private static OcrEngine CreateEngine(string langTag)
        {
            try
            {
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
                    if (l.Y - k.Y > tol) break;
                    if (Math.Abs(k.Y - l.Y) < tol && string.Equals(k.Text, t, StringComparison.Ordinal))
                    { dup = true; break; }
                }
                if (dup) continue;
                l.Text = t;
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

        public static string Recognize(string path, string langTag, Action<int, string> progress, out string engine)
        {
            engine = "";
            throw new Exception("当前构建未包含 OCR 组件（缺少 Windows SDK 元数据），请使用官方发布版或在装有 Windows SDK 的机器上重新构建。");
        }
    }
}
#endif
