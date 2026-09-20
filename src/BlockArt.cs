using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Text;

namespace RichTextGen
{
    /// <summary>色块图参数</summary>
    public class BlockArtOptions
    {
        public int Columns = 24;      // 横向块数（精度）
        public int Budget = 500;      // 字符预算（示例：游戏在 500 字符左右容易出问题）
        public bool AutoFit = true;   // 超预算时自动降低精度以满足预算
        public bool AspectFix = true; // 字符高宽比约 2:1，行数按此校正
        public bool CloseTag = false; // 每块加 </color>（更规范但每块 +8 字符）
        public char Glyph = '\u2588'; // 全块字符
    }

    /// <summary>把图片转成「彩色全块字符 + 富文本颜色」的方块画</summary>
    public static class BlockArt
    {
        public static string Render(Image img, BlockArtOptions o, out string info)
        {
            int perBlock = 16 + 1;                 // "<color=#RRGGBB>" + 1 个块字符
            if (o.CloseTag) perBlock += 8;         // + "</color>"

            int cols = Math.Max(1, o.Columns);
            int rows = RowsFor(cols, img.Width, img.Height, o.AspectFix);
            long cost = Cost(cols, rows, perBlock);
            string note = "";

            if (o.AutoFit && cost > o.Budget)
            {
                int lo = 1, hi = cols, best = 1;
                while (lo <= hi)
                {
                    int mid = (lo + hi) / 2;
                    int r = RowsFor(mid, img.Width, img.Height, o.AspectFix);
                    if (Cost(mid, r, perBlock) <= o.Budget) { best = mid; lo = mid + 1; }
                    else hi = mid - 1;
                }
                int br = RowsFor(best, img.Width, img.Height, o.AspectFix);
                note = "｜原请求 " + o.Columns + " 列会超出预算，已自动降到 " + best + " 列 × " + br + " 行";
                cols = best;
                rows = br;
            }

            Bitmap small = new Bitmap(cols, rows);
            using (Graphics g = Graphics.FromImage(small))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.DrawImage(img, new Rectangle(0, 0, cols, rows));
            }

            StringBuilder sb = new StringBuilder(cols * rows * perBlock + rows);
            for (int y = 0; y < rows; y++)
            {
                for (int x = 0; x < cols; x++)
                {
                    Color c = small.GetPixel(x, y);
                    sb.Append("<color=#")
                      .Append(c.R.ToString("X2")).Append(c.G.ToString("X2")).Append(c.B.ToString("X2"))
                      .Append(">").Append(o.Glyph);
                    if (o.CloseTag) sb.Append("</color>");
                }
                if (y < rows - 1) sb.Append('\n');
            }
            small.Dispose();

            int final = sb.Length;
            info = "输出 " + cols + " 列 × " + rows + " 行 = " + final + " 字符（预算 " + o.Budget + "）"
                 + note + (final <= o.Budget ? "｜✓ 在预算内" : "｜⚠ 仍超出预算");
            return sb.ToString();
        }

        private static long Cost(int cols, int rows, int perBlock)
        {
            return (long)cols * rows * perBlock + rows;   // 每行末尾 1 个换行
        }

        private static int RowsFor(int cols, int w, int h, bool aspect)
        {
            double r = (double)cols * h / Math.Max(1, w);
            if (aspect) r /= 2.0;                         // 字符高≈宽×2
            int rows = (int)Math.Round(r);
            return Math.Max(1, rows);
        }

        /// <summary>给 UI 用的预估（不算像素，只算块数/字符）</summary>
        public static string Estimate(int cols, int imgW, int imgH, BlockArtOptions o)
        {
            int perBlock = 16 + 1 + (o.CloseTag ? 8 : 0);
            int rows = RowsFor(Math.Max(1, cols), imgW, imgH, o.AspectFix);
            long cost = Cost(Math.Max(1, cols), rows, perBlock);
            return cols + " 列 × " + rows + " 行 ≈ " + cost + " 字符"
                 + (cost <= o.Budget ? "（在预算内）" : "（超出预算" + (o.AutoFit ? "，将自动降精度" : "") + "）");
        }
    }
}
