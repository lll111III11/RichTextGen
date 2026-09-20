using System;
using System.Text;
using System.Windows.Forms;

namespace RichTextGen
{
    public partial class MainForm
    {
        /// <summary>把外部生成的代码写进输出区（色块图 / 托盘热键发送都会用到）</summary>
        public void SetGeneratedCode(string code)
        {
            if (txtResult == null) return;
            txtResult.Text = code;
            previewReady = false;
            RenderPreview(code);
            if (_outPreviewMode) RenderOutPreview();
            status("已写入输出区（" + code.Length + " 字符）");
        }

        /// <summary>把生成代码（含标签）渲染成 HTML，供「输出区预览模式」使用</summary>
        private string HtmlFromCode(string code)
        {
            if (string.IsNullOrEmpty(code)) return "<span style=\"opacity:.6\">(空)</span>";
            StringBuilder sb = new StringBuilder();
            string[] lines = code.Split(new char[] { (char)10 });
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].TrimEnd('\r');
                if (chkPrefix.Checked)
                {
                    string cmd = Convert.ToString(cboCommand.SelectedItem);
                    if (cmd.Length > 0 && line.StartsWith(cmd + " ")) line = line.Substring(cmd.Length + 1);
                }
                sb.Append(HtmlFromLine(line));
                sb.Append("<br>");
            }
            return sb.ToString();
        }
    }
}
