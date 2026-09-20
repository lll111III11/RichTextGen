using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace RichTextGen
{
    public enum TagKind
    {
        Wrap,     // 有闭合标签，包裹文本
        Prefix,   // 单标签，插在文本最前
        Suffix    // 单标签，插在文本最后
    }

    public enum TagParam
    {
        None,   // 无参数（开关）
        Color,  // 颜色参数（跟随「颜色模式」输出 Hex / RGB / 色名）
        Text,   // 自由文本（自行校验）
        Size,   // 尺寸/数值（50、+4、2em、50%、12px）
        Enum    // 枚举（下拉）
    }

    public class TagDef
    {
        public string Id;
        public string Group;
        public string Label;
        public string TagName;
        public TagKind Kind;
        public TagParam Param;
        public string[] EnumValues;
        public string Hint;
        public int Order;          // 越小越靠外层
        public bool Resource;      // 依赖游戏内资源（可能不生效）
        public bool Legacy;        // 旧版 UGUI 标签（TMP 一般无效）
        public int Lines = 1;      // UI 占几行（避免同组控件互相挤）

        public TagDef(string id, string group, string label, string tagName, TagKind kind, TagParam param,
                      int order, string hint, bool resource = false, bool legacy = false, string[] enumValues = null)
        {
            Id = id; Group = group; Label = label; TagName = tagName; Kind = kind; Param = param;
            Order = order; Hint = hint ?? ""; Resource = resource; Legacy = legacy; EnumValues = enumValues;
        }

        public bool NeedsInput { get { return Param != TagParam.None; } }

        public string OpenTag(string value)
        {
            if (Param == TagParam.None) return "<" + TagName + ">";
            return "<" + TagName + "=" + value + ">";
        }

        public string CloseTag()
        {
            if (Kind == TagKind.Wrap) return "</" + TagName + ">";
            return "";
        }
    }

    /// <summary>用户在 UI 上勾选/填写的标签实例</summary>
    public class ActiveTag
    {
        public TagDef Def;
        public string Value;
        public string Extra;   // link 用：显示文字
        public bool ReplaceBody;   // link 用：显示文字是否替换正文（否则追加在正文后）

        public ActiveTag(TagDef def, string value) { Def = def; Value = value; Extra = ""; }
    }

    /// <summary>
    /// 标签注册表：一处定义 → UI 自动生成 / 生成器规范嵌套 / 预览映射，三处同源。
    /// 覆盖 Unity 6 (TextMeshPro) 全部富文本标签 + 旧版 UGUI 兼容标签。
    /// </summary>
    public static class TagRegistry
    {
        public const string GroupBlock = "对齐与位置（作用于整段）";
        public const string GroupStyle = "样式";
        public const string GroupCase = "大小写";
        public const string GroupColor = "颜色";
        public const string GroupFont = "大小与字体";
        public const string GroupSpacing = "字距与偏移";
        public const string GroupLink = "超链接";
        public const string GroupBreak = "换行";
        public const string GroupResource = "资源类（需游戏内资源）";
        public const string GroupLegacy = "旧版兼容（TMP 一般不生效）";

        public static readonly List<TagDef> All = new List<TagDef>
        {
            // ---------- 对齐与位置（块级，最外层） ----------
            new TagDef("align", GroupBlock, "整段对齐 <align>", "align", TagKind.Prefix, TagParam.Enum, 1,
                       "left / center / right / justified / flush，作用于整段", false, false,
                       new string[] { "left", "center", "right", "justified", "flush" }),
            new TagDef("pos", GroupBlock, "水平起始位置 <pos>", "pos", TagKind.Prefix, TagParam.Size, 3,
                       "如 50% 或 120px，从该位置开始排版"),
            new TagDef("space", GroupBlock, "预留空白 <space>", "space", TagKind.Prefix, TagParam.Size, 4,
                       "如 20px，插入等宽空白"),
            new TagDef("width", GroupBlock, "文本宽度 <width>", "width", TagKind.Wrap, TagParam.Size, 5,
                       "如 200px / 20em"),
            new TagDef("margin", GroupBlock, "边距 <margin>", "margin", TagKind.Wrap, TagParam.Size, 6,
                       "如 10px；也可写 margin-left=10px"),
            new TagDef("indent", GroupBlock, "整段缩进 <indent>", "indent", TagKind.Wrap, TagParam.Size, 7,
                       "如 15% / 20px"),
            new TagDef("line-indent", GroupBlock, "首行缩进 <line-indent>", "line-indent", TagKind.Wrap, TagParam.Size, 8,
                       "如 10% / 20px"),
            new TagDef("line-height", GroupBlock, "行高 <line-height>", "line-height", TagKind.Wrap, TagParam.Size, 9,
                       "如 25px / 120% / 1.5em"),

            // ---------- 换行 ----------
            new TagDef("br", "换行", "换行 <br>", "br", TagKind.Suffix, TagParam.None, 10, "在文本末尾插入换行"),

            // ---------- 样式 ----------
            new TagDef("b", GroupStyle, "粗体 <b>", "b", TagKind.Wrap, TagParam.None, 20, ""),
            new TagDef("i", GroupStyle, "斜体 <i>", "i", TagKind.Wrap, TagParam.None, 21, ""),
            new TagDef("u", GroupStyle, "下划线 <u>", "u", TagKind.Wrap, TagParam.None, 22, ""),
            new TagDef("s", GroupStyle, "删除线 <s>", "s", TagKind.Wrap, TagParam.None, 23, ""),
            new TagDef("sub", GroupStyle, "下标 <sub>", "sub", TagKind.Wrap, TagParam.None, 24, ""),
            new TagDef("sup", GroupStyle, "上标 <sup>", "sup", TagKind.Wrap, TagParam.None, 25, ""),
            new TagDef("nobr", GroupStyle, "不折行 <nobr>", "nobr", TagKind.Wrap, TagParam.None, 26, ""),
            new TagDef("noparse", GroupStyle, "不解析 <noparse>", "noparse", TagKind.Wrap, TagParam.None, 27,
                       "包裹内文本不再解析标签，原样显示"),

            // ---------- 大小写 ----------
            new TagDef("uppercase", GroupCase, "全部大写 <uppercase>", "uppercase", TagKind.Wrap, TagParam.None, 30, ""),
            new TagDef("lowercase", GroupCase, "全部小写 <lowercase>", "lowercase", TagKind.Wrap, TagParam.None, 31, ""),
            new TagDef("smallcaps", GroupCase, "小型大写 <smallcaps>", "smallcaps", TagKind.Wrap, TagParam.None, 32, ""),
            new TagDef("allcaps", GroupCase, "大写（含小写字母）<allcaps>", "allcaps", TagKind.Wrap, TagParam.None, 33, ""),

            // ---------- 颜色 ----------
            new TagDef("color", GroupColor, "文字颜色 <color>", "color", TagKind.Wrap, TagParam.Color, 40,
                       "跟随顶部「颜色模式」输出：#RRGGBB / 色名"),
            new TagDef("alpha", GroupColor, "透明度 <alpha>", "alpha", TagKind.Wrap, TagParam.Text, 41,
                       "#00 全透明 → #FF 不透明（两位十六进制）"),
            new TagDef("mark", GroupColor, "高亮底色 <mark>", "mark", TagKind.Wrap, TagParam.Color, 42,
                       "#RRGGBBAA，默认补 80 半透明"),

            // ---------- 大小与字体 ----------
            new TagDef("size", GroupFont, "字号 <size>", "size", TagKind.Wrap, TagParam.Size, 50,
                       "50 / +4 / -2 / 2em / 50%"),
            new TagDef("font", GroupFont, "字体 <font>", "font", TagKind.Wrap, TagParam.Text, 51,
                       "字体资源名（需游戏内存在）", true),
            new TagDef("font-weight", GroupFont, "字重 <font-weight>", "font-weight", TagKind.Wrap, TagParam.Text, 52,
                       "100-900，需字体资源支持", true),

            // ---------- 字距与偏移 ----------
            new TagDef("cspace", GroupSpacing, "字距 <cspace>", "cspace", TagKind.Wrap, TagParam.Size, 60, "如 1em / 5px"),
            new TagDef("mspace", GroupSpacing, "等宽 <mspace>", "mspace", TagKind.Wrap, TagParam.Size, 61, "如 1em / 10px"),
            new TagDef("voffset", GroupSpacing, "垂直偏移 <voffset>", "voffset", TagKind.Wrap, TagParam.Size, 62, "如 0.5em / 5px"),
            new TagDef("rotate", GroupSpacing, "旋转 <rotate>", "rotate", TagKind.Wrap, TagParam.Text, 63, "角度，如 30 / -15"),

            // ---------- 超链接 ----------
            new TagDef("link", GroupLink, "超链接 <link>", "link", TagKind.Wrap, TagParam.Text, 70,
                       "TMP 的 link 需鼠标命中才可点；游戏内提示/广播一般点不了，只会按样式显示文字"),

            // ---------- 资源类 ----------
            new TagDef("sprite", GroupResource, "精灵图 <sprite>", "sprite", TagKind.Prefix, TagParam.Text, 80,
                       "如 0 / index=3 / name=\"star\"（需精灵资源）", true),
            new TagDef("style", GroupResource, "样式表 <style>", "style", TagKind.Wrap, TagParam.Text, 81,
                       "TMP 样式表名（需样式资源）", true),
            new TagDef("gradient", GroupResource, "渐变预设 <gradient>", "gradient", TagKind.Wrap, TagParam.Text, 82,
                       "TMP 渐变预设名（需预设资源）", true),

            // ---------- 旧版兼容 ----------
            new TagDef("quad", GroupLegacy, "四边形 <quad>", "quad", TagKind.Prefix, TagParam.Text, 90,
                       "旧版 UGUI 图片标签", false, true),
            new TagDef("material", GroupLegacy, "材质 <material>", "material", TagKind.Wrap, TagParam.Text, 91,
                       "旧版 UGUI 材质标签", false, true)
        };

        public static TagDef ById(string id)
        {
            for (int i = 0; i < All.Count; i++) if (All[i].Id == id) return All[i];
            return null;
        }

        /// <summary>UI 显示顺序：常用组在前，块级/资源/旧版在后</summary>
        public static List<string> DisplayOrder()
        {
            return new List<string>
            {
                GroupStyle, GroupColor, GroupFont, GroupLink, GroupCase,
                GroupSpacing, GroupBlock, GroupBreak, GroupResource, GroupLegacy
            };
        }

        public static List<string> GroupOrder()
        {
            List<string> groups = new List<string>();
            for (int i = 0; i < All.Count; i++)
            {
                if (!groups.Contains(All[i].Group)) groups.Add(All[i].Group);
            }
            return groups;
        }

        public static List<TagDef> InGroup(string group)
        {
            List<TagDef> list = new List<TagDef>();
            for (int i = 0; i < All.Count; i++) if (All[i].Group == group) list.Add(All[i]);
            list.Sort(delegate (TagDef a, TagDef b) { return a.Order.CompareTo(b.Order); });
            return list;
        }

        /// <summary>
        /// 规范嵌套生成：按 Order 升序，最外层先开、内层后开；闭合顺序严格反向。
        /// 单标签（Prefix/Suffix）不参与嵌套，分别置于最前 / 最后。
        /// </summary>
        public static string Generate(string text, List<ActiveTag> active)
        {
            if (active == null || active.Count == 0) return text;

            List<ActiveTag> wraps = new List<ActiveTag>();
            List<ActiveTag> prefixes = new List<ActiveTag>();
            List<ActiveTag> suffixes = new List<ActiveTag>();
            for (int i = 0; i < active.Count; i++)
            {
                ActiveTag t = active[i];
                if (t.Def.Kind == TagKind.Wrap) wraps.Add(t);
                else if (t.Def.Kind == TagKind.Prefix) prefixes.Add(t);
                else suffixes.Add(t);
            }
            Comparison<ActiveTag> cmp = delegate (ActiveTag a, ActiveTag b) { return a.Def.Order.CompareTo(b.Def.Order); };
            wraps.Sort(cmp);
            prefixes.Sort(cmp);
            suffixes.Sort(cmp);

            string content = text;
            // link 特殊：提供了显示文字时，用它替换被包裹内容
            for (int i = 0; i < wraps.Count; i++)
            {
                ActiveTag t = wraps[i];
                if (t.Def.Id == "link")
                {
                    string link = "<link=\"" + (t.Value ?? "") + "\">" + ((t.Extra != null && t.Extra.Length > 0) ? t.Extra : content) + "</link>";
                    // 默认不吞掉正文：填写了显示文字且未勾选「替换正文」时，链接追加在正文之后
                    if (t.Extra != null && t.Extra.Length > 0 && !t.ReplaceBody)
                        content = content + link;
                    else
                        content = link;
                }
            }

            StringBuilder open = new StringBuilder();
            StringBuilder close = new StringBuilder();
            for (int i = 0; i < wraps.Count; i++)
            {
                ActiveTag t = wraps[i];
                if (t.Def.Id == "link") continue;
                open.Append(t.Def.OpenTag(t.Value));
                close.Insert(0, t.Def.CloseTag());
            }

            StringBuilder head = new StringBuilder();
            for (int i = 0; i < prefixes.Count; i++) head.Append(prefixes[i].Def.OpenTag(prefixes[i].Value));

            StringBuilder tail = new StringBuilder();
            for (int i = 0; i < suffixes.Count; i++) tail.Append(suffixes[i].Def.OpenTag(suffixes[i].Value));

            // noparse 必须最外层，否则内部标签会被转义逻辑破坏
            string inner = open.ToString() + content + close.ToString();
            ActiveTag noparse = null;
            for (int i = 0; i < wraps.Count; i++) if (wraps[i].Def.Id == "noparse") noparse = wraps[i];
            if (noparse != null)
            {
                inner = inner.Replace("<noparse>", "").Replace("</noparse>", "");
                inner = "<noparse>" + inner + "</noparse>";
            }
            return head.ToString() + inner + tail.ToString();
        }

        /// <summary>把标签 + 内容映射为浏览器 HTML（预览用），无法在浏览器还原的标签给出徽标提示</summary>
        public static string HtmlWrap(TagDef def, string value, string inner, string colorTagValue)
        {
            string v = value == null ? "" : value.Trim();
            switch (def.Id)
            {
                case "b": return "<b>" + inner + "</b>";
                case "i": return "<i>" + inner + "</i>";
                case "u": return "<u>" + inner + "</u>";
                case "s": return "<s>" + inner + "</s>";
                case "sub": return "<sub>" + inner + "</sub>";
                case "sup": return "<sup>" + inner + "</sup>";
                case "nobr": return "<span style=\"white-space:nowrap\">" + inner + "</span>";
                case "noparse": return "<code>" + inner + "</code>";
                case "uppercase": return "<span style=\"text-transform:uppercase\">" + inner + "</span>";
                case "lowercase": return "<span style=\"text-transform:lowercase\">" + inner + "</span>";
                case "smallcaps": return "<span style=\"font-variant:small-caps\">" + inner + "</span>";
                case "allcaps": return "<span style=\"text-transform:uppercase\">" + inner + "</span>";
                case "color": return "<span style=\"color:" + colorTagValue + "\">" + inner + "</span>";
                case "alpha": return "<span style=\"opacity:" + AlphaToOpacity(v) + "\">" + inner + "</span>";
                case "mark": return "<span style=\"background-color:" + MarkColor(v) + "\">" + inner + "</span>";
                case "size": return "<span style=\"font-size:" + CssSize(v) + "\">" + inner + "</span>";
                case "font": return "<span style=\"font-family:'" + Esc(v) + "'\">" + inner + "</span>";
                case "font-weight": return "<span style=\"font-weight:" + Esc(v) + "\">" + inner + "</span>";
                case "cspace": return "<span style=\"letter-spacing:" + CssSize(v) + "\">" + inner + "</span>";
                case "mspace": return "<span style=\"font-family:monospace;letter-spacing:" + CssSize(v) + "\">" + inner + "</span>";
                case "voffset": return "<span style=\"position:relative;top:-" + CssSize(v) + "\">" + inner + "</span>";
                case "rotate": return "<span style=\"display:inline-block;transform:rotate(" + Esc(v) + "deg)\">" + inner + "</span>";
                case "line-height": return "<span style=\"line-height:" + CssSize(v) + "\">" + inner + "</span>";
                case "indent": return "<span style=\"padding-left:" + CssSize(v) + "\">" + inner + "</span>";
                case "line-indent": return "<span style=\"text-indent:" + CssSize(v) + "\">" + inner + "</span>";
                case "width": return "<span style=\"display:inline-block;width:" + CssSize(v) + "\">" + inner + "</span>";
                case "margin": return "<span style=\"display:inline-block;margin:" + CssSize(v) + "\">" + inner + "</span>";
                case "align": return "<div style=\"text-align:" + AlignCss(v) + "\">" + inner + "</div>";
                case "pos": return "<span style=\"margin-left:" + CssSize(v) + "\">" + inner + "</span>";
                case "space": return "<span style=\"display:inline-block;width:" + CssSize(v) + "\"></span>" + inner;
                case "br": return inner + "<br>";
                case "link": return "<a href=\"" + Esc(v) + "\" onclick=\"return false\">" + inner + "</a>";
            }
            // 资源类 / 旧版：浏览器无法还原，用徽标如实标出
            return inner + Badge(def, v);
        }

        private static string Badge(TagDef def, string v)
        {
            string t = "<" + def.TagName + (v.Length > 0 ? "=" + v : "") + ">";
            return "<span style=\"border:1px solid #b08;color:#b08;border-radius:4px;padding:0 3px;font-size:11px\">" + Esc(t) + "</span>";
        }

        private static string AlignCss(string v)
        {
            if (v == "center") return "center";
            if (v == "right") return "right";
            if (v == "justified" || v == "flush") return "justify";
            return "left";
        }

        private static string AlphaToOpacity(string v)
        {
            string h = v.TrimStart('#');
            int n;
            if (h.Length == 2 && int.TryParse(h, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out n))
                return Math.Round(n / 255.0, 3).ToString(CultureInfo.InvariantCulture);
            return "1";
        }

        private static string MarkColor(string v)
        {
            string c = v.Trim();
            if (c.StartsWith("#") && c.Length == 7) c = c + "80";
            if (!c.StartsWith("#")) c = "#" + c;
            return c;
        }

        private static string CssSize(string v)
        {
            string s = v.Trim();
            if (s.Length == 0) return "0";
            if (s.EndsWith("%") || s.EndsWith("px") || s.EndsWith("em") || s.EndsWith("rem")) return s;
            double d;
            if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) return s + "px";
            return s;
        }

        private static string Esc(string s)
        {
            if (s == null) return "";
            return s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
        }
    }
}
