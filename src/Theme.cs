using System;
using System.Drawing;
using System.Windows.Forms;

namespace RichTextGen
{
    public enum ThemeMode
    {
        Light = 0,
        Dark = 1
    }

    /// <summary>亮色 / 深色两套配色，运行时切换；Tag 为 "hint" 的控件用次要文字色</summary>
    public static class Theme
    {
        /// <summary>Fluent 圆角：给控件套圆角 Region（窗口/卡片 8px，按钮/输入框 4px）</summary>
        public static void Round(Control c, int r)
        {
            try
            {
                if (c.Width <= 0 || c.Height <= 0) return;
                using (System.Drawing.Drawing2D.GraphicsPath p = new System.Drawing.Drawing2D.GraphicsPath())
                {
                    int d = r * 2;
                    p.AddArc(0, 0, d, d, 180, 90);
                    p.AddArc(c.Width - d - 1, 0, d, d, 270, 90);
                    p.AddArc(c.Width - d - 1, c.Height - d - 1, d, d, 0, 90);
                    p.AddArc(0, c.Height - d - 1, d, d, 90, 90);
                    p.CloseFigure();
                    Region old = c.Region;
                    c.Region = new Region(p);
                    if (old != null) old.Dispose();
                }
            }
            catch { }
        }

        /// <summary>Segoe UI Variable（Win11）优先，缺失回退 Segoe UI / 微软雅黑</summary>
        /// <summary>系统自带的 Win11 图标字体（Segoe Fluent Icons / 回退 MDL2 Assets）</summary>
        public static readonly string IconFont = PickIconFont();
        private static string PickIconFont()
        {
            string[] want = new string[] { "Segoe Fluent Icons", "Segoe MDL2 Assets" };
            foreach (string w in want)
            {
                try { using (Font f2 = new Font(w, 10f)) if (f2.Name == w) return w; } catch { }
            }
            return "";
        }

        /// <summary>给按钮加 Win11 图标（字体图标 + 文本）</summary>
        public static void Icon(Button b, char glyph, float size)
        {
            if (IconFont.Length == 0) return;
            try { b.Font = new Font(IconFont, size); b.Text = glyph + "  " + b.Text; } catch { }
        }

        public static readonly string UiFont = PickFont();
        private static string PickFont()
        {
            string[] want = new string[] { "Segoe UI Variable Text", "Segoe UI Variable", "Segoe UI" };
            foreach (string w in want)
            {
                try { using (Font f2 = new Font(w, 9f)) if (f2.Name == w) return w; } catch { }
            }
            return "Microsoft YaHei";
        }
        public static ThemeMode Mode = ThemeMode.Light;

        public static Color FormBack;
        public static Color PanelBack;
        public static Color Text;
        public static Color SubText;
        public static Color Border;
        public static Color InputBack;
        public static Color InputText;
        public static Color ButtonBack;
        public static Color ButtonText;
        public static Color ButtonBorder;
        public static Color Accent;
        public static Color PreviewBack;
        public static Color PreviewText;
        public static Color SwatchBorder;

        static Theme()
        {
            Set(ThemeMode.Light);
        }

        private static bool m_lightTheme = true;

        public static void Set(ThemeMode m)
        {
            Mode = m;
            m_lightTheme = (m == ThemeMode.Light);
            if (m == ThemeMode.Light)
            {
                // Win11 亮色（Fluent）
                FormBack = Color.FromArgb(243, 243, 243);      // 窗体 #F3F3F3
                PanelBack = Color.FromArgb(255, 255, 255);     // 卡片 #FFFFFF
                Text = Color.FromArgb(27, 27, 27);
                SubText = Color.FromArgb(93, 93, 93);
                Border = Color.FromArgb(229, 229, 229);        // 克制描边
                InputBack = Color.FromArgb(255, 255, 255);
                InputText = Color.FromArgb(27, 27, 27);
                ButtonBack = Color.FromArgb(251, 251, 251);
                ButtonText = Color.FromArgb(27, 27, 27);
                ButtonBorder = Color.FromArgb(214, 214, 214);
                Accent = Color.FromArgb(0, 103, 192);          // Win11 系统蓝
                PreviewBack = Color.Black;                     // 预览区：黑底
                PreviewText = Color.FromArgb(240, 240, 240);
                SwatchBorder = Color.FromArgb(200, 200, 200);
            }
            else
            {
                // Win11 深色
                FormBack = Color.FromArgb(32, 32, 32);
                PanelBack = Color.FromArgb(43, 43, 43);
                Text = Color.FromArgb(255, 255, 255);
                SubText = Color.FromArgb(197, 197, 197);
                Border = Color.FromArgb(58, 58, 58);
                InputBack = Color.FromArgb(38, 38, 38);
                InputText = Color.FromArgb(255, 255, 255);
                ButtonBack = Color.FromArgb(50, 50, 50);
                ButtonText = Color.FromArgb(255, 255, 255);
                ButtonBorder = Color.FromArgb(70, 70, 70);
                Accent = Color.FromArgb(76, 194, 255);
                PreviewBack = Color.Black;                     // 预览区：黑底
                PreviewText = Color.FromArgb(240, 240, 240);
                SwatchBorder = Color.FromArgb(90, 90, 90);
            }
        }

        public static Color SubOf(Control c)
        {
            if (c.Tag != null && c.Tag.ToString() == "hint") return SubText;
            if (c.Tag != null && c.Tag.ToString() == "accent") return Accent;
            return Text;
        }

        /// <summary>递归套用配色</summary>
        public static void Apply(Control root)
        {
            if (root == null) return;
            ApplyOne(root);
            for (int i = 0; i < root.Controls.Count; i++) Apply(root.Controls[i]);
        }

        private static void ApplyOne(Control c)
        {
            if (c is WebBrowser) return;                      // 预览用 HTML 自己上色
            if (c is PictureBox) return;                      // 色块 / 拾色器位图
            if (c is Panel && c.Tag != null && c.Tag.ToString() == "swatch") return;   // 自绘色块
            if (c is ProgressBar) return;

            if (UiBackground.Active && UiBackground.FullCover && (c is Panel || c is GroupBox || c is TabPage || c is SplitContainer)) return;   // 仅「铺满」模式才保持透明
            if (c is Form)
            {
                c.BackColor = FormBack;
                c.ForeColor = Text;
                RoundWindow((Form)c);
                return;
            }
            if (c is Panel || c is TabPage || c is SplitContainer)
            {
                c.BackColor = PanelBack;      // 比窗体略亮/略暗，制造层次（避免扁平）
                c.ForeColor = Text;
                return;
            }
            if (c is GroupBox)
            {
                c.BackColor = PanelBack;      // 卡片式分组底色
                c.ForeColor = Text;
                return;
            }
            if (c is TabControl)
            {
                c.BackColor = FormBack;
                c.ForeColor = Text;
                TabControl tc = (TabControl)c;
                tc.DrawMode = TabDrawMode.OwnerDrawFixed;
                tc.DrawItem -= TabControl_DrawItem;
                tc.DrawItem += TabControl_DrawItem;
                return;
            }
            if (c is TextBox || c is RichTextBox || c is ListBox || c is ComboBox || c is NumericUpDown)
            {
                c.BackColor = InputBack;
                c.ForeColor = InputText;
                if (c is TextBoxBase && !(c is RichTextBox)) { TextBox tb = c as TextBox; if (tb != null) tb.BorderStyle = tb.ReadOnly ? BorderStyle.FixedSingle : BorderStyle.Fixed3D; }
                return;
            }
            if (c is Button)
            {
                Button b = (Button)c;
                b.FlatStyle = FlatStyle.Flat;
                b.FlatAppearance.BorderColor = ButtonBorder;
                b.FlatAppearance.BorderSize = 1;
                b.BackColor = ButtonBack;
                b.ForeColor = ButtonText;
                if (m_lightTheme)                              // 亮色：交给 Windows 画（Win11 原生圆角，最清晰）
                {
                    b.FlatStyle = FlatStyle.Standard;
                    b.BackColor = Color.Empty;
                    b.ForeColor = Color.Empty;
                    b.UseVisualStyleBackColor = true;
                    if (b.Tag != null && b.Tag.ToString() == "primary") b.Font = new Font(b.Font, FontStyle.Bold);
                    return;
                }
                Round(b, 4);
                b.FlatAppearance.MouseOverBackColor = (b.Tag != null && b.Tag.ToString() == "primary") ? ControlPaint.Light(Accent, 0.15f) : ControlPaint.Light(ButtonBack, 0.06f);
                if (b.Tag != null && b.Tag.ToString() == "primary")
                {
                    b.BackColor = Accent;
                    b.ForeColor = Color.White;
                }
                return;
            }
            if (c is CheckBox || c is RadioButton)
            {
                c.BackColor = Color.Transparent;
                c.ForeColor = C(c.Parent);
                if (c.Tag != null && c.Tag.ToString() == "hint") c.ForeColor = SubText;
                return;
            }
            if (c is Label || c is LinkLabel)
            {
                if (c.Parent != null) c.BackColor = Color.Transparent;
                c.ForeColor = SubOf(c);
                return;
            }
            c.BackColor = FormBack;
            c.ForeColor = Text;
        }

        [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int val, int size);

        /// <summary>Win11 圆角窗口（DWM 33 = WINDOW_CORNER_PREFERENCE，2 = ROUND）</summary>
        public static void RoundWindow(Form f)
        {
            try { int v = 2; DwmSetWindowAttribute(f.Handle, 33, ref v, 4); } catch { }
        }

        /// <summary>给控件打开双缓冲（减少闪烁/卡顿）</summary>
        public static void EnableDoubleBuffer(Control c)
        {
            try
            {
                typeof(Control).GetProperty("DoubleBuffered", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).SetValue(c, true, null);
            }
            catch { }
        }

        private static Color C(Control parent)
        {
            return Text;
        }

        private static void TabControl_DrawItem(object sender, DrawItemEventArgs e)
        {
            TabControl tc = (TabControl)sender;
            TabPage page = tc.TabPages[e.Index];
            Rectangle r = e.Bounds;
            bool selected = (tc.SelectedIndex == e.Index);
            Color back = selected ? PanelBack : FormBack;
            using (SolidBrush br = new SolidBrush(back)) e.Graphics.FillRectangle(br, r);
            using (Pen pen = new Pen(Border)) e.Graphics.DrawRectangle(pen, r.Left, r.Top, r.Width - 1, r.Height - 1);
            TextRenderer.DrawText(e.Graphics, page.Text, tc.Font, r,
                selected ? Text : SubText,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
    }
}
