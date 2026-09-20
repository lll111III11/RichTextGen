using System;
using System.Drawing;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Windows.Forms;

namespace RichTextGen
{
    /// <summary>
    /// 图片系统（两个模式）：
    ///   ① URL 模式：拖图 → 本地/局域网/公网地址（自动取 LAN IPv4 / 全局 IPv6，也可手填公网 IP 或域名）
    ///   ② 色块图模式：拖图 → 用「全块字符 █ + 富文本颜色」把图片画成色块，可调精度与字符预算（超预算自动降精度）
    /// </summary>
    public class ImageSystemDialog : Form
    {
        private readonly MainForm _main;
        private readonly LocalHttpServer _server = new LocalHttpServer();

        // URL 模式
        private Panel _dropUrl;
        private ComboBox _cboHostMode;
        private TextBox _txtHost;
        private NumericUpDown _numPort;
        private TextBox _txtUrl;
        private Label _lblUrlInfo;
        private string _lastFile = "";

        // 色块图模式
        private Panel _dropArt;
        private Bitmap _artImg;
        private NumericUpDown _numCols;
        private NumericUpDown _numBudget;
        private CheckBox _chkAutoFit;
        private CheckBox _chkAspect;
        private CheckBox _chkClose;
        private ComboBox _cboGlyph;
        private TextBox _txtArt;
        private Label _lblEst;

        public ImageSystemDialog(MainForm main)
        {
            _main = main;
            Text = "图片系统（URL 模式 / 色块图模式）";
            Font = new Font("Microsoft YaHei", 9f);
            ClientSize = new Size(860, 620);
            MinimumSize = new Size(760, 560);
            StartPosition = FormStartPosition.CenterParent;

            TabControl tabs = new TabControl();
            tabs.Dock = DockStyle.Fill;
            tabs.Padding = new Point(14, 5);
            Controls.Add(tabs);

            TabPage tUrl = new TabPage("① URL 模式");
            TabPage tArt = new TabPage("② 色块图模式（不用 URL）");
            tabs.TabPages.Add(tUrl);
            tabs.TabPages.Add(tArt);
            BuildUrlTab(tUrl);
            BuildArtTab(tArt);

            Theme.Apply(this);
        }

        // ============================================================ URL 模式
        private void BuildUrlTab(TabPage page)
        {
            _dropUrl = new Panel();
            _dropUrl.Location = new Point(14, 14);
            _dropUrl.Size = new Size(320, 150);
            _dropUrl.BorderStyle = BorderStyle.FixedSingle;
            _dropUrl.AllowDrop = true;
            _dropUrl.Tag = "swatch";
            _dropUrl.BackColor = Color.FromArgb(238, 238, 238);
            _dropUrl.Paint += delegate (object s, PaintEventArgs e)
            {
                TextRenderer.DrawText(e.Graphics,
                    _lastFile.Length == 0 ? "把图片拖到这里\n（自动复制并生成 URL）" : "已载入：" + Path.GetFileName(_lastFile) + "\n可再拖入替换",
                    Font, _dropUrl.ClientRectangle, Color.FromArgb(90, 90, 90),
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
            };
            _dropUrl.DragEnter += delegate (object s, DragEventArgs e)
            {
                e.Effect = (e.Data.GetDataPresent(DataFormats.FileDrop)) ? DragDropEffects.Copy : DragDropEffects.None;
            };
            _dropUrl.DragDrop += delegate (object s, DragEventArgs e)
            {
                string[] f = e.Data.GetData(DataFormats.FileDrop) as string[];
                if (f != null && f.Length > 0) LoadUrlImage(f[0]);
            };
            page.Controls.Add(_dropUrl);

            Label l1 = MkLabel("地址来源", 350, 20);
            page.Controls.Add(l1);

            _cboHostMode = new ComboBox();
            _cboHostMode.DropDownStyle = ComboBoxStyle.DropDownList;
            _cboHostMode.Location = new Point(420, 16);
            _cboHostMode.Size = new Size(220, 24);
            _cboHostMode.Items.AddRange(new object[] { "自动 · 局域网 IPv4", "自动 · 全局 IPv6", "手动填写（公网 IP / 域名）" });
            _cboHostMode.SelectedIndex = 0;
            _cboHostMode.SelectedIndexChanged += delegate { RebuildUrl(); };
            page.Controls.Add(_cboHostMode);

            Label l2 = MkLabel("手动地址", 350, 54);
            page.Controls.Add(l2);
            _txtHost = new TextBox();
            _txtHost.Location = new Point(420, 50);
            _txtHost.Size = new Size(220, 24);
            _txtHost.TextChanged += delegate { RebuildUrl(); };
            page.Controls.Add(_txtHost);

            Label l3 = MkLabel("端口", 350, 88);
            page.Controls.Add(l3);
            _numPort = new NumericUpDown();
            _numPort.Location = new Point(420, 84);
            _numPort.Size = new Size(80, 24);
            _numPort.Minimum = 1024;
            _numPort.Maximum = 65530;
            _numPort.Value = 8799;
            _numPort.ValueChanged += delegate { RebuildUrl(); };
            page.Controls.Add(_numPort);

            Button bStart = new Button();
            bStart.Text = "启动/重载服务";
            bStart.Size = new Size(120, 28);
            bStart.Location = new Point(512, 82);
            bStart.Click += delegate
            {
                _server.Stop();
                string dir = Path.Combine(Path.GetTempPath(), "RichTextGenImage");
                bool ok = _server.Start(dir, (int)_numPort.Value);
                _lblUrlInfo.Text = ok ? ("服务已启动，目录：" + dir) : "服务启动失败（端口被占用？换一个端口试试）";
                if (_lastFile.Length > 0) LoadUrlImage(_lastFile);
            };
            page.Controls.Add(bStart);

            Button bStop = new Button();
            bStop.Text = "停止服务";
            bStop.Size = new Size(90, 28);
            bStop.Location = new Point(640, 82);
            bStop.Click += delegate { _server.Stop(); _lblUrlInfo.Text = "服务已停止"; };
            page.Controls.Add(bStop);

            Label l4 = MkLabel("生成 URL", 14, 180);
            page.Controls.Add(l4);
            _txtUrl = new TextBox();
            _txtUrl.Location = new Point(84, 176);
            _txtUrl.Size = new Size(646, 26);
            _txtUrl.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            page.Controls.Add(_txtUrl);

            Button bCopy = new Button();
            bCopy.Text = "复制 URL";
            bCopy.Size = new Size(100, 28);
            bCopy.Location = new Point(740, 175);
            bCopy.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            bCopy.Click += delegate
            {
                if (_txtUrl.Text.Length == 0) return;
                try { Clipboard.SetText(_txtUrl.Text); } catch { }
                _lblUrlInfo.Text = "已复制 URL";
            };
            page.Controls.Add(bCopy);

            Button bOpen = new Button();
            bOpen.Text = "浏览器打开";
            bOpen.Size = new Size(100, 28);
            bOpen.Location = new Point(740, 209);
            bOpen.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            bOpen.Click += delegate
            {
                if (_txtUrl.Text.Length == 0) return;
                try { System.Diagnostics.Process.Start(_txtUrl.Text); } catch { }
            };
            page.Controls.Add(bOpen);

            _lblUrlInfo = new Label();
            _lblUrlInfo.Location = new Point(14, 212);
            _lblUrlInfo.Size = new Size(716, 60);
            _lblUrlInfo.Tag = "hint";
            _lblUrlInfo.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            page.Controls.Add(_lblUrlInfo);
            _lblUrlInfo.Text = "拖入图片后自动生成 URL；同一网络的设备/客户端可直接访问。\n能否在游戏内显示，取决于所用插件是否支持按 URL 取图。";
        }

        private void LoadUrlImage(string file)
        {
            string err = UiBackground.Validate(file);
            string ext = Path.GetExtension(file).ToLowerInvariant();
            bool isImg = ext == ".png" || ext == ".jpg" || ext == ".jpeg" || ext == ".bmp" || ext == ".gif" || ext == ".webp";
            if (!isImg) { _lblUrlInfo.Text = "只能拖入图片文件（png/jpg/bmp/gif/webp）"; return; }
            if (err != null) { _lblUrlInfo.Text = "无法使用：" + err; return; }

            _lastFile = file;
            _dropUrl.Invalidate();
            string dir = Path.Combine(Path.GetTempPath(), "RichTextGenImage");
            if (!_server.Running)
            {
                bool ok = false;
                for (int p = (int)_numPort.Value; p < (int)_numPort.Value + 20 && !ok; p++) ok = _server.Start(dir, p);
                if (!ok) { _lblUrlInfo.Text = "本地服务启动失败（端口占用）"; return; }
            }
            try { File.Copy(file, Path.Combine(dir, Path.GetFileName(file)), true); } catch { }
            RebuildUrl();
        }

        private void RebuildUrl()
        {
            if (_txtUrl == null) return;
            string host;
            switch (_cboHostMode.SelectedIndex)
            {
                case 1: host = "[" + GlobalV6() + "]"; break;
                case 2: host = _txtHost.Text.Trim(); break;
                default: host = LocalHttpServer.LanIp(); break;
            }
            if (host.Length == 0) { _txtUrl.Text = "（未取到地址，请手动填写）"; return; }
            int port = _server.Running ? _server.Port : (int)_numPort.Value;
            string name = _lastFile.Length > 0 ? Path.GetFileName(_lastFile) : "图.png";
            _txtUrl.Text = "http://" + host + ":" + port + "/" + name;
        }

        private static string GlobalV6()
        {
            try
            {
                foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;
                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback || ni.NetworkInterfaceType == NetworkInterfaceType.Tunnel) continue;
                    foreach (UnicastIPAddressInformation ua in ni.GetIPProperties().UnicastAddresses)
                    {
                        if (ua.Address.AddressFamily != AddressFamily.InterNetworkV6) continue;
                        if (ua.Address.IsIPv6LinkLocal || IPAddress.IsLoopback(ua.Address)) continue;
                        return ua.Address.ToString();
                    }
                }
            }
            catch { }
            return "::1";
        }

        // ============================================================ 色块图模式
        private void BuildArtTab(TabPage page)
        {
            _dropArt = new Panel();
            _dropArt.Location = new Point(14, 14);
            _dropArt.Size = new Size(300, 170);
            _dropArt.BorderStyle = BorderStyle.FixedSingle;
            _dropArt.AllowDrop = true;
            _dropArt.Tag = "swatch";
            _dropArt.BackColor = Color.FromArgb(238, 238, 238);
            _dropArt.Paint += delegate (object s, PaintEventArgs e)
            {
                if (_artImg != null)
                {
                    Rectangle r = _dropArt.ClientRectangle;
                    r.Inflate(-6, -6);
                    e.Graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                    e.Graphics.DrawImage(_artImg, FitRect(_artImg.Width, _artImg.Height, r));
                }
                else
                {
                    TextRenderer.DrawText(e.Graphics, "把图片拖到这里\n（转成彩色方块画）", Font, _dropArt.ClientRectangle,
                        Color.FromArgb(90, 90, 90), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
                }
            };
            _dropArt.DragEnter += delegate (object s, DragEventArgs e)
            {
                e.Effect = (e.Data.GetDataPresent(DataFormats.FileDrop)) ? DragDropEffects.Copy : DragDropEffects.None;
            };
            _dropArt.DragDrop += delegate (object s, DragEventArgs e)
            {
                string[] f = e.Data.GetData(DataFormats.FileDrop) as string[];
                if (f == null || f.Length == 0) return;
                string err;
                if (!UiBackground.Load(f[0], out err)) { _lblEst.Text = "载入失败：" + err; return; }
                if (_artImg != null) _artImg.Dispose();
                _artImg = new Bitmap(UiBackground.Source);   // 副本，避免共享引用
                UiBackground.Clear();
                _dropArt.Invalidate();
                UpdateEstimate();
            };
            page.Controls.Add(_dropArt);

            Label l1 = MkLabel("横向块数（精度）", 330, 20);
            page.Controls.Add(l1);
            _numCols = new NumericUpDown();
            _numCols.Location = new Point(470, 16);
            _numCols.Size = new Size(80, 24);
            _numCols.Minimum = 2;
            _numCols.Maximum = 200;
            _numCols.Value = 24;
            _numCols.ValueChanged += delegate { UpdateEstimate(); };
            page.Controls.Add(_numCols);

            Label l2 = MkLabel("字符预算（上限）", 330, 54);
            page.Controls.Add(l2);
            _numBudget = new NumericUpDown();
            _numBudget.Location = new Point(470, 50);
            _numBudget.Size = new Size(80, 24);
            _numBudget.Minimum = 60;
            _numBudget.Maximum = 20000;
            _numBudget.Value = 500;
            _numBudget.ValueChanged += delegate { UpdateEstimate(); };
            page.Controls.Add(_numBudget);

            _chkAutoFit = new CheckBox();
            _chkAutoFit.Text = "超出预算时自动降低精度（保证完整显示）";
            _chkAutoFit.Location = new Point(566, 18);
            _chkAutoFit.AutoSize = true;
            _chkAutoFit.Checked = true;
            _chkAutoFit.CheckedChanged += delegate { UpdateEstimate(); };
            page.Controls.Add(_chkAutoFit);

            _chkAspect = new CheckBox();
            _chkAspect.Text = "宽高比校正（字符高≈宽×2）";
            _chkAspect.Location = new Point(566, 46);
            _chkAspect.AutoSize = true;
            _chkAspect.Checked = true;
            _chkAspect.CheckedChanged += delegate { UpdateEstimate(); };
            page.Controls.Add(_chkAspect);

            _chkClose = new CheckBox();
            _chkClose.Text = "每块加 </color>（更规范，但每块多 8 字符）";
            _chkClose.Location = new Point(330, 84);
            _chkClose.AutoSize = true;
            _chkClose.CheckedChanged += delegate { UpdateEstimate(); };
            page.Controls.Add(_chkClose);

            Label l3 = MkLabel("块字符", 330, 116);
            page.Controls.Add(l3);
            _cboGlyph = new ComboBox();
            _cboGlyph.DropDownStyle = ComboBoxStyle.DropDownList;
            _cboGlyph.Location = new Point(400, 112);
            _cboGlyph.Size = new Size(220, 24);
            _cboGlyph.Items.AddRange(new object[] { "█ 全块 U+2588（推荐）", "⬛ 大黑方块 U+2B1B", "■ 黑方块 U+25A0", "● 圆点 U+25CF" });
            _cboGlyph.SelectedIndex = 0;
            page.Controls.Add(_cboGlyph);

            _lblEst = new Label();
            _lblEst.Location = new Point(14, 194);
            _lblEst.Size = new Size(820, 44);
            _lblEst.Tag = "hint";
            _lblEst.Text = "拖入图片后这里显示预估（列×行 / 预计字符数 / 是否超预算）";
            page.Controls.Add(_lblEst);

            Button bGen = new Button();
            bGen.Text = "生成色块图";
            bGen.Size = new Size(130, 32);
            bGen.Location = new Point(14, 240);
            bGen.Tag = "primary";
            bGen.Click += delegate { GenerateArt(); };
            page.Controls.Add(bGen);

            Button bCopy2 = new Button();
            bCopy2.Text = "复制";
            bCopy2.Size = new Size(90, 32);
            bCopy2.Location = new Point(152, 240);
            bCopy2.Click += delegate
            {
                if (_txtArt.Text.Length == 0) return;
                try { Clipboard.SetText(_txtArt.Text); } catch { }
                _lblEst.Text = "已复制（" + _txtArt.Text.Length + " 字符）";
            };
            page.Controls.Add(bCopy2);

            Button bToOut = new Button();
            bToOut.Text = "写入生成页输出区";
            bToOut.Size = new Size(150, 32);
            bToOut.Location = new Point(250, 240);
            bToOut.Click += delegate
            {
                if (_txtArt.Text.Length == 0) return;
                if (_main != null) _main.SetGeneratedCode(_txtArt.Text);
                _lblEst.Text = "已写入「文本生成」页的输出区";
            };
            page.Controls.Add(bToOut);

            _txtArt = new TextBox();
            _txtArt.Multiline = true;
            _txtArt.ReadOnly = true;
            _txtArt.ScrollBars = ScrollBars.Both;
            _txtArt.WordWrap = false;
            _txtArt.Location = new Point(14, 282);
            _txtArt.Size = new Size(820, 260);
            _txtArt.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;
            _txtArt.Font = new Font("Consolas", 8f);
            page.Controls.Add(_txtArt);
        }

        private static Rectangle FitRect(int iw, int ih, Rectangle box)
        {
            double s = Math.Min((double)box.Width / iw, (double)box.Height / ih);
            int w = Math.Max(1, (int)(iw * s)), h = Math.Max(1, (int)(ih * s));
            return new Rectangle(box.X + (box.Width - w) / 2, box.Y + (box.Height - h) / 2, w, h);
        }

        private void UpdateEstimate()
        {
            if (_lblEst == null || _artImg == null) return;
            BlockArtOptions o = Current();
            _lblEst.Text = "原图 " + _artImg.Width + "×" + _artImg.Height + "｜" + BlockArt.Estimate(o.Columns, _artImg.Width, _artImg.Height, o);
        }

        private BlockArtOptions Current()
        {
            BlockArtOptions o = new BlockArtOptions();
            o.Columns = (int)_numCols.Value;
            o.Budget = (int)_numBudget.Value;
            o.AutoFit = _chkAutoFit.Checked;
            o.AspectFix = _chkAspect.Checked;
            o.CloseTag = _chkClose.Checked;
            switch (_cboGlyph.SelectedIndex)
            {
                case 1: o.Glyph = '\u2B1B'; break;
                case 2: o.Glyph = '\u25A0'; break;
                case 3: o.Glyph = '\u25CF'; break;
                default: o.Glyph = '\u2588'; break;
            }
            return o;
        }

        private void GenerateArt()
        {
            if (_artImg == null) { _lblEst.Text = "先拖一张图片进来"; return; }
            string info;
            string code = BlockArt.Render(_artImg, Current(), out info);
            _txtArt.Text = code;
            _lblEst.Text = info + "｜块字符 █ 需字体包含该字形，否则请换 ⬛ / ■";
        }

        private static Label MkLabel(string text, int x, int y)
        {
            Label l = new Label();
            l.Text = text;
            l.Location = new Point(x, y);
            l.AutoSize = true;
            l.Tag = "hint";
            return l;
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            base.OnFormClosed(e);
            // 服务留给主窗体继续用；这里不主动停，用户可在 URL 页手动停
        }
    }
}
