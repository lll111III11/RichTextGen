using System;

namespace RichTextGen
{
    /// <summary>OCR 已拆分为独立应用（桌面 RichTextGen-OCR），主程序只保留兼容占位。</summary>
    internal static class Ocr
    {
        public static string LanguagesJson()
        {
            return "{\"ok\":false,\"list\":[],\"error\":\"OCR 已拆分为独立应用 RichTextGen-OCR\"}";
        }

        public static string Recognize(string path, string langTag, Action<int, string> progress, out string engine)
        {
            engine = "";
            throw new Exception("OCR 已拆分为独立应用：请使用桌面上的「RichTextGen OCR」。");
        }
    }
}
