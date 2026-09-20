using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

// 用法: IconMaker.exe <源图> <输出.ico> <输出背景.png>
// 1) 自动裁掉背景留白（取角落像素作背景色，找内容包围盒）
// 2) 按比例缩放居中放进正方形画布（图形约占 82%）
// 3) 生成多尺寸 ico（256 用 PNG，其余用 BMP）+ 一张 1024 背景 PNG
class IconMaker
{
    static void Main(string[] args)
    {
        if (args.Length < 3) { Console.WriteLine("usage: IconMaker <src> <out.ico> <out.png> [--nocrop]"); return; }
        bool noCrop = Array.IndexOf(args, "--nocrop") >= 0;
        string src = args[0], outIco = args[1], outPng = args[2];
        int scalePct = args.Length > 3 ? int.Parse(args[3]) : 0;
        bool fullMode = (scalePct > 0);   // >0：整图（不裁切）按百分比缩放居中   // >0：整图按该百分比缩放居中（不裁切）

        using (Bitmap raw = new Bitmap(src))
        {
            Color bg = SampleBackground(raw);
            Rectangle bbox = (noCrop || fullMode) ? new Rectangle(0, 0, raw.Width, raw.Height) : ContentBox(raw, bg, 64);
            Console.WriteLine("source " + raw.Width + "x" + raw.Height + " bg=" + bg.R + "," + bg.G + "," + bg.B
                + " content=" + bbox.X + "," + bbox.Y + " " + bbox.Width + "x" + bbox.Height);

            const int side = 1024;
            using (Bitmap canvas = new Bitmap(side, side, PixelFormat.Format32bppArgb))
            using (Graphics g = Graphics.FromImage(canvas))
            {
                g.Clear(fullMode ? Color.White : bg);
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.SmoothingMode = SmoothingMode.HighQuality;

                if (noCrop)
                {
                    // 已裁切好的成品：直接铺满整个方形（不做留边，避免出现色条）
                    g.DrawImage(raw, new Rectangle(0, 0, side, side));
                    canvas.Save(outPng, ImageFormat.Png);
                    WriteIco(canvas, outIco, (scalePct > 0 && scalePct < 70) ? 0.72 : -1);
                    Console.WriteLine("wrote " + outIco + " and " + outPng + " (nocrop, fill)");
                    return;
                }
                double target = (scalePct > 0) ? side * (scalePct / 100.0) : side * 0.86;                       // 图形占画布 86%
                double s = Math.Min(target / bbox.Width, target / bbox.Height);
                int w = Math.Max(1, (int)Math.Round(bbox.Width * s));
                int h = Math.Max(1, (int)Math.Round(bbox.Height * s));
                int x = (side - w) / 2, y = (side - h) / 2;
                using (Image cut = raw.Clone(bbox, raw.PixelFormat))
                    g.DrawImage(cut, new Rectangle(x, y, w, h));

                canvas.Save(outPng, ImageFormat.Png);
                WriteIco(canvas, outIco, (scalePct > 0 && scalePct < 70) ? 0.72 : -1);
                Console.WriteLine("wrote " + outIco + " and " + outPng);
            }
        }
    }

    /// <summary>背景色：四角内缩 8% 取样取平均（避开边缘水印与中央图形）</summary>
    static Color SampleBackground(Bitmap bmp)
    {
        int ix = (int)(bmp.Width * 0.08), iy = (int)(bmp.Height * 0.08);
        Point[] pts = new Point[] {
            new Point(ix, iy), new Point(bmp.Width - 1 - ix, iy),
            new Point(ix, bmp.Height - 1 - iy), new Point(bmp.Width - 1 - ix, bmp.Height - 1 - iy)
        };
        long r = 0, g = 0, b = 0;
        foreach (Point p in pts) { Color c = bmp.GetPixel(p.X, p.Y); r += c.R; g += c.G; b += c.B; }
        return Color.FromArgb((int)(r / pts.Length), (int)(g / pts.Length), (int)(b / pts.Length));
    }

    /// <summary>用行/列直方图找图形主体（忽略边缘孤立杂点/水印）</summary>
    static Rectangle ContentBox(Bitmap bmp, Color bg, int thr)
    {
        int w = bmp.Width, h = bmp.Height;
        int[] cols = new int[w], rows = new int[h];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                Color c = bmp.GetPixel(x, y);
                int mx = Math.Max(c.R, Math.Max(c.G, c.B));
                int mn = Math.Min(c.R, Math.Min(c.G, c.B));
                int d = Math.Abs(c.R - bg.R) + Math.Abs(c.G - bg.G) + Math.Abs(c.B - bg.B);
                if ((mx - mn) > 36 || d > 90) { cols[x]++; rows[y]++; }
            }
        }
        int thrC = Math.Max(4, (int)(h * 0.004));
        int thrR = Math.Max(4, (int)(w * 0.004));
        int minX = 0, maxX = w - 1, minY = 0, maxY = h - 1;
        while (minX < w - 1 && cols[minX] < thrC) minX++;
        while (maxX > minX && cols[maxX] < thrC) maxX--;
        while (minY < h - 1 && rows[minY] < thrR) minY++;
        while (maxY > minY && rows[maxY] < thrR) maxY--;
        if (maxX <= minX || maxY <= minY) return new Rectangle(0, 0, w, h);
        int pad = Math.Max(2, (int)(Math.Max(w, h) * 0.012));
        minX = Math.Max(0, minX - pad); minY = Math.Max(0, minY - pad);
        maxX = Math.Min(w - 1, maxX + pad); maxY = Math.Min(h - 1, maxY + pad);
        return new Rectangle(minX, minY, maxX - minX + 1, maxY - minY + 1);
    }
    static void WriteIco(Bitmap src, string path, double smallPct)
    {
        int[] sizes = new int[] { 256, 128, 64, 48, 32, 16 };
        using (FileStream fs = new FileStream(path, FileMode.Create))
        using (BinaryWriter bw = new BinaryWriter(fs))
        {
            bw.Write((ushort)0); bw.Write((ushort)1); bw.Write((ushort)sizes.Length);
            int offset = 6 + 16 * sizes.Length;
            var blobs = new byte[sizes.Length][];
            double small = smallPct;   // <64px 的图标自适应放大，避免看不清
            for (int i = 0; i < sizes.Length; i++)
            {
                int s = sizes[i];
                using (Bitmap b = new Bitmap(s, s, PixelFormat.Format32bppArgb))
                {
                    using (Graphics g = Graphics.FromImage(b))
                    {
                        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                        if (small > 0 && s < 64)
                        {
                            g.Clear(src.GetPixel(2, 2));
                            int w2 = (int)(s * small), h2 = (int)(s * small);
                            using (Bitmap tmp = new Bitmap(w2, h2))
                            {
                                using (Graphics g2 = Graphics.FromImage(tmp))
                                {
                                    g2.InterpolationMode = InterpolationMode.HighQualityBicubic;
                                    g2.DrawImage(src, new Rectangle(0, 0, w2, h2));
                                }
                                g.DrawImage(tmp, new Rectangle((s - w2) / 2, (s - h2) / 2, w2, h2));
                            }
                        }
                        else g.DrawImage(src, new Rectangle(0, 0, s, s));
                    }
                    blobs[i] = (s >= 128) ? ToPng(b) : ToBmp(b);
                }
            }
            for (int i = 0; i < sizes.Length; i++)
            {
                int s = sizes[i];
                bw.Write((byte)(s >= 256 ? 0 : s));
                bw.Write((byte)(s >= 256 ? 0 : s));
                bw.Write((byte)0);            // 调色板数
                bw.Write((byte)0);            // 保留
                bw.Write((ushort)1);          // 平面
                bw.Write((ushort)32);         // 位深
                bw.Write(blobs[i].Length);
                bw.Write(offset);
                offset += blobs[i].Length;
            }
            for (int i = 0; i < sizes.Length; i++) bw.Write(blobs[i]);
        }
    }

    static byte[] ToPng(Bitmap b)
    {
        using (MemoryStream ms = new MemoryStream()) { b.Save(ms, ImageFormat.Png); return ms.ToArray(); }
    }

    static byte[] ToBmp(Bitmap b)
    {
        int w = b.Width, h = b.Height;
        using (MemoryStream ms = new MemoryStream())
        using (BinaryWriter bw = new BinaryWriter(ms))
        {
            int xorLen = w * h * 4;
            int andLen = ((w + 31) / 32) * 4 * h;
            bw.Write(40);                 // BITMAPINFOHEADER 大小
            bw.Write(w); bw.Write(h * 2); // 高度 = XOR + AND
            bw.Write((ushort)1); bw.Write((ushort)32); bw.Write(0);
            bw.Write(xorLen + andLen);
            bw.Write(0); bw.Write(0); bw.Write(0); bw.Write(0);
            for (int y = h - 1; y >= 0; y--)          // 自下而上
                for (int x = 0; x < w; x++)
                {
                    Color c = b.GetPixel(x, y);
                    bw.Write(c.B); bw.Write(c.G); bw.Write(c.R); bw.Write(c.A);
                }
            bw.Write(new byte[andLen]);               // AND 掩码全 0（用 alpha）
            bw.Flush();
            return ms.ToArray();
        }
    }
}
