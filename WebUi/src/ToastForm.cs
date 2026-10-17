using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Media;
using System.Windows.Forms;

namespace RichTextGen
{
    /// <summary>右下角通知卡片（Windows 11 通知风格，不阻塞、自动消失）。</summary>
    internal class ToastForm : Form
    {
        private readonly Timer _life = new Timer();
        private readonly Action _onClick;
        private bool _closing;

        public ToastForm(string title, string subtitle, string message, Action onClick)
        {
            _onClick = onClick;

            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            AutoScaleMode = AutoScaleMode.Dpi;
            AutoScaleDimensions = new SizeF(96F, 96F);
            BackColor = Color.FromArgb(32, 32, 32);
            Size = new Size(380, 124);
            DoubleBuffered = true;

            using (GraphicsPath path = Rounded(new Rectangle(0, 0, Width, Height), 14))
                Region = new Region(path);

            Rectangle wa = Screen.PrimaryScreen.WorkingArea;
            Location = new Point(wa.Right - Width - 18, wa.Bottom - Height - 18);

            // 左侧强调条
            Panel bar = new Panel();
            bar.BackColor = Color.FromArgb(76, 194, 255);
            bar.Bounds = new Rectangle(0, 10, 4, 104);
            bar.Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Bottom;

            // 应用图标
            PictureBox icon = new PictureBox();
            icon.SizeMode = PictureBoxSizeMode.Zoom;
            icon.Bounds = new Rectangle(18, 20, 32, 32);
            try { icon.Image = Icon.ExtractAssociatedIcon(Application.ExecutablePath).ToBitmap(); } catch { }

            Label lblTitle = new Label();
            lblTitle.AutoSize = true;
            lblTitle.Text = title;
            lblTitle.Font = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Bold);
            lblTitle.ForeColor = Color.White;
            lblTitle.BackColor = Color.Transparent;
            lblTitle.Location = new Point(62, 13);

            Label lblSub = new Label();
            lblSub.AutoSize = true;
            lblSub.Text = subtitle;
            lblSub.Font = new Font("Microsoft YaHei UI", 8f);
            lblSub.ForeColor = Color.FromArgb(160, 160, 160);
            lblSub.BackColor = Color.Transparent;
            lblSub.Location = new Point(62, 34);

            Label lblMsg = new Label();
            lblMsg.Text = message;
            lblMsg.Font = new Font("Microsoft YaHei UI", 9f);
            lblMsg.ForeColor = Color.FromArgb(230, 230, 230);
            lblMsg.BackColor = Color.Transparent;
            lblMsg.Bounds = new Rectangle(62, 54, 300, 60);

            Label btnClose = new Label();
            btnClose.Text = "✕";
            btnClose.Font = new Font("Segoe UI", 9f);
            btnClose.ForeColor = Color.FromArgb(150, 150, 150);
            btnClose.BackColor = Color.Transparent;
            btnClose.TextAlign = ContentAlignment.MiddleCenter;
            btnClose.Bounds = new Rectangle(Width - 32, 6, 24, 24);
            btnClose.Cursor = Cursors.Hand;
            btnClose.Click += delegate { Dismiss(false); };

            Controls.Add(bar);
            Controls.Add(icon);
            Controls.Add(lblTitle);
            Controls.Add(lblSub);
            Controls.Add(lblMsg);
            Controls.Add(btnClose);

            EventHandler click = delegate { Dismiss(true); };
            this.Click += click;
            bar.Click += click;
            icon.Click += click;
            lblTitle.Click += click;
            lblSub.Click += click;
            lblMsg.Click += click;

            _life.Interval = 8000;
            _life.Tick += delegate { _life.Stop(); Dismiss(false); };
            _life.Start();

            try { SystemSounds.Asterisk.Play(); } catch { }
        }

        private static GraphicsPath Rounded(Rectangle r, int radius)
        {
            GraphicsPath p = new GraphicsPath();
            int d = radius * 2;
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        private void Dismiss(bool byUser)
        {
            if (_closing) return;
            _closing = true;
            _life.Stop();
            if (byUser && _onClick != null) { try { _onClick(); } catch { } }
            try { Close(); } catch { }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _life.Dispose();
            base.Dispose(disposing);
        }
    }
}
