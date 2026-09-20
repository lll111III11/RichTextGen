using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.Windows.Forms;

namespace RichTextGen
{
    /// <summary>HTML 颜色选择器：SV 方块 + 竖直色相条 + RGB/HSV/Hex + 三种颜色模式 + 折叠色块区</summary>
    public class ColorPickerDialog : Form
    {
        private const int SVW = 256, SVH = 256;

        private PictureBox svBox;
        private PictureBox hueBar;
        private Panel curSwatch;
        private Panel prevSwatch;
        private NumericUpDown numR, numG, numB, numH, numS, numV;
        private TextBox txtHex;
        private Label lblForms;
        private Label lblTag;
        private ComboBox cboMode;
        private Button btnMore;
        private Panel swatchPanel;
        private FlowLayoutPanel flow;
        private Button btnOk;
        private Button btnCancel;
        private Label lblStatus;
        private Label lblHueHint;

        private Bitmap svBase;      // 纯色相下的 SV 方块
        private Bitmap svShown;     // 带十字准星
        private Bitmap hueBmp;

        private double hue;         // 0-360
        private double sat;         // 0-1
        private double val;         // 0-1
        private Rgb current;
        private Rgb original;
        private bool suppress;
        private bool dragSV, dragHue;
        private bool swatchesLoaded;

        public Rgb SelectedColor { get { return current; } }
        public ColorOutputMode OutputMode
        {
            get { return (ColorOutputMode)Math.Max(0, cboMode.SelectedIndex); }
        }

        public ColorPickerDialog(Rgb initial, ColorOutputMode mode)
        {
            original = initial;
            current = initial;

            BuildUi();
            SetColor(initial, true);
            cboMode.SelectedIndex = (int)mode;
            UpdateReadouts();

            Theme.Apply(this);
            BuildSvBase();
            PaintSv();
            PaintHue();
            LayoutAll();
            curSwatch.BackColor = current.ToColor();   // 主题套用之后再确认一次色块颜色
            prevSwatch.BackColor = original.ToColor();
        }

        // ---------------------------------------------------------------- UI
        private void BuildUi()
        {
            Text = "HTML 颜色选择器";
            Font = new Font("Microsoft YaHei", 9f);
            ClientSize = new Size(552, 560);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            KeyPreview = true;

            Label lblTitle = new Label();
            lblTitle.Text = "HTML 颜色选择器";
            lblTitle.Font = new Font("Microsoft YaHei", 12f, FontStyle.Bold);
            lblTitle.Location = new Point(14, 10);
            lblTitle.AutoSize = true;
            Controls.Add(lblTitle);

            lblHueHint = new Label();
            lblHueHint.Text = "在方块内拖动 = 饱和度/明度；拖色相条 = 色调";
            lblHueHint.Location = new Point(16, 36);
            lblHueHint.AutoSize = true;
            lblHueHint.Tag = "hint";
            Controls.Add(lblHueHint);

            svBox = new PictureBox();
            svBox.Location = new Point(16, 58);
            svBox.Size = new Size(SVW, SVH);
            svBox.BorderStyle = BorderStyle.FixedSingle;
            svBox.Cursor = Cursors.Cross;
            svBox.MouseDown += sv_MouseDown;
            svBox.MouseMove += sv_MouseMove;
            svBox.MouseUp += sv_MouseUp;
            Controls.Add(svBox);

            hueBar = new PictureBox();
            hueBar.Location = new Point(280, 58);
            hueBar.Size = new Size(24, SVH);
            hueBar.BorderStyle = BorderStyle.FixedSingle;
            hueBar.Cursor = Cursors.Hand;
            hueBar.MouseDown += hue_MouseDown;
            hueBar.MouseMove += hue_MouseMove;
            hueBar.MouseUp += hue_MouseUp;
            Controls.Add(hueBar);

            curSwatch = new Panel();
            curSwatch.Location = new Point(314, 58);
            curSwatch.Size = new Size(62, 62);
            curSwatch.BorderStyle = BorderStyle.FixedSingle;
            curSwatch.Tag = "swatch";
            Controls.Add(curSwatch);

            prevSwatch = new Panel();
            prevSwatch.Location = new Point(314, 126);
            prevSwatch.Size = new Size(62, 62);
            prevSwatch.BorderStyle = BorderStyle.FixedSingle;
            prevSwatch.Tag = "swatch";
            Controls.Add(prevSwatch);

            Label lblNow = new Label();
            lblNow.Text = "当前 / 原色";
            lblNow.Location = new Point(314, 192);
            lblNow.AutoSize = true;
            lblNow.Tag = "hint";
            Controls.Add(lblNow);

            int x1 = 388, x2 = 412;
            numR = AddNum("R", x1, x2, 58, 255, 44);
            numG = AddNum("G", x1, x2, 58, 255, 74);
            numB = AddNum("B", x1, x2, 58, 255, 104);
            numH = AddNum("H", x1, x2, 58, 360, 140);
            numS = AddNum("S", x1, x2, 58, 100, 170);
            numV = AddNum("V", x1, x2, 58, 100, 200);

            Label u1 = MkLabel("°", x2 + 62, 144); Controls.Add(u1);
            Label u2 = MkLabel("%", x2 + 62, 174); Controls.Add(u2);
            Label u3 = MkLabel("%", x2 + 62, 204); Controls.Add(u3);

            Label hash = MkLabel("#", x1, 240); Controls.Add(hash);
            txtHex = new TextBox();
            txtHex.Location = new Point(x2, 236);
            txtHex.Size = new Size(104, 24);
            txtHex.CharacterCasing = CharacterCasing.Upper;
            txtHex.TextChanged += txtHex_TextChanged;
            Controls.Add(txtHex);

            lblForms = new Label();
            lblForms.Location = new Point(x1, 268);
            lblForms.Size = new Size(158, 52);
            lblForms.Tag = "hint";
            Controls.Add(lblForms);

            Label lblMode = MkLabel("颜色模式", x1, 322); Controls.Add(lblMode);
            cboMode = new ComboBox();
            cboMode.DropDownStyle = ComboBoxStyle.DropDownList;
            cboMode.Location = new Point(x1, 342);
            cboMode.Size = new Size(158, 24);
            cboMode.Items.AddRange(new object[] { "#十六进制（#FF8800）", "RGB 十进制（255,136,0）", "英文色名（orange）" });
            cboMode.SelectedIndexChanged += delegate { UpdateReadouts(); };
            Controls.Add(cboMode);

            lblTag = new Label();
            lblTag.Location = new Point(x1, 374);
            lblTag.Size = new Size(158, 34);
            lblTag.Tag = "accent";
            Controls.Add(lblTag);

            btnMore = new Button();
            btnMore.Text = "▼ 更多颜色（在线 / 内置色块）";
            btnMore.Location = new Point(16, 326);
            btnMore.Size = new Size(250, 28);
            btnMore.Click += delegate { ToggleSwatches(); };
            Controls.Add(btnMore);

            swatchPanel = new Panel();
            swatchPanel.Location = new Point(16, 360);
            swatchPanel.Size = new Size(524, 200);
            swatchPanel.Visible = false;
            Controls.Add(swatchPanel);

            flow = new FlowLayoutPanel();
            flow.Dock = DockStyle.Fill;
            flow.AutoScroll = true;
            flow.WrapContents = true;
            flow.BackColor = Color.Transparent;
            swatchPanel.Controls.Add(flow);

            lblStatus = new Label();
            lblStatus.Location = new Point(16, 356);
            lblStatus.AutoSize = true;
            lblStatus.Tag = "hint";
            Controls.Add(lblStatus);

            btnOk = new Button();
            btnOk.Text = "刷新并关闭";
            btnOk.Size = new Size(120, 32);
            btnOk.Tag = "primary";
            btnOk.Click += delegate { DialogResult = DialogResult.OK; Close(); };
            Controls.Add(btnOk);

            btnCancel = new Button();
            btnCancel.Text = "取消";
            btnCancel.Size = new Size(90, 32);
            btnCancel.Click += delegate { DialogResult = DialogResult.Cancel; Close(); };
            Controls.Add(btnCancel);

            AcceptButton = btnOk;
            KeyDown += delegate (object s, KeyEventArgs e) { if (e.KeyCode == Keys.Escape) { DialogResult = DialogResult.Cancel; Close(); } };
        }

        private NumericUpDown AddNum(string label, int lx, int nx, int w, int max, int y)
        {
            Label l = MkLabel(label, lx, y + 4);
            Controls.Add(l);
            NumericUpDown n = new NumericUpDown();
            n.Location = new Point(nx, y);
            n.Size = new Size(w, 24);
            n.Maximum = max;
            n.Minimum = 0;
            n.ValueChanged += num_ValueChanged;
            Controls.Add(n);
            return n;
        }

        private Label MkLabel(string text, int x, int y)
        {
            Label l = new Label();
            l.Text = text;
            l.Location = new Point(x, y);
            l.AutoSize = true;
            l.Tag = "hint";
            return l;
        }

        private void LayoutAll()
        {
            int y;
            if (swatchPanel.Visible)
            {
                swatchPanel.Location = new Point(16, 380);
                swatchPanel.Size = new Size(524, 200);
                lblStatus.Location = new Point(16, 588);
                y = 610;
            }
            else
            {
                y = 386;
            }
            btnOk.Location = new Point(16, y);
            btnCancel.Location = new Point(146, y);
            ClientSize = new Size(552, y + 48);
        }

        private void ToggleSwatches()
        {
            swatchPanel.Visible = !swatchPanel.Visible;
            btnMore.Text = (swatchPanel.Visible ? "▲" : "▼") + " 更多颜色（在线 / 内置色块）";
            if (swatchPanel.Visible)
            {
                swatchPanel.Location = new Point(16, 360);
                LoadSwatches();
            }
            LayoutAll();
        }

        private void LoadSwatches()
        {
            if (swatchesLoaded) { lblStatus.Text = "色块来源: " + OnlineColors.Source; return; }
            flow.Controls.Clear();
            lblStatus.Text = "正在准备色块…";
            Application.DoEvents();

            if (OnlineColors.Cached.Count == 0 && OnlineColors.IsOnline())
            {
                Cursor = Cursors.WaitCursor;
                OnlineColors.Fetch();
                Cursor = Cursors.Default;
            }
            List<NamedColor> list = OnlineColors.Current();
            foreach (NamedColor nc in list)
            {
                Button b = new Button();
                b.Size = new Size(58, 22);
                b.FlatStyle = FlatStyle.Flat;
                b.FlatAppearance.BorderColor = Theme.SwatchBorder;
                b.BackColor = nc.Color.ToColor();
                b.ForeColor = Contrast(nc.Color);
                b.Text = nc.Name.Length > 9 ? nc.Name.Substring(0, 9) : nc.Name;
                b.Font = new Font("Microsoft YaHei", 7f);
                Rgb cap = nc.Color;
                b.Click += delegate { SetColor(cap, false); };
                ToolTip tp = new ToolTip();
                tp.SetToolTip(b, nc.Name + "  " + nc.Color.HexSharp);
                flow.Controls.Add(b);
            }
            swatchesLoaded = true;
            lblStatus.Text = "色块来源: " + OnlineColors.Source + "（共 " + list.Count + " 色）";
        }

        private static Color Contrast(Rgb c)
        {
            double l = (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255.0;
            return l > 0.6 ? Color.Black : Color.White;
        }

        // ---------------------------------------------------------------- 位图
        private void BuildSvBase()
        {
            if (svBase == null) svBase = new Bitmap(SVW, SVH, PixelFormat.Format32bppArgb);
            if (svShown == null) svShown = new Bitmap(SVW, SVH, PixelFormat.Format32bppArgb);

            BitmapData bd = svBase.LockBits(new Rectangle(0, 0, SVW, SVH), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            try
            {
                int[] row = new int[SVW];
                for (int y = 0; y < SVH; y++)
                {
                    double v = 1.0 - (double)y / (SVH - 1);
                    for (int x = 0; x < SVW; x++)
                    {
                        double s = (double)x / (SVW - 1);
                        Rgb c = ColorUtil.FromHsv(hue, s, v);
                        row[x] = (255 << 24) | (c.R << 16) | (c.G << 8) | c.B;
                    }
                    System.Runtime.InteropServices.Marshal.Copy(row, 0, (IntPtr)((long)bd.Scan0 + y * bd.Stride), SVW);
                }
            }
            finally { svBase.UnlockBits(bd); }
        }

        private void PaintSv()
        {
            using (Graphics g = Graphics.FromImage(svShown))
            {
                g.DrawImageUnscaled(svBase, 0, 0);
                int mx = (int)Math.Round(sat * (SVW - 1));
                int my = (int)Math.Round((1 - val) * (SVH - 1));
                using (Pen outer = new Pen(Color.Black, 2f))
                using (Pen inner = new Pen(Color.White, 1f))
                {
                    g.DrawEllipse(outer, mx - 5, my - 5, 10, 10);
                    g.DrawEllipse(inner, mx - 4, my - 4, 8, 8);
                }
            }
            svBox.Image = svShown;
        }

        private void PaintHue()
        {
            if (hueBmp == null) hueBmp = new Bitmap(hueBar.Width - 2, hueBar.Height - 2, PixelFormat.Format32bppArgb);
            int w = hueBmp.Width, h = hueBmp.Height;
            BitmapData bd = hueBmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            try
            {
                int[] row = new int[w];
                for (int y = 0; y < h; y++)
                {
                    Rgb c = ColorUtil.FromHsv((double)y / (h - 1) * 360.0, 1, 1);
                    int px = (255 << 24) | (c.R << 16) | (c.G << 8) | c.B;
                    for (int x = 0; x < w; x++) row[x] = px;
                    System.Runtime.InteropServices.Marshal.Copy(row, 0, (IntPtr)((long)bd.Scan0 + y * bd.Stride), w);
                }
            }
            finally { hueBmp.UnlockBits(bd); }
            hueBar.Image = hueBmp;
        }

        // ---------------------------------------------------------------- 交互
        private void sv_MouseDown(object sender, MouseEventArgs e) { dragSV = true; PickSv(e.X, e.Y); }
        private void sv_MouseMove(object sender, MouseEventArgs e) { if (dragSV) PickSv(e.X, e.Y); }
        private void sv_MouseUp(object sender, MouseEventArgs e) { dragSV = false; }

        private void PickSv(int px, int py)
        {
            sat = Clamp01((double)px / (SVW - 1));
            val = 1.0 - Clamp01((double)py / (SVH - 1));
            current = ColorUtil.FromHsv(hue, sat, val);
            PushToUi();
            PaintSv();
        }

        private void hue_MouseDown(object sender, MouseEventArgs e) { dragHue = true; PickHue(e.Y); }
        private void hue_MouseMove(object sender, MouseEventArgs e) { if (dragHue) PickHue(e.Y); }
        private void hue_MouseUp(object sender, MouseEventArgs e) { dragHue = false; }

        private void PickHue(int py)
        {
            double h = Clamp01((double)py / (hueBar.Height - 3)) * 360.0;
            if (h >= 360) h = 359;
            hue = h;
            current = ColorUtil.FromHsv(hue, sat, val);
            BuildSvBase();
            PaintSv();
            PushToUi();
        }

        private void num_ValueChanged(object sender, EventArgs e)
        {
            if (suppress) return;
            suppress = true;
            try
            {
                Rgb fromRgb = new Rgb((byte)numR.Value, (byte)numG.Value, (byte)numB.Value);
                double h2, s2, v2;
                ColorUtil.ToHsv(fromRgb, out h2, out s2, out v2);
                // HSV 控件被改动时以 HSV 为准
                if (numH.Focused || numS.Focused || numV.Focused)
                {
                    hue = (double)numH.Value;
                    sat = (double)numS.Value / 100.0;
                    val = (double)numV.Value / 100.0;
                    current = ColorUtil.FromHsv(hue, sat, val);
                    BuildSvBase();
                    PaintSv();
                }
                else
                {
                    current = fromRgb;
                    if (s2 > 0) hue = h2;
                    sat = s2;
                    val = v2;
                    PaintSv();
                }
                SyncOthers();
                UpdateReadouts();
            }
            finally { suppress = false; }
        }

        private void txtHex_TextChanged(object sender, EventArgs e)
        {
            if (suppress) return;
            Rgb parsed;
            if (ColorUtil.TryParse(txtHex.Text, out parsed))
            {
                SetColor(parsed, false);
            }
        }

        private void SyncOthers()
        {
            suppress = true;
            try
            {
                numR.Value = current.R;
                numG.Value = current.G;
                numB.Value = current.B;
                double h, s, v;
                ColorUtil.ToHsv(current, out h, out s, out v);
                numH.Value = (decimal)Math.Round(h);
                numS.Value = (decimal)Math.Round(s * 100);
                numV.Value = (decimal)Math.Round(v * 100);
                txtHex.Text = current.HexSharp;
                curSwatch.BackColor = current.ToColor();
            }
            finally { suppress = false; }
        }

        private void PushToUi()
        {
            SyncOthers();
            UpdateReadouts();
        }

        private void UpdateReadouts()
        {
            string name; int d; if (!ColorUtil.HasExactName(current, out name)) name = ColorUtil.NearestName(current, out d) + "（近似）";
            lblForms.Text = "HEX " + current.HexSharp + "\r\nRGB " + current.RgbText + "\r\nNAME " + name;

            string v = (cboMode.SelectedIndex == 2) ? ColorUtil.ToTagValue(current, ColorOutputMode.Name)
                                                    : current.HexSharp;
            lblTag.Text = "将生成  <color=" + v + ">";
            prevSwatch.BackColor = original.ToColor();
        }

        private void SetColor(Rgb c, bool rebuildHue)
        {
            double h, s, v;
            ColorUtil.ToHsv(c, out h, out s, out v);
            if (s > 0 || rebuildHue) hue = h;
            sat = s;
            val = v;
            current = c;
            if (rebuildHue) BuildSvBase();
            else BuildSvBase();
            PaintSv();
            SyncOthers();
            UpdateReadouts();
        }

        private static double Clamp01(double x) { return x < 0 ? 0 : (x > 1 ? 1 : x); }
    }
}
