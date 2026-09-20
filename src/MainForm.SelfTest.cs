using System;
using System.IO;
using System.Text;
using System.Windows.Forms;

namespace RichTextGen
{
    public partial class MainForm
    {
        /// <summary>自检：程序化勾选一组标签 → 生成 → 写出结果（供自动化核对规范嵌套）</summary>
        public void RunSelfTest(string outPath)
        {
            StringBuilder log = new StringBuilder();
            txtText.Text = "测试ABC";
            txtColor.Text = "#FF8800";
            cboColorMode.SelectedIndex = 0;
            chkPrefix.Checked = true;
            cboCommand.SelectedIndex = 0;
            chkGrad.Checked = false;

            EnableTag("b", null);
            EnableTag("sub", null);
            EnableTag("nobr", null);
            EnableTag("uppercase", null);
            EnableTag("color", null);
            EnableTag("alpha", "#C8");
            EnableTag("mark", "#0066FF");
            EnableTag("size", "24");
            EnableTag("font", "MainFont");
            EnableTag("cspace", "5");
            EnableTag("voffset", "-3");
            EnableTag("rotate", "15");
            EnableTag("link", "https://example.com/invite", "点这里");
            EnableTag("sprite", "0");

            UpdatePreviewNow();

            log.AppendLine("== 颜色模式 ==");
            foreach (ColorOutputMode m in new ColorOutputMode[] { ColorOutputMode.Hex, ColorOutputMode.Rgb, ColorOutputMode.Name })
            {
                Rgb c;
                ColorUtil.TryParse("255,136,0", out c);
                log.AppendLine(m + " -> <color=" + ColorUtil.ToTagValue(c, m) + ">");
            }
            Rgb named;
            ColorUtil.TryParse("white", out named);
            log.AppendLine("white -> <color=" + ColorUtil.ToTagValue(named, ColorOutputMode.Name) + ">  hex=" + named.HexSharp + "  rgb=" + named.RgbText);

            log.AppendLine();
            log.AppendLine("== 启用标签数 ==");
            log.AppendLine(CollectTags().Count.ToString());
            log.AppendLine();
            log.AppendLine("== 生成结果 ==");
            log.AppendLine(txtResult.Text);
            log.AppendLine();
            log.AppendLine("== 反向嵌套自检 ==");
            log.AppendLine(CheckNesting(txtResult.Text));

            try { File.WriteAllText(outPath, log.ToString(), new UTF8Encoding(true)); }
            catch (Exception ex) { File.AppendAllText(outPath, "WRITE ERROR " + ex.Message); }
        }

        /// <summary>演示内容：两行带样式的文本，用于人工核对预览与输出</summary>
        public void ApplyDemo()
        {
            txtText.Text = "SCP:SL 富文本演示 ABC123" + Environment.NewLine + "第二行：渐变与高亮";
            txtColor.Text = "#FF8800";
            cboColorMode.SelectedIndex = 0;
            chkGrad.Checked = false;
            chkPrefix.Checked = true;
            cboCommand.SelectedIndex = 0;
            EnableTag("b", null);
            EnableTag("color", null);
            EnableTag("size", "26");
            EnableTag("mark", "#003366");
            EnableTag("sub", null);
            EnableTag("link", "https://example.com", "点这里");
        }

        private void EnableTag(string id, string value)
        {
            EnableTag(id, value, null);
        }

        private void EnableTag(string id, string value, string extra)
        {
            CheckBox chk;
            if (tagCheck.TryGetValue(id, out chk)) chk.Checked = true;
            if (value != null)
            {
                Control c;
                if (tagInput.TryGetValue(id, out c))
                {
                    ComboBox cb = c as ComboBox;
                    if (cb != null) cb.SelectedIndex = 0;
                    else c.Text = value;
                }
            }
            if (extra != null)
            {
                TextBox tb;
                if (tagExtra.TryGetValue(id, out tb)) tb.Text = extra;
            }
        }

        /// <summary>括号配对与闭合顺序检查：返回 OK 或第一处错误</summary>
        private static string CheckNesting(string code)
        {
            System.Collections.Generic.Stack<string> stack = new System.Collections.Generic.Stack<string>();
            int i = 0;
            while (i < code.Length)
            {
                int lt = code.IndexOf('<', i);
                if (lt < 0) break;
                int gt = code.IndexOf('>', lt);
                if (gt < 0) return "未闭合的 '<' 于位置 " + lt;
                string token = code.Substring(lt + 1, gt - lt - 1);
                i = gt + 1;
                if (token.StartsWith("/"))
                {
                    string name = token.Substring(1).Trim().ToLowerInvariant();
                    if (stack.Count == 0) return "多余的闭合标签 </" + name + ">";
                    string top = stack.Pop();
                    if (top != name) return "闭合顺序错误：期望 </" + top + ">，实际 </" + name + ">";
                }
                else
                {
                    string name = token;
                    int eq = name.IndexOf('=');
                    if (eq >= 0) name = name.Substring(0, eq);
                    name = name.Trim().ToLowerInvariant();
                    TagDef def = TagRegistry.ById(name);
                    if (def != null && def.Kind == TagKind.Wrap) stack.Push(def.TagName.ToLowerInvariant());
                }
            }
            if (stack.Count > 0) return "有未闭合的标签：" + string.Join(",", stack.ToArray());
            return "OK（括号全部配对，闭合顺序正确）";
        }
    }
}
