using System;
using System.Drawing;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace RichTextGenOcr
{
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new OcrForm(args.Length > 0 ? args[0] : null));
        }
    }

    internal sealed class OcrForm : Form
    {
        private readonly TextBox _out;
        private readonly Button _pick, _clip, _copy, _save;
        private readonly ProgressBar _bar;
        private readonly Label _info;

        private const string Filter =
            "图片|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff;*.webp;*.avif;*.heic;*.heif|所有文件|*.*";

        public OcrForm(string startupPath)
        {
            Text = "RichTextGen OCR · 图片文字提取";
            Font = new Font("Microsoft YaHei UI", 9f);
            ClientSize = new Size(760, 580);
            MinimumSize = new Size(560, 420);
            StartPosition = FormStartPosition.CenterScreen;
            AllowDrop = true;

            Panel top = new Panel { Dock = DockStyle.Top, Height = 92, Padding = new Padding(12, 10, 12, 6) };

            _pick = new Button { Text = "选择图片…", AutoSize = true, Location = new Point(12, 10) };
            _pick.Click += delegate { PickFile(); };

            _clip = new Button { Text = "从剪贴板", AutoSize = true, Location = new Point(112, 10) };
            _clip.Click += delegate { FromClipboard(); };

            _copy = new Button { Text = "复制文本", AutoSize = true, Location = new Point(206, 10) };
            _copy.Click += delegate { CopyText(); };

            _save = new Button { Text = "保存为 TXT", AutoSize = true, Location = new Point(296, 10) };
            _save.Click += delegate { SaveText(); };

            _info = new Label
            {
                AutoEllipsis = true,
                Location = new Point(12, 46),
                Size = new Size(730, 18),
                Text = "把图片拖进窗口，或点「选择图片…」。支持长截图，自动分段识别。"
            };

            _bar = new ProgressBar { Location = new Point(12, 66), Size = new Size(730, 12), Minimum = 0, Maximum = 100 };

            top.Controls.Add(_pick);
            top.Controls.Add(_clip);
            top.Controls.Add(_copy);
            top.Controls.Add(_save);
            top.Controls.Add(_info);
            top.Controls.Add(_bar);

            _out = new TextBox
            {
                Multiline = true,
                ScrollBars = ScrollBars.Both,
                WordWrap = false,
                Dock = DockStyle.Fill,
                Font = new Font("Cascadia Mono", 10.5f),
                AcceptsReturn = true
            };

            Panel body = new Panel { Dock = DockStyle.Fill, Padding = new Padding(12, 0, 12, 12) };
            body.Controls.Add(_out);

            Controls.Add(body);
            Controls.Add(top);

            DragEnter += delegate(object s, DragEventArgs e)
            {
                if (e.Data.GetDataPresent(DataFormats.FileDrop)) e.Effect = DragDropEffects.Copy;
            };
            DragDrop += delegate(object s, DragEventArgs e)
            {
                string[] files = e.Data.GetData(DataFormats.FileDrop) as string[];
                if (files != null && files.Length > 0) Run(files[0]);
            };

            if (!string.IsNullOrEmpty(startupPath) && File.Exists(startupPath))
                SetTimeout(200, delegate { Run(startupPath); });
        }

        private void SetTimeout(int ms, Action act)
        {
            Timer t = new Timer { Interval = ms };
            t.Tick += delegate { t.Stop(); t.Dispose(); try { act(); } catch { } };
            t.Start();
        }

        private void PickFile()
        {
            using (OpenFileDialog d = new OpenFileDialog())
            {
                d.Filter = Filter;
                d.Title = "选择要提取文字的图片";
                if (d.ShowDialog(this) != DialogResult.OK) return;
                Run(d.FileName);
            }
        }

        private void FromClipboard()
        {
            try
            {
                Image img = null;
                try
                {
                    IDataObject d = Clipboard.GetDataObject();
                    if (d != null && d.GetDataPresent("PNG"))
                    {
                        MemoryStream ms = d.GetData("PNG") as MemoryStream;
                        if (ms != null) using (Image im = Image.FromStream(ms)) img = new Bitmap(im);
                    }
                }
                catch { }
                if (img == null && Clipboard.ContainsImage()) img = Clipboard.GetImage();
                if (img == null) { _info.Text = "剪贴板里没有图片。"; return; }

                string tmp = Path.Combine(Path.GetTempPath(), "rtgocr-" + Guid.NewGuid().ToString("N") + ".png");
                try { img.Save(tmp, System.Drawing.Imaging.ImageFormat.Png); }
                finally { img.Dispose(); }
                Run(tmp, true);
            }
            catch (Exception ex) { _info.Text = "读取剪贴板失败：" + ex.Message; }
        }

        private void Run(string path, bool temp = false)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                _info.Text = "找不到图片：" + path;
                return;
            }
            SetBusy(true);
            _bar.Value = 0;
            _info.Text = "正在读取图片…";
            string name = Path.GetFileName(path);

            Task.Run(delegate
            {
                try
                {
                    string text = Ocr.Recognize(path, delegate(int pct, string msg)
                    {
                        BeginInvoke((Action)delegate
                        {
                            _bar.Value = Math.Max(0, Math.Min(100, pct));
                            _info.Text = msg;
                        });
                    });
                    BeginInvoke((Action)delegate
                    {
                        _out.Text = text;
                        _bar.Value = 100;
                        _info.Text = name + " · 识别完成，" + text.Length + " 个字符";
                        SetBusy(false);
                    });
                }
                catch (Exception ex)
                {
                    BeginInvoke((Action)delegate
                    {
                        _info.Text = "识别失败：" + ex.Message;
                        SetBusy(false);
                    });
                }
                finally
                {
                    if (temp) { try { File.Delete(path); } catch { } }
                }
            });
        }

        private void SetBusy(bool busy)
        {
            _pick.Enabled = _clip.Enabled = !busy;
            _copy.Enabled = _save.Enabled = !busy;
            Cursor = busy ? Cursors.WaitCursor : Cursors.Default;
        }

        private void CopyText()
        {
            if (_out.Text.Length == 0) { _info.Text = "还没有识别结果。"; return; }
            try { Clipboard.SetText(_out.Text); _info.Text = "已复制到剪贴板。"; }
            catch (Exception ex) { _info.Text = "复制失败：" + ex.Message; }
        }

        private void SaveText()
        {
            if (_out.Text.Length == 0) { _info.Text = "还没有识别结果。"; return; }
            using (SaveFileDialog d = new SaveFileDialog())
            {
                d.Filter = "文本文件|*.txt|所有文件|*.*";
                d.FileName = "OCR-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".txt";
                if (d.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    File.WriteAllText(d.FileName, _out.Text, new UTF8Encoding(false));
                    _info.Text = "已保存：" + d.FileName;
                }
                catch (Exception ex) { _info.Text = "保存失败：" + ex.Message; }
            }
        }
    }
}
