using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Windows.Forms;

namespace RichTextGen
{
    // ==================================================================== UI 缩放
    public static class UiZoom
    {
        private class Snap
        {
            public Control C;
            public Rectangle B;
            public float FontSize;
            public string Family;
            public bool AutoSize;
        }

        private static List<Snap> _snap = new List<Snap>();
        private static Control _root;
        private static Size _rootClient;
        public static float Factor = 1f;

        public static void Capture(Control root)
        {
            _root = root;
            _snap = new List<Snap>();
            _rootClient = root.ClientSize;
            Walk(root);
            Factor = 1f;
        }

        private static void Walk(Control parent)
        {
            foreach (Control ch in parent.Controls)
            {
                if (ch is WebBrowser) { continue; }                       // 预览自己按字体缩放
                _snap.Add(new Snap
                {
                    C = ch,
                    B = ch.Bounds,
                    FontSize = ch.Font.Size,
                    Family = ch.Font.FontFamily.Name,
                    AutoSize = ch.AutoSize
                });
                Walk(ch);
            }
        }

        /// <summary>按倍率重排（相对启动时的基准布局，避免累积误差）</summary>
        public static void Apply(float f)
        {
            if (f < 0.7f) f = 0.7f;
            if (f > 2.0f) f = 2.0f;
            Factor = f;
            foreach (Snap s in _snap)
            {
                try
                {
                    float fs = Math.Max(6.5f, s.FontSize * f);
                    if (Math.Abs(s.C.Font.Size - fs) > 0.01f) s.C.Font = new Font(s.Family, fs);
                    if (s.AutoSize)
                    {
                        s.C.Location = new Point((int)Math.Round(s.B.X * f), (int)Math.Round(s.B.Y * f));
                    }
                    else
                    {
                        s.C.Bounds = new Rectangle(
                            (int)Math.Round(s.B.X * f), (int)Math.Round(s.B.Y * f),
                            (int)Math.Round(s.B.Width * f), (int)Math.Round(s.B.Height * f));
                    }
                }
                catch { }
            }
            if (_root != null && _root is Form)
            {
                Form fm = (Form)_root;
                Rectangle wa = Screen.FromControl(fm).WorkingArea;
                int w = Math.Min((int)Math.Round(_rootClient.Width * f) + (fm.Width - fm.ClientSize.Width), wa.Width - 20);
                int h = Math.Min((int)Math.Round(_rootClient.Height * f) + (fm.Height - fm.ClientSize.Height), wa.Height - 20);
                fm.ClientSize = new Size(w, h);
            }
        }
    }

    // ==================================================================== UI 配色（逐部件）
    public enum UiPart
    {
        FormBack, PanelBack, Text, SubText, Border,
        InputBack, InputText, ButtonBack, ButtonText, ButtonBorder,
        Accent, PreviewBack, PreviewText, SwatchBorder
    }

    public class UiColorsDialog : Form
    {
        private readonly Form _owner;
        private readonly Dictionary<UiPart, Panel> _sw = new Dictionary<UiPart, Panel>();
        private readonly Dictionary<UiPart, Label> _hex = new Dictionary<UiPart, Label>();

        private static readonly KeyValuePair<UiPart, string>[] Parts = new KeyValuePair<UiPart, string>[]
        {
            new KeyValuePair<UiPart,string>(UiPart.FormBack,   "窗体背景"),
            new KeyValuePair<UiPart,string>(UiPart.PanelBack,  "分组/面板背景"),
            new KeyValuePair<UiPart,string>(UiPart.Text,       "主文字"),
            new KeyValuePair<UiPart,string>(UiPart.SubText,    "次要文字（灰字）"),
            new KeyValuePair<UiPart,string>(UiPart.Border,     "边框线"),
            new KeyValuePair<UiPart,string>(UiPart.InputBack,  "输入框背景"),
            new KeyValuePair<UiPart,string>(UiPart.InputText,  "输入框文字"),
            new KeyValuePair<UiPart,string>(UiPart.ButtonBack, "按钮背景"),
            new KeyValuePair<UiPart,string>(UiPart.ButtonText, "按钮文字"),
            new KeyValuePair<UiPart,string>(UiPart.ButtonBorder,"按钮边框"),
            new KeyValuePair<UiPart,string>(UiPart.Accent,     "强调色（主按钮/链接）"),
            new KeyValuePair<UiPart,string>(UiPart.PreviewBack,"预览背景"),
            new KeyValuePair<UiPart,string>(UiPart.PreviewText,"预览文字"),
            new KeyValuePair<UiPart,string>(UiPart.SwatchBorder,"色块描边")
        };

        public UiColorsDialog(Form owner)
        {
            _owner = owner;
            Text = "UI 配色（逐部件自定义）";
            Font = new Font("Microsoft YaHei", 9f);
            ClientSize = new Size(470, 470);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;

            int y = 12;
            foreach (KeyValuePair<UiPart, string> kv in Parts)
            {
                Label l = new Label();
                l.Text = kv.Value;
                l.Location = new Point(14, y + 6);
                l.AutoSize = true;
                Controls.Add(l);

                Panel sw = new Panel();
                sw.Location = new Point(200, y + 2);
                sw.Size = new Size(48, 22);
                sw.BorderStyle = BorderStyle.FixedSingle;
                sw.Tag = "swatch";
                sw.BackColor = Current(kv.Key);
                Controls.Add(sw);
                _sw[kv.Key] = sw;

                Label hex = new Label();
                hex.Text = Hex(Current(kv.Key));
                hex.Location = new Point(258, y + 6);
                hex.AutoSize = true;
                hex.Tag = "hint";
                Controls.Add(hex);
                _hex[kv.Key] = hex;

                UiPart part = kv.Key;
                Button b = new Button();
                b.Text = "改色…";
                b.Size = new Size(90, 26);
                b.Location = new Point(340, y);
                b.Click += delegate { Pick(part); };
                Controls.Add(b);

                y += 30;
            }

            Button reset = new Button();
            reset.Text = "恢复默认（跟随亮/深主题）";
            reset.Size = new Size(200, 30);
            reset.Location = new Point(14, y + 6);
            reset.Click += delegate
            {
                Theme.Set(Theme.Mode);
                Theme.Apply(_owner);
                RefreshSwatches();
            };
            Controls.Add(reset);

            Button ok = new Button();
            ok.Text = "刷新并关闭";
            ok.Size = new Size(110, 30);
            ok.Location = new Point(230, y + 6);
            ok.Tag = "primary";
            ok.Click += delegate { DialogResult = DialogResult.OK; Close(); };
            Controls.Add(ok);

            ClientSize = new Size(470, y + 48);
            Theme.Apply(this);
        }

        private static Color Current(UiPart p)
        {
            switch (p)
            {
                case UiPart.FormBack: return Theme.FormBack;
                case UiPart.PanelBack: return Theme.PanelBack;
                case UiPart.Text: return Theme.Text;
                case UiPart.SubText: return Theme.SubText;
                case UiPart.Border: return Theme.Border;
                case UiPart.InputBack: return Theme.InputBack;
                case UiPart.InputText: return Theme.InputText;
                case UiPart.ButtonBack: return Theme.ButtonBack;
                case UiPart.ButtonText: return Theme.ButtonText;
                case UiPart.ButtonBorder: return Theme.ButtonBorder;
                case UiPart.Accent: return Theme.Accent;
                case UiPart.PreviewBack: return Theme.PreviewBack;
                case UiPart.PreviewText: return Theme.PreviewText;
                default: return Theme.SwatchBorder;
            }
        }

        private static void Set(UiPart p, Color c)
        {
            switch (p)
            {
                case UiPart.FormBack: Theme.FormBack = c; break;
                case UiPart.PanelBack: Theme.PanelBack = c; break;
                case UiPart.Text: Theme.Text = c; break;
                case UiPart.SubText: Theme.SubText = c; break;
                case UiPart.Border: Theme.Border = c; break;
                case UiPart.InputBack: Theme.InputBack = c; break;
                case UiPart.InputText: Theme.InputText = c; break;
                case UiPart.ButtonBack: Theme.ButtonBack = c; break;
                case UiPart.ButtonText: Theme.ButtonText = c; break;
                case UiPart.ButtonBorder: Theme.ButtonBorder = c; break;
                case UiPart.Accent: Theme.Accent = c; break;
                case UiPart.PreviewBack: Theme.PreviewBack = c; break;
                case UiPart.PreviewText: Theme.PreviewText = c; break;
                default: Theme.SwatchBorder = c; break;
            }
        }

        private static string Hex(Color c)
        {
            return string.Format("#{0:X2}{1:X2}{2:X2}", c.R, c.G, c.B);
        }

        private void Pick(UiPart p)
        {
            Color cur = Current(p);
            ColorPickerDialog dlg = new ColorPickerDialog(new Rgb(cur.R, cur.G, cur.B), ColorOutputMode.Hex);
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            Set(p, dlg.SelectedColor.ToColor());
            Theme.Apply(_owner);                       // 立即生效
            if (_owner is MainForm) ((MainForm)_owner).AfterThemeChanged();
            Theme.Apply(this);
            RefreshSwatches();
        }

        private void RefreshSwatches()
        {
            foreach (KeyValuePair<UiPart, Panel> kv in _sw)
            {
                kv.Value.BackColor = Current(kv.Key);
                _hex[kv.Key].Text = Hex(Current(kv.Key));
            }
        }
    }

    // ==================================================================== 背景图（拖入 + 对称 + 自适配）
    public static class UiBackground
    {
        public const long MaxBytes = 8L * 1024 * 1024;     // 8MB
        public const int MaxSide = 4096;                    // 单边上限
        public static Bitmap Source;                        // 原图
        public static bool Mirror = true;                   // 对称（镜像平铺）
        public static bool Fit = false;                     // 自适配（拉伸覆盖）
        public static Color Fallback = Color.Empty;         // 背景色（图片不可用时）

        /// <summary>校验：过大/过宽/非图片 → 返回原因，否则 null</summary>
        public static string Validate(string path)
        {
            try
            {
                FileInfo fi = new FileInfo(path);
                if (!fi.Exists) return "文件不存在";
                if (fi.Length > MaxBytes) return "图片超过 " + (MaxBytes / 1024 / 1024) + " MB（" + (fi.Length / 1024 / 1024) + " MB）";
                using (Image img = Image.FromFile(path))
                {
                    if (img.Width > MaxSide || img.Height > MaxSide)
                        return "尺寸超过 " + MaxSide + "px（当前 " + img.Width + "×" + img.Height + "）";
                }
                return null;
            }
            catch (Exception ex) { return "无法读取为图片：" + ex.Message; }
        }

        public static bool Load(string path, out string error)
        {
            error = Validate(path);
            if (error != null) return false;
            try
            {
                using (Image img = Image.FromFile(path))
                    Source = new Bitmap(img);
                return true;
            }
            catch (Exception ex) { error = ex.Message; return false; }
        }

        public static bool Active = false;
        public static bool FullCover = false;   // true=容器透明图透出来(旧行为)；false=仅窗体铺图，保留面板层次      // 背景是否生效（生效时主题不再刷容器底色）

        /// <summary>整窗一张图：按窗口大小铺满（自适配=拉伸；对称=镜像平铺；否则直接平铺）</summary>
        public static Bitmap Build(Size targetSize)
        {
            if (Source == null) return null;
            int w = Math.Max(64, targetSize.Width), h = Math.Max(64, targetSize.Height);
            Bitmap bmp = new Bitmap(w, h);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                if (Fit)
                {
                    g.DrawImage(Source, new Rectangle(0, 0, w, h));
                }
                else
                {
                    Bitmap tile = Mirror ? MirrorTile() : new Bitmap(Source);
                    for (int y = 0; y < h; y += tile.Height)
                        for (int x = 0; x < w; x += tile.Width)
                            g.DrawImageUnscaled(tile, x, y);
                    tile.Dispose();
                }
            }
            return bmp;
        }

        private static Bitmap MirrorTile()
        {
            Bitmap bmp = new Bitmap(Source.Width * 2, Source.Height * 2);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.DrawImage(Source, new Rectangle(0, 0, Source.Width, Source.Height));
                g.DrawImage(Source, new Rectangle(Source.Width, 0, Source.Width, Source.Height));
            }
            bmp.RotateFlip(RotateFlipType.RotateNoneFlipY);
            return bmp;
        }

        public static void Clear()
        {
            if (Source != null) { Source.Dispose(); Source = null; }
            Active = false;
        }

        /// <summary>
        /// 一张图覆盖整个界面：窗体铺满同一张图，TabPage 用同一张图的对应裁片，
        /// 其余容器统一透明 → 看起来是"一整张图"，而不是每个面板各贴一张。
        /// 预览区(WebBrowser)与输出区(只读文本框)保持自身颜色，不被覆盖。
        /// </summary>
        public static Bitmap PaintBack;      // 窗体 OnPaintBackground 直接画它（客户区大小）
        public static Form TargetForm;

        /// <summary>子容器统一按偏移绘制同一张图 → 整窗连续，且不会每个框各自从原点重画</summary>
        private static void SurfacePaint(object sender, PaintEventArgs e)
        {
            if (!Active || PaintBack == null || TargetForm == null) return;
            try
            {
                Control c = (Control)sender;
                Point p = TargetForm.PointToClient(c.PointToScreen(Point.Empty));
                e.Graphics.DrawImageUnscaled(PaintBack, -p.X, -p.Y);
            }
            catch { }
        }

        public static void ApplyTo(Form target)
        {
            if (target == null) return;
            target.SuspendLayout();
            try
            {
                if (PaintBack != null) { PaintBack.Dispose(); PaintBack = null; }
                if (Source != null) PaintBack = Build(target.ClientSize);
                Active = (Source != null);
                target.BackgroundImage = null;                 // 统一交给 OnPaintBackground
                if (Fallback != Color.Empty) target.BackColor = Fallback;

                foreach (Control c in All(target))
                {
                    try
                    {
                        if (c is TabPage)
                        {
                            TabPage tp = (TabPage)c;
                            tp.BackgroundImage = null;
                            tp.BackColor = (Fallback == Color.Empty) ? Theme.FormBack : Fallback;
                            tp.Paint -= SurfacePaint;
                            if (PaintBack != null) tp.Paint += SurfacePaint;
                            tp.Invalidate();
                            continue;
                        }
                        if (c is SplitContainer)
                        {
                            SplitContainer sc = (SplitContainer)c;
                            sc.Panel1.BackColor = Color.Transparent;
                            sc.Panel2.BackColor = Color.Transparent;
                            continue;
                        }
                        if (c is Panel)
                        {
                            Panel pnl = (Panel)c;
                            pnl.BackColor = (Fallback == Color.Empty) ? Theme.FormBack : Fallback;
                            pnl.Paint -= SurfacePaint;
                            if (PaintBack != null) pnl.Paint += SurfacePaint;
                            pnl.Invalidate();
                        }
                    }
                    catch { }
                }
            }
            finally { target.ResumeLayout(true); }
            target.Refresh();
        }
        private static IEnumerable<Control> All(Control parent)
        {
            foreach (Control c in parent.Controls)
            {
                yield return c;
                foreach (Control d in All(c)) yield return d;
            }
        }

        private static Point OffsetIn(Form target, Control c)
        {
            try { return target.PointToClient(c.PointToScreen(Point.Empty)); }
            catch { return new Point(0, 0); }
        }

        private static Bitmap Crop(Bitmap src, Rectangle r)
        {
            int x = Math.Max(0, Math.Min(src.Width - 1, r.X));
            int y = Math.Max(0, Math.Min(src.Height - 1, r.Y));
            int w = Math.Max(1, Math.Min(src.Width - x, r.Width));
            int h = Math.Max(1, Math.Min(src.Height - y, r.Height));
            Bitmap bmp = new Bitmap(w, h);
            using (Graphics g = Graphics.FromImage(bmp))
            using (Image cut = src.Clone(new Rectangle(x, y, w, h), src.PixelFormat))
                g.DrawImageUnscaled(cut, 0, 0);
            return bmp;
        }
    }

    public class BackgroundDialog : Form
    {
        private readonly Form _target;
        private readonly Panel _drop;
        private readonly Label _info;
        private readonly CheckBox _mirror;
        private readonly CheckBox _fit;
        private readonly Panel _colorSw;

        public BackgroundDialog(Form target)
        {
            _target = target;
            Text = "背景设置（拖入图片 / 对称 / 自适配 / 背景色）";
            Font = new Font("Microsoft YaHei", 9f);
            ClientSize = new Size(520, 330);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeMinimizeFalse();
            StartPosition = FormStartPosition.CenterParent;

            _drop = new Panel();
            _drop.Location = new Point(14, 14);
            _drop.Size = new Size(492, 150);
            _drop.BorderStyle = BorderStyle.FixedSingle;
            _drop.AllowDrop = true;
            _drop.BackColor = Color.FromArgb(238, 238, 238);
            _drop.Paint += DropPaint;
            _drop.DragEnter += delegate (object s, DragEventArgs e)
            {
                if (HasImage(e)) { e.Effect = DragDropEffects.Copy; Info("松手即可放入（将做尺寸校验）", false); }
                else { e.Effect = DragDropEffects.None; Info("只能拖入图片文件", true); }
            };
            _drop.DragLeave += delegate { Relayout(); };
            _drop.DragDrop += delegate (object s, DragEventArgs e)
            {
                string[] files = e.Data.GetData(DataFormats.FileDrop) as string[];
                if (files == null || files.Length == 0) return;
                string err;
                if (!UiBackground.Load(files[0], out err)) { Info("拖入失败：" + err, true); Relayout(); return; }
                Info("已载入：" + Path.GetFileName(files[0]) + "（" + UiBackground.Source.Width + "×" + UiBackground.Source.Height + "）", false);
                UiBackground.ApplyTo(_target);
                Relayout();
            };
            Controls.Add(_drop);

            _info = new Label();
            _info.Location = new Point(14, 172);
            _info.Size = new Size(492, 38);
            _info.Tag = "hint";
            Controls.Add(_info);

            _mirror = new CheckBox();
            _mirror.Text = "对称显示（镜像平铺，四周对称）";
            _mirror.Location = new Point(14, 214);
            _mirror.AutoSize = true;
            _mirror.Checked = UiBackground.Mirror;
            _mirror.CheckedChanged += delegate { UiBackground.Mirror = _mirror.Checked; UiBackground.ApplyTo(_target); };
            Controls.Add(_mirror);

            CheckBox full = new CheckBox();
            full.Text = "铺满整窗（容器透明，图片透上来；不勾 = 保留面板层次）";
            full.Location = new Point(14, 244);
            full.AutoSize = true;
            full.Checked = UiBackground.FullCover;
            full.CheckedChanged += delegate { UiBackground.FullCover = full.Checked; UiBackground.ApplyTo(_target); };
            Controls.Add(full);

            _fit = new CheckBox();
            _fit.Text = "自适配（按窗口比例拉伸铺满）";
            _fit.Location = new Point(280, 214);
            _fit.AutoSize = true;
            _fit.Checked = UiBackground.Fit;
            _fit.CheckedChanged += delegate { UiBackground.Fit = _fit.Checked; UiBackground.ApplyTo(_target); };
            Controls.Add(_fit);

            Label lc = new Label();
            lc.Text = "背景色（图片不可用或想纯色时用）";
            lc.Location = new Point(14, 248);
            lc.AutoSize = true;
            lc.Tag = "hint";
            Controls.Add(lc);

            _colorSw = new Panel();
            _colorSw.Location = new Point(240, 244);
            _colorSw.Size = new Size(52, 24);
            _colorSw.BorderStyle = BorderStyle.FixedSingle;
            _colorSw.Tag = "swatch";
            _colorSw.BackColor = (UiBackground.Fallback == Color.Empty) ? Theme.FormBack : UiBackground.Fallback;
            Controls.Add(_colorSw);

            Button bcol = new Button();
            bcol.Text = "选背景色…";
            bcol.Size = new Size(110, 26);
            bcol.Location = new Point(300, 242);
            bcol.Click += delegate
            {
                ColorPickerDialog dlg = new ColorPickerDialog(new Rgb(_colorSw.BackColor.R, _colorSw.BackColor.G, _colorSw.BackColor.B), ColorOutputMode.Hex);
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                UiBackground.Fallback = dlg.SelectedColor.ToColor();
                _colorSw.BackColor = UiBackground.Fallback;
                UiBackground.ApplyTo(_target);
            };
            Controls.Add(bcol);

            Button bBuiltin = new Button();
            bBuiltin.Text = "使用内置图片";
            bBuiltin.Size = new Size(110, 26);
            bBuiltin.Location = new Point(132, 282);
            bBuiltin.Click += delegate
            {
                string p = MainForm.EnsureEmbeddedBackground();
                if (p.Length == 0) { Info("exe 里没有内置图片资源", true); return; }
                string err;
                if (!UiBackground.Load(p, out err)) { Info("加载内置图片失败：" + err, true); return; }
                Info("已使用内置图片（" + UiBackground.Source.Width + "×" + UiBackground.Source.Height + "）", false);
                UiBackground.ApplyTo(_target);
                Relayout();
            };
            Controls.Add(bBuiltin);

            Button bclear = new Button();
            bclear.Text = "清除背景图";
            bclear.Size = new Size(110, 26);
            bclear.Location = new Point(14, 282);
            bclear.Click += delegate
            {
                UiBackground.Clear();
                UiBackground.ApplyTo(_target);
                Info("已清除背景图", false);
                Relayout();
            };
            Controls.Add(bclear);

            Button bBuilt = new Button();
            bBuilt.Text = "使用内置图片";
            bBuilt.Size = new Size(110, 26);
            bBuilt.Location = new Point(134, 282);
            bBuilt.Click += delegate
            {
                string p = MainForm.BuiltInBgPath;
                if (!File.Exists(p)) { Info("找不到内置图片（首次运行会自动释放到 %APPDATA%\\\\RichTextGen）", true); return; }
                string err;
                if (!UiBackground.Load(p, out err)) { Info("内置图片不可用：" + err, true); return; }
                UiBackground.ApplyTo(_target);
                Info("已使用内置图片：" + p, false);
                Relayout();
            };
            Controls.Add(bBuilt);

            Button bok = new Button();
            bok.Text = "刷新并关闭";
            bok.Size = new Size(110, 28);
            bok.Location = new Point(396, 282);
            bok.Tag = "primary";
            bok.Click += delegate { DialogResult = DialogResult.OK; Close(); };
            Controls.Add(bok);

            SetStyle(ControlStyles.ResizeRedraw, true);
            Theme.Apply(this);
            Relayout();
        }

        private void MaximizeMinimizeFalse()
        {
            MaximizeBox = false;
            MinimizeBox = false;
        }

        private static bool HasImage(DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return false;
            string[] files = e.Data.GetData(DataFormats.FileDrop) as string[];
            if (files == null || files.Length == 0) return false;
            string ext = Path.GetExtension(files[0]).ToLowerInvariant();
            return ext == ".png" || ext == ".jpg" || ext == ".jpeg" || ext == ".bmp" || ext == ".gif";
        }

        private void Info(string text, bool warn)
        {
            _info.Text = text;
            _info.ForeColor = warn ? Color.FromArgb(200, 60, 60) : Theme.SubText;
        }

        private void DropPaint(object sender, PaintEventArgs e)
        {
            string txt = (UiBackground.Source == null)
                ? "把图片拖到这里（≤ 8MB，单边 ≤ 4096px）\n超出规格将无法拖入"
                : "已载入图片：" + UiBackground.Source.Width + "×" + UiBackground.Source.Height + "（可再次拖入替换）";
            TextRenderer.DrawText(e.Graphics, txt, Font, _drop.ClientRectangle,
                UiBackground.Source == null ? Color.FromArgb(120, 120, 120) : Color.FromArgb(40, 110, 60),
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
        }

        private void Relayout()
        {
            _drop.Invalidate();
        }
    }
}
