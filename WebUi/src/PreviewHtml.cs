using System;
using System.Text;

namespace RichTextGen
{
    /// <summary>
    /// 预览渲染器：把「开标签 / 闭标签」拆成可配对的 HTML 片段。
    /// 映射表只有一份（TagRegistry.HtmlWrap），这里只做开/闭拆分，避免两处维护。
    /// </summary>
    public static class PreviewHtml
    {
        private const string Marker = "\u0001";

        public static string Open(TagDef def, string value)
        {
            if (def.Resource || def.Legacy)
                return TagRegistry.HtmlWrap(def, value, "", value);   // 浏览器无法还原 → 直接标徽标

            string wrapped = TagRegistry.HtmlWrap(def, value, Marker, value);
            int idx = wrapped.IndexOf(Marker, StringComparison.Ordinal);
            if (idx < 0) return wrapped;

            if (def.Kind == TagKind.Suffix)                            // <br> / <page>：内容在前，标签在后
                return wrapped.Substring(idx + Marker.Length);

            return wrapped.Substring(0, idx);                          // Wrap / Prefix：开片段在前
        }

        public static string Close(TagDef def)
        {
            if (def.Resource || def.Legacy) return "";
            if (def.Kind != TagKind.Wrap) return "";

            string wrapped = TagRegistry.HtmlWrap(def, "", Marker, "");
            int idx = wrapped.IndexOf(Marker, StringComparison.Ordinal);
            if (idx < 0) return "";
            return wrapped.Substring(idx + Marker.Length);
        }
    }
}
