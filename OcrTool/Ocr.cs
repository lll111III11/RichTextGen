using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Tesseract;

namespace RichTextGenOcr
{
    /// <summary>
    /// 图片文字提取：Tesseract 5 + 内置中英文模型，完全离线。
    /// 长截图按 1500px 分段识别（重叠 160px），自动去掉接缝重复行。
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

        private static readonly Regex CjkSpace = new Regex(
            "(?<=[\\u3400-\\u9fff\\uf900-\\ufaff\\u3000-\\u303f\\uff00-\\uffef])\\s+(?=[\\u3400-\\u9fff\\uf900-\\ufaff\\u3000-\\u303f\\uff00-\\uffef])",
            RegexOptions.Compiled);

        public static string TessDataDir
        {
            get
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "RichTextGenOCR", "tessdata");
                Directory.CreateDirectory(dir);
                return dir;
            }
        }

        public static string Recognize(string path, Action<int, string> progress)
        {
            string dataDir = EnsureTessData();
            lock (TessLock)
            {
                if (_tess == null)
                {
                    if (progress != null) progress(1, "正在加载识别引擎…");
                    _tess = new TesseractEngine(dataDir, "chi_sim+eng", EngineMode.LstmOnly);
                    _tess.SetVariable("user_defined_dpi", "300");
                    _tess.SetVariable("preserve_interword_spaces", "1");
                }

                using (Bitmap bmp = LoadBitmap(path))
                {
                    int w = bmp.Width, h = bmp.Height;
                    if ((long)w * h > MaxPixels)
                        throw new Exception("图片像素过大（" + w + "×" + h + "）。请先裁切或压缩后再试。");

                    double scale = (w <= 1600) ? 2.0 : 1.0;
                    if (w * scale > MaxSide) scale = (double)MaxSide / w;

                    int strips = (int)Math.Ceiling((double)h / StripHeight);
                    List<string> merged = new List<string>();

                    for (int i = 0; i < strips; i++)
                    {
                        int top = Math.Max(0, i * StripHeight - (i > 0 ? StripOverlap : 0));
                        int bottom = Math.Min(h, i * StripHeight + StripHeight);
                        using (Bitmap part = MakePart(bmp, new Rectangle(0, top, w, bottom - top), scale))
                        {
                            NormalizeForOcr(part);
                            using (Pix pix = PixConverter.ToPix(part))
                            using (Page page = _tess.Process(pix, PageSegMode.SingleBlock))
                                MergeStrip(merged, page.GetText());
                        }
                        if (progress != null)
                            progress((int)Math.Round((i + 1) * 100.0 / strips),
                                     "正在识别第 " + (i + 1) + "/" + strips + " 段…");
                    }
                    return string.Join("\n", merged.ToArray());
                }
            }
        }

        private static Bitmap LoadBitmap(string path)
        {
            return new Bitmap(path);
        }

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
                    long sum = 0; int count = 0;
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
            foreach (string raw in (text ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
            {
                string t = raw.Trim();
                if (t.Length == 0) continue;
                list.Add(CjkSpace.Replace(t, ""));
            }
            return list.ToArray();
        }

        private static string EnsureTessData()
        {
            if (_tessDir != null) return _tessDir;
            string dir = TessDataDir;
            Assembly asm = Assembly.GetExecutingAssembly();
            string[] names = asm.GetManifestResourceNames();
            foreach (string want in new string[] { "chi_sim.traineddata", "eng.traineddata" })
            {
                string dest = Path.Combine(dir, want);
                FileInfo fi = new FileInfo(dest);
                if (fi.Exists && fi.Length > 100000) continue;
                string res = null;
                foreach (string n in names)
                    if (n.EndsWith(want, StringComparison.OrdinalIgnoreCase)) { res = n; break; }
                if (res == null) continue;
                using (Stream s = asm.GetManifestResourceStream(res))
                using (FileStream fs = File.Create(dest))
                    s.CopyTo(fs);
            }
            _tessDir = dir;
            return dir;
        }
    }
}
