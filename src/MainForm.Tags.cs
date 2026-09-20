using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Text;
using System.Windows.Forms;

namespace RichTextGen
{
    public partial class MainForm
    {
        private int tagStackTop;
        private int tagStackBottom;
        private GroupBox gbAppearance;
        private ComboBox cboGroupFilter;

        /// <summary>只显示选中的分组（页面化）</summary>
        private void ApplyGroupFilter(string sel)
        {
            bool all = (sel == null) || sel.StartsWith("全部");
            foreach (KeyValuePair<string, GroupBox> kv in tagGroups)
                kv.Value.Visible = all || kv.Key == sel;
            RelayoutTagGroups();
        }
        private CheckBox chkLinkReplace;
        private bool previewReady;

        private ColorOutputMode ColorMode
        {
            get { return (ColorOutputMode)Math.Max(0, cboColorMode.SelectedIndex); }
        }

        // ================================================================ 标签分组 UI
        private void BuildTagGroupPanel(int yTop)
        {
            // 分组筛选：像 Win11 设置那样一次只看一类，避免一股脑全堆出来
            Label lf = MkHint("显示分组", 10, yTop + 4, leftPanel);
            leftPanel.Controls.Add(lf);
            cboGroupFilter = new ComboBox();
            cboGroupFilter.DropDownStyle = ComboBoxStyle.DropDownList;
            cboGroupFilter.Location = new Point(76, yTop);
            cboGroupFilter.Size = new Size(260, 24);
            cboGroupFilter.Items.Add("全部（显示所有分组）");
            foreach (string g in TagRegistry.DisplayOrder()) cboGroupFilter.Items.Add(g);
            cboGroupFilter.SelectedIndex = 0;
            cboGroupFilter.SelectedIndexChanged += delegate { ApplyGroupFilter(Convert.ToString(cboGroupFilter.SelectedItem)); };
            leftPanel.Controls.Add(cboGroupFilter);
            tagStackTop = yTop + 34;
            int y = yTop;
            foreach (string group in TagRegistry.DisplayOrder())
            {
                List<TagDef> defs = TagRegistry.InGroup(group);
                int rowCount = 0;
                for (int i = 0; i < defs.Count; i++)
                {
                    rowCount += (defs[i].Id == "link") ? 2 : 1;
                }
                int rows = (int)Math.Ceiling(rowCount / 2.0);
                int expandedH = 52 + rows * 30 + 6;

                GroupBox gb = new GroupBox();
                gb.Text = group;
                gb.Location = new Point(10, y);
                gb.Size = new Size(566, expandedH);
                leftPanel.Controls.Add(gb);
                tagGroups[group] = gb;
                gb.Tag = expandedH;

                CheckBox fold = new CheckBox();
                fold.Text = "展开";
                fold.Checked = IsGroupExpandedByDefault(group);
                fold.Location = new Point(10, 20);
                fold.AutoSize = true;
                gb.Controls.Add(fold);
                fold.CheckedChanged += delegate
                {
                    bool on = fold.Checked;
                    fold.Text = on ? "展开" : "收起（已勾选的标签仍会生效）";
                    gb.Height = on ? (int)gb.Tag : 46;
                    RelayoutTagGroups();
                };

                int rowY = 46, col = 0;
                for (int i = 0; i < defs.Count; i++)
                {
                    TagDef def = defs[i];
                    string label = def.Label;
                    if (def.Resource) label += "  ·需资源";
                    if (def.Legacy) label += "  ·旧版";

                    if (def.Id == "link")
                    {
                        // 超链接：单独占满一行（地址 + 显示文字）
                        if (col != 0) { col = 0; rowY += 30; }
                        CheckBox ck = new CheckBox();
                        ck.Text = label;
                        ck.Location = new Point(10, rowY);
                        ck.Size = new Size(146, 22);
                        gb.Controls.Add(ck);
                        tagCheck[def.Id] = ck;

                        TextBox url = new TextBox();
                        url.Location = new Point(152, rowY - 1);
                        url.Size = new Size(150, 24);
                        gb.Controls.Add(url);
                        tagInput[def.Id] = url;

                        TextBox disp = new TextBox();
                        disp.Location = new Point(306, rowY - 1);
                        disp.Size = new Size(150, 24);
                        gb.Controls.Add(disp);
                        tagExtra[def.Id] = disp;

                        CheckBox rep = new CheckBox();
                        rep.Text = "替换正文";
                        rep.Location = new Point(462, rowY + 1);
                        rep.AutoSize = true;
                        rep.CheckedChanged += delegate { QueuePreview(); };
                        gb.Controls.Add(rep);
                        chkLinkReplace = rep;

                        ToolTip tip = new ToolTip();
                        tip.SetToolTip(ck, def.Hint);
                        tip.SetToolTip(url, "链接地址（可为任意文本，如 https://… 或 invite 码）");
                        tip.SetToolTip(disp, "显示文字（留空则把正文作为显示文字）");
                        rowY += 30;
                        continue;
                    }

                    int cx = (col == 0) ? 10 : 292;
                    CheckBox chk = new CheckBox();
                    chk.Text = label;
                    chk.Location = new Point(cx, rowY);
                    chk.AutoSize = true;
                    chk.MaximumSize = new Size(212, 0);
                    chk.CheckedChanged += delegate { QueuePreview(); };
                    if (def.Id == "color") chk.Checked = true;   // 颜色系统默认生效（输入框留空=用主色）
                    gb.Controls.Add(chk);
                    tagCheck[def.Id] = chk;
                    ToolTip tp = new ToolTip();
                    tp.SetToolTip(chk, def.Hint.Length > 0 ? def.Hint : def.Label);

                    if (def.NeedsInput)
                    {
                        if (def.Param == TagParam.Enum)
                        {
                            ComboBox cb = new ComboBox();
                            cb.DropDownStyle = ComboBoxStyle.DropDownList;
                            cb.Location = new Point(cx + 146, rowY - 2);
                            cb.Size = new Size(64, 24);
                            if (def.EnumValues != null) cb.Items.AddRange(def.EnumValues);
                            cb.SelectedIndex = 0;
                            cb.SelectedIndexChanged += delegate { QueuePreview(); };
                            gb.Controls.Add(cb);
                            tagInput[def.Id] = cb;
                        }
                        else
                        {
                            TextBox tb = new TextBox();
                            tb.Location = new Point(cx + 146, rowY - 2);
                            tb.Size = new Size(64, 24);
                            tb.TextChanged += delegate { QueuePreview(); };
                            gb.Controls.Add(tb);
                            tagInput[def.Id] = tb;
                            tp.SetToolTip(tb, def.Hint + (def.Param == TagParam.Color ? "　（留空 = 使用上方「颜色系统」里的主色）" : ""));
                        }
                    }
                    col++;
                    if (col >= 2) { col = 0; rowY += 30; }
                }

                y += expandedH + 6;
                gb.Height = fold.Checked ? expandedH : 46;
            }
            tagStackBottom = y;
        }

        private static bool IsGroupExpandedByDefault(string group)
        {
            if (group == TagRegistry.GroupStyle) return true;
            if (group == TagRegistry.GroupColor) return true;
            if (group == TagRegistry.GroupFont) return true;
            if (group == TagRegistry.GroupCase) return false;
            if (group == TagRegistry.GroupBlock) return false;
            if (group == TagRegistry.GroupSpacing) return false;
            if (group == TagRegistry.GroupLink) return true;
            return false;
        }

        /// <summary>自由编辑系统：一键展开/收纳全部标签组</summary>
        private void SetAllGroupsExpanded(bool expand)
        {
            foreach (KeyValuePair<string, GroupBox> kv in tagGroups)
            {
                CheckBox fold = null;
                foreach (Control c2 in kv.Value.Controls)
                {
                    CheckBox cb = c2 as CheckBox;
                    if (cb != null) { fold = cb; break; }
                }
                if (fold == null) continue;
                bool want = expand ? true : IsGroupExpandedByDefault(kv.Key);
                if (fold.Checked != want) fold.Checked = want;   // 触发高度重算与重排
            }
            RelayoutTagGroups();
        }

        private void RelayoutTagGroups()
        {
            if (tagGroups.Count == 0) return;
            int y = 0;
            bool first = true;
            foreach (string group in TagRegistry.DisplayOrder())
            {
                GroupBox gb;
                if (!tagGroups.TryGetValue(group, out gb)) continue;
                if (!gb.Visible) continue;                       // 筛选时跳过隐藏分组，避免留空
                if (first) { y = gb.Top; first = false; }   // 以当前实际位置为基准（缩放后也正确）
                gb.Location = new Point(gb.Left, y);
                y += gb.Height + 6;
            }
            tagStackBottom = y;
            if (gbAppearance != null) gbAppearance.Location = new Point(gbAppearance.Left, y + 6);
        }

        // ================================================================ 采集与生成
        private List<ActiveTag> CollectTags()
        {
            List<ActiveTag> active = new List<ActiveTag>();
            foreach (TagDef def in TagRegistry.All)
            {
                CheckBox chk;
                if (!tagCheck.TryGetValue(def.Id, out chk)) continue;
                if (!chk.Checked) continue;

                string val = "";
                if (def.NeedsInput)
                {
                    Control c;
                    if (!tagInput.TryGetValue(def.Id, out c)) continue;
                    ComboBox cb = c as ComboBox;
                    val = ((cb != null) ? Convert.ToString(cb.SelectedItem) : c.Text).Trim();
                    if (def.Param == TagParam.Color && val.Length == 0) val = txtColor.Text.Trim();
                    if (val.Length == 0) continue;   // 有参数但没填 → 跳过，避免产出非法标签
                }
                if (def.Param == TagParam.Color)
                {
                    Rgb cc;
                    if (!ColorUtil.TryParse(val, out cc)) continue;
                    // mark 需要 #RRGGBBAA，固定补 80（半透明），不吃「色名模式」
                    if (def.Id == "mark") val = "#" + cc.Hex + "80";
                    else val = ColorUtil.ToTagValue(cc, ColorMode);
                }

                else if (def.Id == "alpha")
                {
                    string h = val.TrimStart('#').ToUpperInvariant();
                    if (h.Length != 2) continue;
                    val = "#" + h;
                }

                ActiveTag at = new ActiveTag(def, val);
                if (def.Id == "link")
                {
                    TextBox ex;
                    if (tagExtra.TryGetValue(def.Id, out ex)) at.Extra = ex.Text.Trim();
                    at.ReplaceBody = (chkLinkReplace != null && chkLinkReplace.Checked);
                    if (val.Length == 0) at.Value = at.Extra;
                }
                active.Add(at);
            }
            return active;
        }

        private string GenerateOneLine(string line, List<ActiveTag> tags)
        {
            string content = line;
            if (chkGrad != null && chkGrad.Checked) content = ApplyGradient(content);
            content = ApplyFrame(content);
            string withTags = TagRegistry.Generate(content, tags);
            withTags = ApplyBrackets(withTags);
            return withTags;
        }

        private string ApplyFrame(string text)
        {
            if (cboFrame == null || text.Length == 0) return text;
            int idx = cboFrame.SelectedIndex;
            if (idx <= 0 || text.Length == 0) return text;
            string l = "", r = "";
            switch (idx)
            {
                case 1: l = "▌"; r = "▌"; break;
                case 2: l = "【"; r = "】"; break;
                case 3: l = "◆ "; r = " ◆"; break;
                case 4: l = "★ "; r = " ★"; break;
            }
            return l + text + r;
        }

        private string ApplyBrackets(string text)
        {
            if (cboBracket == null || text.Length == 0) return text;
            int idx = cboBracket.SelectedIndex;
            if (idx <= 0 || text.Length == 0) return text;
            string[] pairs = { "", "（）", "【】", "「」", "『』", "《》", "{}", "[]", "()" };
            string p = pairs[Math.Min(idx, pairs.Length - 1)];
            if (p.Length < 2) return text;
            return p.Substring(0, 1) + text + p.Substring(1, 1);
        }

        private string ApplyGradient(string text)
        {
            if (text.Length == 0) return text;
            Rgb s, e;
            if (!ColorUtil.TryParse(txtGradStart.Text, out s)) s = new Rgb(255, 0, 0);
            if (!ColorUtil.TryParse(txtGradEnd.Text, out e)) e = new Rgb(0, 0, 255);

            int len = text.Length;
            int period = (int)numPeriod.Value;
            if (period <= 0) period = len;
            if (period <= 0) period = 1;

            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < len; i++)
            {
                int pos = chkLoopGrad.Checked ? (i % period) : i;
                int span = chkLoopGrad.Checked ? period : len;
                double t = (span > 1) ? (double)pos / (span - 1) : 0;
                if (chkAxisGrad != null && chkAxisGrad.Checked) t = 1.0 - Math.Abs(2.0 * t - 1.0);   // 轴对称：中心镜像 A→B→A
                byte r = (byte)Math.Round(s.R + (e.R - s.R) * t);
                byte g = (byte)Math.Round(s.G + (e.G - s.G) * t);
                byte b = (byte)Math.Round(s.B + (e.B - s.B) * t);
                string hex = string.Format("#{0:X2}{1:X2}{2:X2}", r, g, b);
                string ch = text.Substring(i, 1);
                if (ch == "<") ch = "&lt;";
                else if (ch == ">") ch = "&gt;";
                else if (ch == "&") ch = "&amp;";
                sb.Append("<color=" + hex + ">" + ch + "</color>");
            }
            return sb.ToString();
        }

        private string BuildFullCode()
        {
            List<ActiveTag> tags = CollectTags();
            string text = txtText.Text.Replace("\r\n", "\n").Replace("\r", "\n");
            string[] lines = text.Split('\n');

            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < lines.Length; i++)
            {
                string one = GenerateOneLine(lines[i], tags);
                if (one.Length == 0) continue;
                if (sb.Length > 0) sb.Append("\n");
                sb.Append(one);
            }
            string body = sb.ToString();
            if (chkPrefix != null && chkPrefix.Checked && cboCommand != null && cboCommand.SelectedIndex < 2 && body.Length > 0)
                body = Convert.ToString(cboCommand.SelectedItem) + " " + body;
            return body;
        }

        // ================================================================ 预览
        private void UpdatePreviewNow()
        {
            if (!uiReady || txtResult == null) return;   // 界面未就绪/已销毁
            string code = BuildFullCode();
            txtResult.Text = code;
            RenderPreview(code);
            if (_outPreviewMode) RenderOutPreview();
            int tagCount = CollectTags().Count;
            status("已启用标签 " + tagCount + " 个 · 输出 " + code.Length + " 字符");
        }

        private void RenderPreview(string code)
        {
            StringBuilder body = new StringBuilder();
            string[] lines = code.Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                if (chkPrefix.Checked)
                {
                    string cmd = Convert.ToString(cboCommand.SelectedItem);
                    if (cmd.Length > 0 && line.StartsWith(cmd + " "))
                        line = line.Substring(cmd.Length + 1);
                }
                body.Append(HtmlFromLine(line));
                body.Append("<br>");
            }
            if (code.Trim().Length == 0)
            {
                body.Length = 0;
                body.Append("<span style='color:#888;font-style:italic'>（输入文本后此处显示预览）</span>");
            }

            string back = Theme.PreviewBack.R + "," + Theme.PreviewBack.G + "," + Theme.PreviewBack.B;
            string fore = Theme.PreviewText.R + "," + Theme.PreviewText.G + "," + Theme.PreviewText.B;
            string html =
                "<!DOCTYPE html><html><head><meta charset='utf-8'><style>" +
                "body{margin:0;padding:12px;background:rgb(" + back + ");color:rgb(" + fore + ");" +
                "font-family:'Microsoft YaHei','Segoe UI',sans-serif;font-size:" + (int)(18 * UiZoom.Factor) + "px;word-break:break-word}" +
                "a{color:#4da3ff}code{background:rgba(128,128,128,.2);padding:1px 4px;border-radius:3px}" +
                "</style></head><body><div id='c'>" + body + "</div></body></html>";

            try
            {
                if (!previewReady)
                {
                    wbPreview.DocumentText = html;
                    previewReady = true;
                }
                else
                {
                    wbPreview.Document.Body.InnerHtml = body.ToString();
                }
            }
            catch
            {
                try { wbPreview.DocumentText = html; } catch { }
            }
        }

        private string HtmlFromLine(string line)
        {
            StringBuilder sb = new StringBuilder();
            Stack<TagDef> stack = new Stack<TagDef>();
            int i = 0;
            while (i < line.Length)
            {
                int lt = line.IndexOf('<', i);
                if (lt < 0) { sb.Append(Escape(line.Substring(i))); break; }
                if (lt > i) sb.Append(Escape(line.Substring(i, lt - i)));
                int gt = line.IndexOf('>', lt);
                if (gt < 0) { sb.Append(Escape(line.Substring(lt))); break; }

                string token = line.Substring(lt + 1, gt - lt - 1);
                i = gt + 1;
                bool closing = token.StartsWith("/");
                string body = closing ? token.Substring(1) : token;
                string name = body, val = "";
                int eq = body.IndexOf('=');
                if (eq >= 0)
                {
                    name = body.Substring(0, eq);
                    val = body.Substring(eq + 1).Trim();
                    if (val.Length >= 2 && val.StartsWith("\"") && val.EndsWith("\"")) val = val.Substring(1, val.Length - 2);
                }
                name = name.Trim().ToLowerInvariant();
                TagDef def = TagRegistry.ById(name);
                if (def == null)
                {
                    sb.Append(Escape("<" + token + ">"));
                    continue;
                }
                if (closing)
                {
                    if (stack.Count > 0) { stack.Pop(); sb.Append(PreviewHtml.Close(def)); }
                    continue;
                }
                sb.Append(PreviewHtml.Open(def, val));
                if (def.Kind == TagKind.Wrap) stack.Push(def);
            }
            while (stack.Count > 0) { sb.Append(PreviewHtml.Close(stack.Pop())); }
            return sb.ToString();
        }

        private static string Escape(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
        }
    }
}
