using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

namespace RichTextGen
{
    /// <summary>AI 接入设置（保存在 %APPDATA%\RichTextGen\ai.json）。</summary>
    internal static class AiConfig
    {
        public static string Provider = "deepseek";
        public static string Endpoint = "https://api.deepseek.com";
        public static string Model = "deepseek-chat";
        public static string Key = "";
        public static bool ServerEnabled = false;
        public static int Port = 18762;
        public static string Token = "";
        public static bool NotifyDone = true;
        public static bool WebSearchEnabled = false;

        // ---- 思考能力 / 上下文 / 记忆 ----
        /// <summary>思考强度：off（关闭思考）/ low / medium / high / max。</summary>
        public static string ReasoningEffort = "high";
        /// <summary>随请求携带的历史消息条数（不含本轮），0 表示不限制（全部带上）。</summary>
        public static int ContextTurns = 0;
        /// <summary>单次回复最大输出 token，0 表示不限制（不传该参数，交给接口取最大值）。</summary>
        public static int MaxTokens = 0;
        /// <summary>是否启用长期记忆。</summary>
        public static bool MemoryEnabled = true;

        /// <summary>保存的 API 配置方案（可多套切换）。</summary>
        public class AiProfile
        {
            public string Name = "";
            public string Provider = "";
            public string Endpoint = "";
            public string Model = "";
            public string Key = "";
        }
        public static List<AiProfile> Profiles = new List<AiProfile>();
        public static string ActiveProfile = "";

        /// <summary>模型预设：名称 / 平台 / 接入口 / 密钥 / 推理档位数。</summary>
        public class AiPreset
        {
            public string Id = "";
            public string Name = "";
            public string Platform = "";
            public string Endpoint = "";
            public string Key = "";
            public int Levels = 4;
        }
        public static List<AiPreset> Presets = new List<AiPreset>();
        public static string ActivePreset = "";

        public static string Dir
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RichTextGen"); }
        }
        public static string FilePath { get { return Path.Combine(Dir, "ai.json"); } }

        public static void Load()
        {
            try
            {
                if (!File.Exists(FilePath)) return;
                FromJson(File.ReadAllText(FilePath, Encoding.UTF8));
            }
            catch { }
        }

        public static void Save()
        {
            try
            {
                Directory.CreateDirectory(Dir);
                StringBuilder sb = new StringBuilder();
                sb.Append("{\"provider\":").Append(J(Provider));
                sb.Append(",\"endpoint\":").Append(J(Endpoint));
                sb.Append(",\"model\":").Append(J(Model));
                sb.Append(",\"key\":").Append(J(Key));
                sb.Append(",\"serverEnabled\":").Append(ServerEnabled ? "true" : "false");
                sb.Append(",\"port\":").Append(Port);
                sb.Append(",\"token\":").Append(J(Token));
                sb.Append(",\"notifyDone\":").Append(NotifyDone ? "true" : "false");
                sb.Append(",\"webSearch\":").Append(WebSearchEnabled ? "true" : "false");
                sb.Append(",\"reasoningEffort\":").Append(J(ReasoningEffort));
                sb.Append(",\"contextTurns\":").Append(ContextTurns);
                sb.Append(",\"maxTokens\":").Append(MaxTokens);
                sb.Append(",\"memoryEnabled\":").Append(MemoryEnabled ? "true" : "false");
                AppendProfiles(sb);
                sb.Append('}');
                File.WriteAllText(FilePath, sb.ToString(), new UTF8Encoding(false));
            }
            catch { }
        }

        /// <summary>密钥掩码：只暴露首尾少量字符，用于界面回显"已保存",不泄露明文。</summary>
        public static string MaskKey(string key)
        {
            key = (key ?? "").Trim();
            if (key.Length == 0) return "";
            if (key.Length <= 8) return new string('•', Math.Max(4, key.Length));
            return key.Substring(0, 3) + "…" + key.Substring(key.Length - 4);
        }

        public static string ToJson()
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("{\"provider\":").Append(J(Provider));
            sb.Append(",\"endpoint\":").Append(J(Endpoint));
            sb.Append(",\"model\":").Append(J(Model));
            sb.Append(",\"key\":").Append(J(Key));
            sb.Append(",\"serverEnabled\":").Append(ServerEnabled ? "true" : "false");
            sb.Append(",\"port\":").Append(Port);
            sb.Append(",\"token\":").Append(J(Token));
            sb.Append(",\"notifyDone\":").Append(NotifyDone ? "true" : "false");
            sb.Append(",\"webSearch\":").Append(WebSearchEnabled ? "true" : "false");
            sb.Append(",\"reasoningEffort\":").Append(J(ReasoningEffort));
            sb.Append(",\"contextTurns\":").Append(ContextTurns);
            sb.Append(",\"maxTokens\":").Append(MaxTokens);
            sb.Append(",\"memoryEnabled\":").Append(MemoryEnabled ? "true" : "false");
            AppendProfiles(sb);
            sb.Append('}');
            return sb.ToString();
        }

        private static void AppendProfiles(StringBuilder sb)
        {
            sb.Append(",\"activeProfile\":").Append(J(ActiveProfile));
            sb.Append(",\"profiles\":[");
            for (int i = 0; i < Profiles.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append("{\"name\":").Append(J(Profiles[i].Name));
                sb.Append(",\"provider\":").Append(J(Profiles[i].Provider));
                sb.Append(",\"endpoint\":").Append(J(Profiles[i].Endpoint));
                sb.Append(",\"model\":").Append(J(Profiles[i].Model));
                sb.Append(",\"key\":").Append(J(Profiles[i].Key)).Append('}');
            }
            sb.Append(']');

            sb.Append(",\"activePreset\":").Append(J(ActivePreset));
            sb.Append(",\"presets\":[");
            for (int i = 0; i < Presets.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append("{\"id\":").Append(J(Presets[i].Id));
                sb.Append(",\"name\":").Append(J(Presets[i].Name));
                sb.Append(",\"platform\":").Append(J(Presets[i].Platform));
                sb.Append(",\"endpoint\":").Append(J(Presets[i].Endpoint));
                sb.Append(",\"key\":").Append(J(Presets[i].Key));
                sb.Append(",\"levels\":").Append(Presets[i].Levels).Append('}');
            }
            sb.Append(']');
        }

        public static void FromJson(string json)
        {
            string v;
            v = JStr(json, "provider"); if (v != null) Provider = v;
            v = JStr(json, "endpoint"); if (v != null) Endpoint = v;
            v = JStr(json, "model"); if (v != null) Model = v;
            v = JStr(json, "key"); if (v != null) Key = v;
            v = JStr(json, "token"); if (v != null) Token = v;
            bool b;
            if (JBool(json, "serverEnabled", out b)) ServerEnabled = b;
            if (JBool(json, "notifyDone", out b)) NotifyDone = b;
            if (JBool(json, "webSearch", out b)) WebSearchEnabled = b;
            if (JBool(json, "memoryEnabled", out b)) MemoryEnabled = b;

            v = JStr(json, "activeProfile"); if (v != null) ActiveProfile = v;
            string profs = JRaw(json, "profiles");
            if (profs.Length > 0)
            {
                Profiles.Clear();
                foreach (string obj in MainForm.SplitTopLevelObjects(profs))
                {
                    AiProfile p = new AiProfile();
                    p.Name = JStr(obj, "name") ?? "";
                    p.Provider = JStr(obj, "provider") ?? "";
                    p.Endpoint = JStr(obj, "endpoint") ?? "";
                    p.Model = JStr(obj, "model") ?? "";
                    p.Key = JStr(obj, "key") ?? "";
                    if (p.Name.Length > 0) Profiles.Add(p);
                }
            }

            v = JStr(json, "activePreset"); if (v != null) ActivePreset = v;
            string pre = JRaw(json, "presets");
            if (pre.Length > 0)
            {
                Presets.Clear();
                foreach (string obj in MainForm.SplitTopLevelObjects(pre))
                {
                    AiPreset p = new AiPreset();
                    p.Id = JStr(obj, "id") ?? "";
                    p.Name = JStr(obj, "name") ?? "";
                    p.Platform = JStr(obj, "platform") ?? "";
                    p.Endpoint = JStr(obj, "endpoint") ?? "";
                    p.Key = JStr(obj, "key") ?? "";
                    int lv;
                    if (JInt(obj, "levels", out lv) && lv >= 2 && lv <= 5) p.Levels = lv;
                    if (p.Name.Length > 0) Presets.Add(p);
                }
            }

            int n;
            if (JInt(json, "port", out n) && n > 0 && n < 65536) Port = n;
            v = JStr(json, "reasoningEffort");
            if (v != null)
            {
                v = v.Trim().ToLowerInvariant();
                if (v == "off" || v == "low" || v == "medium" || v == "high" || v == "max") ReasoningEffort = v;
            }
        }

        // ---------------- 极简 JSON 读取 ----------------
        public static string JStr(string json, string key)
        {
            Match m = Regex.Match(json ?? "", "\"" + Regex.Escape(key) + "\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
            if (!m.Success) return null;
            try { return Regex.Unescape(m.Groups[1].Value); } catch { return m.Groups[1].Value; }
        }

        public static bool JBool(string json, string key, out bool value)
        {
            value = false;
            Match m = Regex.Match(json ?? "", "\"" + Regex.Escape(key) + "\"\\s*:\\s*(true|false)");
            if (!m.Success) return false;
            value = m.Groups[1].Value == "true";
            return true;
        }

        public static bool JInt(string json, string key, out int value)
        {
            value = 0;
            Match m = Regex.Match(json ?? "", "\"" + Regex.Escape(key) + "\"\\s*:\\s*(-?\\d+)");
            return m.Success && int.TryParse(m.Groups[1].Value, out value);
        }

        /// <summary>取出一个完整的 JSON 对象/数组原文（用于 actions）。</summary>
        public static string JRaw(string json, string key)
        {
            Match m = Regex.Match(json ?? "", "\"" + Regex.Escape(key) + "\"\\s*:");
            if (!m.Success) return "";
            int i = m.Index + m.Length;
            while (i < json.Length && char.IsWhiteSpace(json[i])) i++;
            if (i >= json.Length) return "";
            char open = json[i];

            // 值本身是一个 JSON 字符串（例如独立窗口把 history 用 JSON.stringify 成字符串发过来）。
            // 旧实现在这里直接返回空串，导致历史消息被静默丢弃——模型因此"记不住"上下文。
            // 这里取出字符串内容并反转义，返回其内部文本（本身就是一段 JSON），交给调用方解析。
            if (open == '"')
            {
                StringBuilder sb = new StringBuilder();
                int j = i + 1;
                while (j < json.Length)
                {
                    char ch = json[j];
                    if (ch == '\\' && j + 1 < json.Length)
                    {
                        char nx = json[j + 1];
                        if (nx == 'u' && j + 5 < json.Length)
                        {
                            int code;
                            if (int.TryParse(json.Substring(j + 2, 4),
                                    System.Globalization.NumberStyles.HexNumber, null, out code))
                            {
                                sb.Append((char)code);
                                j += 6;
                                continue;
                            }
                        }
                        switch (nx)
                        {
                            case 'n': sb.Append('\n'); break;
                            case 'r': sb.Append('\r'); break;
                            case 't': sb.Append('\t'); break;
                            case 'b': sb.Append('\b'); break;
                            case 'f': sb.Append('\f'); break;
                            default: sb.Append(nx); break;      // \" \\ \/ 等
                        }
                        j += 2;
                        continue;
                    }
                    if (ch == '"') break;
                    sb.Append(ch);
                    j++;
                }
                return sb.ToString();
            }

            char close = open == '[' ? ']' : (open == '{' ? '}' : '\0');
            if (close == '\0') return "";
            int depth = 0;
            bool inStr = false, esc = false;
            for (int j = i; j < json.Length; j++)
            {
                char ch = json[j];
                if (inStr)
                {
                    if (esc) esc = false;
                    else if (ch == '\\') esc = true;
                    else if (ch == '"') inStr = false;
                    continue;
                }
                if (ch == '"') { inStr = true; continue; }
                if (ch == open) depth++;
                else if (ch == close)
                {
                    depth--;
                    if (depth == 0) return json.Substring(i, j - i + 1);
                }
            }
            return "";
        }

        public static string J(string s)
        {
            if (s == null) s = "";
            StringBuilder sb = new StringBuilder(s.Length + 16);
            sb.Append('"');
            for (int i = 0; i < s.Length; i++)
            {
                char ch = s[i];
                switch (ch)
                {
                    case '\\': sb.Append("\\\\"); break;
                    case '"': sb.Append("\\\""); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (ch < ' ') sb.Append("\\u").Append(((int)ch).ToString("x4"));
                        else sb.Append(ch);
                        break;
                }
            }
            sb.Append('"');
            return sb.ToString();
        }
    }

    /// <summary>
    /// AI 长期记忆（保存在 %APPDATA%\RichTextGen\ai-memory.json）。
    /// note = AI 主动记住的偏好/约定/事实（也会在下次会话注入 system prompt）；
    /// task = 每次任务完成后自动追加的简短日志（仅本地留档，注入时只取最近几条）。
    /// </summary>
    internal static class AiMemory
    {
        internal class Item
        {
            public string Id = "";
            public string Kind = "note";
            public string Text = "";
            public string At = "";
        }

        private static readonly List<Item> _items = new List<Item>();
        private static readonly object _lock = new object();
        private static bool _loaded;
        private const int MaxNotes = 80;
        private const int MaxTasks = 20;

        public static string FilePath
        {
            get { return Path.Combine(AiConfig.Dir, "ai-memory.json"); }
        }

        public static void Load()
        {
            lock (_lock)
            {
                _items.Clear();
                _loaded = true;
                try
                {
                    if (!File.Exists(FilePath)) return;
                    string json = File.ReadAllText(FilePath, Encoding.UTF8);
                    string arr = AiConfig.JRaw(json, "items");
                    foreach (string obj in MainForm.SplitTopLevelObjects(arr))
                    {
                        Item it = new Item();
                        it.Id = AiConfig.JStr(obj, "id") ?? "";
                        it.Kind = AiConfig.JStr(obj, "kind") ?? "note";
                        it.Text = AiConfig.JStr(obj, "text") ?? "";
                        it.At = AiConfig.JStr(obj, "at") ?? "";
                        if (it.Text.Length == 0) continue;
                        if (it.Id.Length == 0) it.Id = Guid.NewGuid().ToString("N");
                        _items.Add(it);
                    }
                }
                catch { }
            }
        }

        private static void Ensure()
        {
            if (!_loaded) Load();
        }

        public static int Count
        {
            get { lock (_lock) { Ensure(); return _items.Count; } }
        }

        public static void Save()
        {
            lock (_lock)
            {
                try
                {
                    Directory.CreateDirectory(AiConfig.Dir);
                    StringBuilder sb = new StringBuilder();
                    sb.Append("{\"items\":[");
                    for (int i = 0; i < _items.Count; i++)
                    {
                        if (i > 0) sb.Append(',');
                        sb.Append("{\"id\":").Append(AiConfig.J(_items[i].Id));
                        sb.Append(",\"kind\":").Append(AiConfig.J(_items[i].Kind));
                        sb.Append(",\"text\":").Append(AiConfig.J(_items[i].Text));
                        sb.Append(",\"at\":").Append(AiConfig.J(_items[i].At)).Append('}');
                    }
                    sb.Append("]}");
                    File.WriteAllText(FilePath, sb.ToString(), new UTF8Encoding(false));
                }
                catch { }
            }
        }

        /// <summary>新增一条记忆（完全重复的会被忽略）。</summary>
        public static void Add(string text, string kind)
        {
            lock (_lock)
            {
                Ensure();
                text = (text ?? "").Trim();
                if (text.Length == 0) return;
                if (text.Length > 1000) text = text.Substring(0, 1000);
                foreach (Item it in _items)
                    if (string.Equals(it.Text, text, StringComparison.Ordinal)) return;
                _items.Add(new Item
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Kind = kind == "task" ? "task" : "note",
                    Text = text,
                    At = DateTime.Now.ToString("yyyy-MM-dd HH:mm")
                });
                Trim();
                Save();
            }
        }

        private static void Trim()
        {
            int notes = 0, tasks = 0;
            for (int i = _items.Count - 1; i >= 0; i--)
            {
                if (_items[i].Kind == "task")
                {
                    tasks++;
                    if (tasks > MaxTasks) _items.RemoveAt(i);
                }
                else
                {
                    notes++;
                    if (notes > MaxNotes) _items.RemoveAt(i);
                }
            }
        }

        public static bool Remove(string id)
        {
            lock (_lock)
            {
                Ensure();
                id = (id ?? "").Trim();
                for (int i = 0; i < _items.Count; i++)
                {
                    if (_items[i].Id == id) { _items.RemoveAt(i); Save(); return true; }
                }
                return false;
            }
        }

        public static void Clear()
        {
            lock (_lock)
            {
                _items.Clear();
                _loaded = true;
                Save();
            }
        }

        public static string ToJson()
        {
            lock (_lock)
            {
                Ensure();
                StringBuilder sb = new StringBuilder();
                sb.Append("{\"ok\":true,\"enabled\":").Append(AiConfig.MemoryEnabled ? "true" : "false");
                sb.Append(",\"items\":[");
                for (int i = 0; i < _items.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append("{\"id\":").Append(AiConfig.J(_items[i].Id));
                    sb.Append(",\"kind\":").Append(AiConfig.J(_items[i].Kind));
                    sb.Append(",\"text\":").Append(AiConfig.J(_items[i].Text));
                    sb.Append(",\"at\":").Append(AiConfig.J(_items[i].At)).Append('}');
                }
                sb.Append("]}");
                return sb.ToString();
            }
        }

        /// <summary>拼进 system prompt 的记忆段落；没有记忆时返回空串。</summary>
        public static string PromptText()
        {
            lock (_lock)
            {
                Ensure();
                if (_items.Count == 0) return "";
                StringBuilder notes = new StringBuilder();
                StringBuilder tasks = new StringBuilder();
                int nc = 0;
                foreach (Item it in _items)
                {
                    if (it.Kind == "task") continue;
                    if (nc >= 40) break;
                    nc++;
                    string t = it.Text.Length > 300 ? it.Text.Substring(0, 300) + "…" : it.Text;
                    notes.Append("- ").Append(t).Append('\n');
                }
                int tc = 0;
                for (int i = _items.Count - 1; i >= 0 && tc < 5; i--)
                {
                    if (_items[i].Kind != "task") continue;
                    tc++;
                    string t = _items[i].Text;
                    if (t.Length > 200) t = t.Substring(0, 200) + "…";
                    tasks.Append("- [").Append(_items[i].At).Append("] ").Append(t).Append('\n');
                }
                if (notes.Length == 0 && tasks.Length == 0) return "";
                StringBuilder sb = new StringBuilder();
                if (notes.Length > 0)
                    sb.Append("【长期记忆】以下是用户要求你长期记住的偏好、约定与事实，回答和操作时必须遵守；")
                      .Append("不要向用户复述这些条目，也不要说“根据我的记忆”。\n").Append(notes);
                if (tasks.Length > 0)
                    sb.Append("\n【历史会话记录】以下是你在**更早的、别的会话**里处理过的任务日志，只供了解背景。")
                      .Append("它们**不属于当前这次对话**：用户问“我最开始/第一句说了什么”“我们刚才聊了什么”时，")
                      .Append("必须只依据本次对话的真实消息，绝不能拿这里的记录当成本次对话的内容。\n").Append(tasks);
                return sb.ToString();
            }
        }

        /// <summary>任务结束后自动留一条简短日志（不消耗模型调用）。</summary>
        public static void LogTask(string user, string result)
        {
            if (!AiConfig.MemoryEnabled) return;
            string u = (user ?? "").Trim().Replace("\r", " ").Replace("\n", " ");
            string r = (result ?? "").Trim().Replace("\r", " ").Replace("\n", " ");
            if (u.Length > 60) u = u.Substring(0, 60) + "…";
            if (r.Length > 120) r = r.Substring(0, 120) + "…";
            if (u.Length == 0) return;
            Add("用户要求：" + u + (r.Length > 0 ? " → 已处理：" + r : ""), "task");
        }
    }

    internal class AgentResult
    {
        public bool Ok;
        public string Content = "";
        public string Error = "";
        public string StepsJson = "[]";
        public int StepCount;
    }

    /// <summary>
    /// AI 接入（Beta）：
    /// 1) 多厂商 OpenAI 兼容对话，返回可执行的编辑器操作；
    /// 2) 本地端口服务，供外部 AI / 客户端调用；
    /// 3) 操作回桥：服务线程 → UI 线程执行 → 返回结果。
    /// </summary>
    public partial class MainForm
    {
        private string _aiState = "{}";
        private readonly Dictionary<string, object[]> _aiWaiters = new Dictionary<string, object[]>();

        // ============================================================ 设置
        public string AiLoadSettings()
        {
            AiConfig.Load();
            return AiConfig.ToJson();
        }

        public void AiSaveSettings(string json)
        {
            // 密钥保护：主窗口的密钥框可能为空（未加载完成、切换配置方案、或只改了
            // 联网搜索/思考强度等其它开关就触发保存），此时绝不能把已保存的密钥清掉。
            // 空 key 一律视为"不修改"，与 /settings 接口的语义保持一致。
            string keepKey = AiConfig.Key;
            AiConfig.FromJson(json);
            if (string.IsNullOrEmpty(AiConfig.Key)) AiConfig.Key = keepKey;
            AiConfig.Save();
            CallStr("aiSaved", "AI 设置已保存");
        }

        // ============================================================ 长期记忆
        public string AiMemoryLoad()
        {
            AiMemory.Load();
            return AiMemory.ToJson();
        }

        /// <summary>界面手动添加（json 形如 {"text":"..."}）。</summary>
        public string AiMemoryAdd(string json)
        {
            AiMemory.Load();
            string text = AiConfig.JStr(json ?? "", "text");
            if (string.IsNullOrEmpty(text)) text = (json ?? "").Trim();
            AiMemory.Add(text, "note");
            return AiMemory.ToJson();
        }

        public string AiMemoryRemove(string id)
        {
            AiMemory.Remove(id);
            return AiMemory.ToJson();
        }

        public string AiMemoryClear()
        {
            AiMemory.Clear();
            return AiMemory.ToJson();
        }

        /// <summary>
        /// 手动「压缩长期记忆」：把当前对话交给模型提取用户偏好/约定/事实，逐条写入长期记忆。
        /// 返回新增条数。
        /// </summary>
        internal static int AiCompressMemory(string messagesJson)
        {
            try
            {
                StringBuilder sb = new StringBuilder();
                foreach (string obj in SplitTopLevelObjects(messagesJson))
                {
                    string role = AiConfig.JStr(obj, "role") ?? "";
                    string content = AiConfig.JStr(obj, "content") ?? "";
                    if (content.Length == 0) continue;
                    sb.Append(role == "user" ? "用户：" : "助手：").Append(content).Append('\n');
                }
                string text = sb.ToString().Trim();
                if (text.Length == 0) return 0;
                if (text.Length > 24000) text = text.Substring(text.Length - 24000);

                string sys = "你是长期记忆整理器。从对话中提取值得长期记住的用户偏好、习惯、项目约定和重要事实。"
                    + "每行一条，最多 10 条；不要编号、不要解释、不要重复；没有可记的就输出空字符串。";
                string body = "{\"model\":" + Quote(AiConfig.Model) +
                              ",\"messages\":[{\"role\":\"system\",\"content\":" + Quote(sys) +
                              "},{\"role\":\"user\",\"content\":" + Quote("对话记录：\n" + text + "\n\n请提取。") + "}]" +
                              ",\"max_tokens\":800}";
                string resp = HttpPost(NormalizeEndpoint(AiConfig.Endpoint), (AiConfig.Key ?? "").Trim(), body, 120000);
                string content2 = ExtractContent(resp).Trim();
                if (content2.Length == 0) return 0;

                int added = 0;
                foreach (string raw in content2.Split('\n'))
                {
                    string s = raw.Trim().TrimStart('-', '·', '•', '*', ' ', '\t');
                    if (s.Length < 4) continue;
                    int before = AiMemory.Count;
                    AiMemory.Add(s, "note");
                    if (AiMemory.Count > before) added++;
                }
                return added;
            }
            catch { return 0; }
        }

        // ============================================================ 任务 / 会话
        private static string ConvsPath
        {
            get { return Path.Combine(AiConfig.Dir, "convs.json"); }
        }

        public string AiConvsLoad()
        {
            try
            {
                if (!File.Exists(ConvsPath)) return "{\"list\":[]}";
                string s = File.ReadAllText(ConvsPath, Encoding.UTF8);
                return string.IsNullOrEmpty(s) ? "{\"list\":[]}" : s;
            }
            catch { return "{\"list\":[]}"; }
        }

        public void AiConvsSave(string json)
        {
            try
            {
                if (string.IsNullOrEmpty(json) || !json.TrimStart().StartsWith("{")) return;
                Directory.CreateDirectory(AiConfig.Dir);
                File.WriteAllText(ConvsPath, json, new UTF8Encoding(false));
            }
            catch { }
        }

        // ============================================================ 状态同步
        public string GetAiState() { return string.IsNullOrEmpty(_aiState) ? "{}" : _aiState; }

        private void SetAiState(string json)
        {
            if (!string.IsNullOrEmpty(json) && json.TrimStart().StartsWith("{")) _aiState = json;
        }

        // ============================================================ 界面聊天（异步，多轮 Agent）
        /// <summary>JS 发起：带历史上下文的多轮对话 + 连续工具调用，最终回调 ui.aiReply。</summary>
        public void AiChat(string json)
        {
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    string message = Field(json, "message");
                    string state = Field(json, "state");
                    string history = Field(json, "history");
                    string endpoint = AiConfig.Endpoint;
                    string key = AiConfig.Key;
                    string model = AiConfig.Model;
                    string provider = AiConfig.Provider;

                    string tmp = Field(json, "endpoint"); if (tmp.Length > 0) endpoint = tmp;
                    tmp = Field(json, "key"); if (tmp.Length > 0) key = tmp;
                    tmp = Field(json, "model"); if (tmp.Length > 0) model = tmp;
                    tmp = Field(json, "provider"); if (tmp.Length > 0) provider = tmp;

                    AgentResult r = RunAgent(endpoint, key, model, provider, message,
                        string.IsNullOrEmpty(state) ? _aiState : state, history, "");
                    if (!r.Ok) { Call("aiReply", "{\"ok\":false,\"content\":" + Quote(r.Error) + "}"); return; }
                    Call("aiReply", "{\"ok\":true,\"content\":" + Quote(r.Content) + ",\"steps\":" + r.StepsJson + "}");
                    CallStr("status", "AI 已完成（Beta）");

                    int doneSteps = r.StepCount;
                    string doneText = r.Content;
                    ThreadPool.QueueUserWorkItem(delegate
                    {
                        System.Threading.Thread.Sleep(600);   // 先让对话渲染完，再弹窗
                        NotifyAiDone(doneText, doneSteps);
                    });
                }
                catch (Exception ex)
                {
                    Call("aiReply", "{\"ok\":false,\"content\":" + Quote("请求出错：" + ex.Message) + "}");
                }
            });
        }

        /// <summary>端口服务调用：同步跑完整 Agent 循环（含多轮工具执行与可选历史）。</summary>
        internal AgentResult AiChatSync(string message, string history, string images)
        {
            AgentResult r = RunAgent(AiConfig.Endpoint, AiConfig.Key, AiConfig.Model, AiConfig.Provider,
                message, _aiState, history ?? "", images ?? "");
            if (r.Ok) NotifyAiDone(r.Content, r.StepCount);
            return r;
        }

        // ============================================================ 模型请求

        /// <summary>把当前思考强度档位翻成中文，供模型自述与界面显示。</summary>
        private static string EffortText(string effort)
        {
            switch ((effort ?? "").Trim().ToLowerInvariant())
            {
                case "off": return "关闭（不启用思考，走普通采样）";
                case "low": return "低";
                case "medium": return "中";
                case "high": return "高";
                case "max": return "最高";
                default: return "未设置";
            }
        }

        /// <summary>
        /// 该模型是否（很可能）支持思考参数。
        /// DeepSeek 当前主推的 deepseek-flash 默认就开思考模式，因此归为支持；
        /// 其余平台也按支持处理，实在不支持时接口会忽略该参数，由用户自行换模型。
        /// </summary>
        private static bool ModelSupportsThinking(string provider, string model)
        {
            string p = (provider ?? "").Trim().ToLowerInvariant();
            string m = (model ?? "").Trim().ToLowerInvariant();
            if (p == "deepseek") return true;                 // flash 默认开启思考模式
            if (p == "moonshot")
                return m.Contains("k3") || m.Contains("k2.6") || m.Contains("k2.7") || m.Contains("k2-");
            return true;
        }

        /// <summary>
        /// 该模型是否（很可能）支持图片输入。名称启发式；不确定时按不支持处理并提示用户，
        /// 这样模型不会声称自己看到了其实读不到的图。
        /// </summary>
        private static bool ModelSupportsVision(string provider, string model)
        {
            string m = (model ?? "").Trim().ToLowerInvariant();
            // 按模型名判断，不依赖厂商字段（用户常用「自定义」接入同一批模型）
            if (m.Contains("deepseek"))
                return !m.Contains("v4-pro");                 // deepseek-flash 支持图片；v4-pro 不支持
            string[] keys = { "vl", "vision", "gpt-4o", "gpt-4.1", "gpt-5", "glm-4v",
                              "gemini", "claude-3", "claude-4", "internvl", "minicpm-v" };
            foreach (string k in keys) if (m.Contains(k)) return true;
            return false;
        }

        /// <summary>
        /// 会话起点锚点：把最早几条用户消息固定写进系统提示。
        /// 这样即使上下文被压缩过，模型依然能准确回答"我最开始说了什么"这类定位问题。
        /// </summary>
        private static string BuildHistoryAnchor(string history)
        {
            if (string.IsNullOrEmpty(history)) return "";
            string h = history.Trim();
            if (!(h.StartsWith("[") && h.EndsWith("]"))) return "";

            List<string> objs = SplitTopLevelObjects(h.Substring(1, h.Length - 2));
            if (objs.Count == 0) return "";

            List<string> firstUsers = new List<string>();
            foreach (string obj in objs)
            {
                string role = AiConfig.JStr(obj, "role") ?? "";
                if (role != "user") continue;
                if (firstUsers.Count >= 3) break;
                string c = (AiConfig.JStr(obj, "content") ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
                if (c.Length == 0) continue;
                if (c.Length > 180) c = c.Substring(0, 180) + "…";
                firstUsers.Add(c);
            }
            if (firstUsers.Count == 0) return "";

            StringBuilder sb = new StringBuilder();
            sb.Append("本次会话的起点信息（必须记住；用户问“我最开始/第一句/一开始说了什么”时以此为准，不要猜）：\n");
            sb.Append("· 目前本次会话累计 ").Append(objs.Count).Append(" 条消息（含助手回复）。\n");
            sb.Append("· 最早的用户消息依次是：");
            for (int i = 0; i < firstUsers.Count; i++)
                sb.Append("\n  ").Append(i + 1).Append(") ").Append(firstUsers[i]);
            sb.Append('\n');
            return sb.ToString();
        }

        private static string BuildSystemPrompt(string state, string provider, string model, bool webSearch,
                                                string memory, string anchor)
        {
            string who = string.IsNullOrEmpty(provider) ? "你当前接入的 AI 服务商" : provider;
            string mdl = string.IsNullOrEmpty(model) ? "" : "（模型：" + model + "）";
            bool canThink = ModelSupportsThinking(provider, model);
            bool canSee = ModelSupportsVision(provider, model);
            return
                "你是一个通用 AI 助手" + mdl + "，由 " + who + " 提供。\n" +
                "你现在运行在「彩色文本生成器」这个工具里，可以通过 apply_editor_actions 直接操作编辑器。\n" +
                "身份要求：当用户问你是谁、你是什么模型、来自哪个平台时，如实说明你的真实模型与提供方；" +
                "不要自称是「彩色文本生成器」自研的模型，也不要把工具名当成你的身份。\n" +
                "自身配置自述（被问到思考强度、推理档位、能否看图时，按下述事实回答，不要一律否认）：\n" +
                "· 当前思考强度档位：由本工具设置为「" + EffortText(AiConfig.ReasoningEffort) + "」。" +
                (canThink
                    ? "你的模型确实支持思考参数，档位会真实生效；注意 DeepSeek 只区分 high 与 max 两档，" +
                      "界面上的「低 / 中」会被接口折叠成 high，「最高」才是 max，如实说明即可。"
                    : "但当前模型名看起来不支持思考参数，该档位可能被接口忽略、不产生实际效果，请如实说明这一点。") + "\n" +
                "· 图片输入（视觉）：" + (canSee
                    ? "当前模型支持，用户可以贴图、拖入图片或截图后粘贴给你看；收到图片要真正描述画面内容，不要假装没看到。"
                    : "当前模型不支持图片输入，用户发图你读不到画面，只能读文字；" +
                      "如需看图，建议用户换成支持视觉的模型（如 deepseek-flash、qwen-vl、gpt-4o、glm-4v 等）。") + "\n" +
                "· 上下文：本工具默认把全部历史消息都发给你（不做人为截断）；只在拼接长度过大时才由系统自动压缩，"
                + "压缩后本次会话的早期内容会以「会话摘要 + 起点信息」的形式保留。\n" +
                "· 不确定的事直接说不确定，不要编造。回答「我最开始说了什么」这类问题时，以本提示下方的"
                + "「会话起点信息」为准。\n" +
                "工作方式：\n" +
                (webSearch ? "0. 需要最新信息、资料、事实核查或用户明确要求搜索时，先调用 web_search 联网搜索，再基于搜索结果回答或操作。\n" : "") +
                "1. 你可以连续多轮调用工具，直到任务完成；轮数不设上限，上下文过长时系统会自动压缩，不用担心中断。\n" +
                "2. actions 数组一次可以放任意多个操作（几十个也可以），不要一次只放一个、更不要一段一段挤。\n" +
                "3. 写长文/整首诗/整段代码时，直接一次 setText 写完整；确实超出输出长度时再用 appendText 续写。\n" +
                "4. 多轮修改后想确认结果，调用 read_state 读取最新状态，不要靠猜。\n" +
                "5. 最终回复要一次性写完整，不要半截、不要省略正文。\n" +
                "6. 用户要求写文章/诗/短句时，必须把完整内容放进 actions 的 setText；只在 reply 里说“已写入”是无效的。\n" +
                "7. 用户要求改标题时用 setTitle。\n" +
                "8. 当用户表达长期偏好、项目约定、固定习惯，或明确说“记住”时，调用 remember 写入长期记忆；" +
                "只记稳定、可复用的事实，不要记录一次性的临时内容。\n\n" +
                (string.IsNullOrEmpty(anchor) ? "" : anchor + "\n") +
                (string.IsNullOrEmpty(memory) ? "" : memory + "\n") +
                "当前编辑器状态 JSON：\n" + (string.IsNullOrEmpty(state) ? "{}" : state) + "\n\n" +
                "如果接口不支持函数调用，请把操作写在 <actions> 与 </actions> 之间（或者只返回一个 JSON 对象，不要 Markdown 围栏）：\n" +
                "<actions>[{\"type\":\"setText\",\"text\":\"...\"}]</actions>\n" +
                "或 {\"reply\":\"给用户的简短说明\",\"actions\":[...]}\n" +
                "不要输出 ＜｜｜DSML｜｜＞ 之类的内部标记；工具调用请使用函数调用，或上面的 <actions> 格式。\n" +
                "可用 actions：\n" +
                "- 文本：{\"type\":\"setText\",\"text\":\"...\"} / appendText / insertAtCursor / replaceSelection / clearText / selectAll / setSelection（from、to）\n" +
                "- 文档：{\"type\":\"setTitle\",\"title\":\"...\"} / newTab（可带 title、text）/ switchTab（name）/ closeTab（name）/ save / saveAs\n" +
                "- 导出：exportTxt / exportHtml / copyText / copyOutput\n" +
                "- 样式：setColor（color）/ setMode（mode）/ setStyle（bold 等）/ setSize（size）/ setAlpha（alpha）/ setGradient / setLink\n" +
                "- 其他：setTheme（dark|light）/ openUrl（url）/ goPage（work|chat|img|trans|set）/ openFile\n" +
                "另外 actions 里也可以放：{\"type\":\"remember\",\"text\":\"要记住的事\"}、" +
                "{\"type\":\"read_state\"}、{\"type\":\"web_search\",\"query\":\"关键词\"}\n" +
                "不需要修改时 actions 返回空数组。";
        }

        /// <summary>
        /// 单次任务的工具调用轮数上限。默认不设上限（int.MaxValue）；
        /// 确实需要限制时，直接把这个数值改小即可。
        /// </summary>
        private const int MaxAgentRounds = int.MaxValue;

        /// <summary>
        /// 上下文压缩阈值（字符数）。超过后自动把旧对话压缩成摘要再继续；
        /// 想调整压缩时机就改这里（默认 48000 字符，约 1.2 万 token）。
        /// </summary>
        /// <summary>
        /// 上下文压缩触发阈值（拼接后的 messages 字符数）。
        /// 早先设为 48000 过于保守——几十轮正常对话就会触发，压缩后早期内容被摘要替换，
        /// 用户会明显感到"上下文被截断"。现在放宽到 128000 字符（约 64k tokens），
        /// 只有在确实接近主流模型上下文窗口时才压缩；同时系统提示里始终带「会话起点信息」，
        /// 保证压缩后仍能回答"我最开始说了什么"。
        /// </summary>
        private const int CompressAtChars = 128000;

        private static string ToolsJson(bool webSearch)
        {
            string editor =
                "{\"type\":\"function\",\"function\":{\"name\":\"apply_editor_actions\"," +
                "\"description\":\"操作彩色文本生成器。actions 是一个数组，可以一次放任意多个操作（几十个也可以），不要一次只放一个；需要写长文时直接一次 setText 写完整。\"," +
                "\"parameters\":{\"type\":\"object\",\"properties\":{" +
                "\"reply\":{\"type\":\"string\",\"description\":\"给用户的简短说明\"}," +
                "\"actions\":{\"type\":\"array\",\"description\":\"操作列表，可一次放多个\",\"items\":{\"type\":\"object\",\"properties\":{" +
                "\"type\":{\"type\":\"string\",\"enum\":[" +
                "\"setText\",\"appendText\",\"insertAtCursor\",\"replaceSelection\",\"clearText\",\"selectAll\",\"setSelection\"," +
                "\"setTitle\",\"newTab\",\"switchTab\",\"closeTab\",\"save\",\"saveAs\"," +
                "\"exportTxt\",\"exportHtml\",\"copyText\",\"copyOutput\"," +
                "\"setColor\",\"setMode\",\"setStyle\",\"setSize\",\"setAlpha\",\"setGradient\",\"setLink\"," +
                "\"setTheme\",\"openUrl\",\"goPage\",\"openFile\"]}," +
                "\"text\":{\"type\":\"string\"},\"title\":{\"type\":\"string\"},\"name\":{\"type\":\"string\"}," +
                "\"from\":{\"type\":\"integer\"},\"to\":{\"type\":\"integer\"}," +
                "\"color\":{\"type\":\"string\"},\"mode\":{\"type\":\"string\"},\"size\":{\"type\":\"string\"}," +
                "\"alpha\":{\"type\":\"string\"},\"url\":{\"type\":\"string\"},\"page\":{\"type\":\"string\"}," +
                "\"theme\":{\"type\":\"string\"}," +
                "\"bold\":{\"type\":\"boolean\"},\"italic\":{\"type\":\"boolean\"},\"underline\":{\"type\":\"boolean\"}," +
                "\"strike\":{\"type\":\"boolean\"},\"sub\":{\"type\":\"boolean\"},\"sup\":{\"type\":\"boolean\"}," +
                "\"mark\":{\"type\":\"boolean\"},\"enabled\":{\"type\":\"boolean\"},\"axial\":{\"type\":\"boolean\"}," +
                "\"loop\":{\"type\":\"boolean\"},\"start\":{\"type\":\"string\"},\"end\":{\"type\":\"string\"}" +
                "},\"required\":[\"type\"]}}},\"required\":[\"reply\",\"actions\"]}}}";

            string stateTool =
                "{\"type\":\"function\",\"function\":{\"name\":\"read_state\"," +
                "\"description\":\"读取编辑器当前最新状态（正文、标题、颜色、样式、标签列表、选区）。多轮修改后需要确认结果时调用。\"," +
                "\"parameters\":{\"type\":\"object\",\"properties\":{}}}";

            string search =
                "{\"type\":\"function\",\"function\":{\"name\":\"web_search\"," +
                "\"description\":\"联网搜索网页，返回标题、URL 与摘要。需要最新信息、资料或事实核查时调用。\"," +
                "\"parameters\":{\"type\":\"object\",\"properties\":{" +
                "\"query\":{\"type\":\"string\",\"description\":\"搜索关键词\"}," +
                "\"count\":{\"type\":\"integer\",\"description\":\"返回条数，1-8，默认 5\"}" +
                "},\"required\":[\"query\"]}}}";

            string memory =
                "{\"type\":\"function\",\"function\":{\"name\":\"remember\"," +
                "\"description\":\"把用户明确要求记住的、或长期稳定的偏好/项目约定/重要事实写入长期记忆，下次会话会自动加载。" +
                "只记稳定可复用的事实，不要记一次性任务内容。\"," +
                "\"parameters\":{\"type\":\"object\",\"properties\":{" +
                "\"text\":{\"type\":\"string\",\"description\":\"要记住的内容，一句话讲清楚\"}" +
                "},\"required\":[\"text\"]}}}";

            return ",\"tools\":[" + editor + "," + stateTool + "," + memory +
                   (webSearch ? "," + search : "") + "],\"tool_choice\":\"auto\"";
        }

        /// <summary>从 JSON 字符串数组里取出所有元素（元素是 base64 data URL，不含转义引号）。</summary>
        private static List<string> JsonStringArray(string json)
        {
            List<string> list = new List<string>();
            if (string.IsNullOrEmpty(json)) return list;
            int i = 0;
            while (i < json.Length)
            {
                if (json[i] != '"') { i++; continue; }
                int j = i + 1;
                StringBuilder sb = new StringBuilder();
                while (j < json.Length)
                {
                    char c = json[j];
                    if (c == '\\' && j + 1 < json.Length) { sb.Append(json[j + 1]); j += 2; continue; }
                    if (c == '"') break;
                    sb.Append(c);
                    j++;
                }
                if (sb.Length > 0) list.Add(sb.ToString());
                i = j + 1;
            }
            return list;
        }

        /// <summary>
        /// 组装本轮 user 消息的 content。
        /// 无图片时是纯字符串；有图片时按 OpenAI 兼容的多模态数组下发：
        /// [{"type":"text","text":...},{"type":"image_url","image_url":{"url":"data:image/png;base64,..."}}]
        /// </summary>
        private static string BuildUserContent(string message, string images)
        {
            List<string> imgs = JsonStringArray(images);
            if (imgs.Count == 0) return Quote(message);

            StringBuilder sb = new StringBuilder();
            sb.Append("[{\"type\":\"text\",\"text\":").Append(Quote(message)).Append('}');
            foreach (string u in imgs)
            {
                if (u.Length < 16 || u.IndexOf("data:image", StringComparison.OrdinalIgnoreCase) != 0) continue;
                sb.Append(",{\"type\":\"image_url\",\"image_url\":{\"url\":").Append(Quote(u)).Append("}}");
            }
            sb.Append(']');
            return sb.ToString();
        }

        /// <summary>多轮 Agent：模型可连续调用工具，执行结果回传后继续思考，直到给出最终回复。</summary>
        private AgentResult RunAgent(string endpoint, string key, string model, string provider,
                                     string message, string state, string history, string images)
        {
            AgentResult r = new AgentResult();
            if (string.IsNullOrEmpty((message ?? "").Trim()))
            {
                r.Error = "请先输入内容";
                return r;
            }
            if (string.IsNullOrEmpty((key ?? "").Trim()))
            {
                r.Error = "尚未填写 API Key（在「AI 接入」页填入并保存）";
                return r;
            }
            if (string.IsNullOrEmpty((endpoint ?? "").Trim())) endpoint = "https://api.deepseek.com";
            if (string.IsNullOrEmpty((model ?? "").Trim())) model = "deepseek-chat";
            endpoint = NormalizeEndpoint(endpoint);

            // system + 历史上下文 + 本轮 user
            string memoryText = AiConfig.MemoryEnabled ? AiMemory.PromptText() : "";
            // 会话起点锚点：无论上下文是否被压缩，模型都能答出"我最开始说了什么"
            string anchor = BuildHistoryAnchor(history);
            string sysPrompt = BuildSystemPrompt(state, provider, model, AiConfig.WebSearchEnabled, memoryText, anchor);
            string messages = "[{\"role\":\"system\",\"content\":" + Quote(sysPrompt) + "}";
            if (!string.IsNullOrEmpty(history))
            {
                string h = history.Trim();
                if (h.StartsWith("[") && h.EndsWith("]"))
                {
                    string inner = h.Substring(1, h.Length - 2).Trim();
                    if (inner.Length > 0) messages += "," + inner;
                }
            }
            messages += ",{\"role\":\"user\",\"content\":" + BuildUserContent(message, images) + "}]";

            // 任务开始前让前端保存一份快照，供通知卡片上的「撤销」使用
            Call("aiCaptureSnapshot", "{}");

            string tools = ToolsJson(AiConfig.WebSearchEnabled);
            bool toolsAvailable = true;
            bool thinkingOn = !string.IsNullOrEmpty(AiConfig.ReasoningEffort) &&
                              AiConfig.ReasoningEffort != "off";
            int maxTokens = AiConfig.MaxTokens;   // 0 = 不限制，请求里不传 max_tokens
            if (maxTokens > 0)
            {
                if (maxTokens < 512) maxTokens = 512;
                // Kimi 思考模型官方建议 max_tokens ≥ 16000，否则 reasoning_content 会被截断
                if (thinkingOn && model.IndexOf("kimi", StringComparison.OrdinalIgnoreCase) >= 0 && maxTokens < 16000)
                    maxTokens = 16000;
            }
            string lastContent = "";
            string postErr = "";
            int textActionRounds = 0;   // 纯文本 actions 的兜底执行轮数上限
            int selfCorrects = 0;       // 模型"只说不做"时的纠正次数上限
            string rollingSummary = ""; // 会话压缩摘要（每轮累积，不从头重生成）
            List<string> steps = new List<string>();

            for (int round = 0; round < MaxAgentRounds; round++)
            {
                // 上下文过长 → 先把旧对话压成结构化摘要，再继续，避免撞上下文上限
                if (messages.Length > CompressAtChars)
                {
                    string sum = CompressContext(endpoint, key, model, messages, rollingSummary);
                    if (sum.Length > 0)
                    {
                        rollingSummary = sum;
                        messages = "[{\"role\":\"system\",\"content\":" +
                                   Quote(sysPrompt) + "}" +
                                   ",{\"role\":\"user\",\"content\":" +
                                   Quote("【会话压缩摘要】以下是本次任务此前过程的摘要，请基于它继续完成任务，不要重复已完成的部分：\n" + sum) +
                                   "}]";
                        string cstep = "上下文已压缩（" + round + " 轮后），继续执行";
                        steps.Add(cstep);
                        Call("aiStep", "{\"round\":" + (round + 1) + ",\"types\":[\"compress\"]," +
                            "\"applied\":" + Quote("压缩会话上下文") + ",\"text\":" + Quote(cstep) + "}");
                    }
                }

                string body = "{\"model\":" + Quote(model) + ",\"messages\":" + messages +
                              (maxTokens > 0 ? ",\"max_tokens\":" + maxTokens : "") +
                              ExtraParams(provider, model, thinkingOn) +
                              (toolsAvailable ? tools : "") + "}";
                string resp = HttpPostEx(endpoint, key.Trim(), body, 180000, out postErr);

                if (resp.Length == 0 && thinkingOn)
                {
                    thinkingOn = false;       // 接口不认思考参数 → 关掉重试
                    continue;
                }
                if (resp.Length == 0 && toolsAvailable)
                {
                    toolsAvailable = false;   // 接口不支持 tools → 退回纯 JSON 指令
                    continue;
                }
                if (resp.Length == 0 && maxTokens > 4096)
                {
                    maxTokens = 4096;         // 部分模型不支持大输出，降档重试
                    continue;
                }
                if (resp.Length == 0)
                {
                    r.Error = postErr.Length > 0
                        ? "接口返回错误：" + postErr
                        : "网络不可达或接口无响应，请检查网络、Endpoint 与密钥";
                    return r;
                }

                // 思考过程（DeepSeek / 通义 / 智谱 / Kimi 会返回 reasoning_content）
                string reasoningFull = ExtractReasoning(resp);
                string reasoning = reasoningFull;
                if (reasoning.Length > 0)
                {
                    if (reasoning.Length > 1500) reasoning = reasoning.Substring(0, 1500) + "…";
                    Call("aiThink", "{\"round\":" + (round + 1) + ",\"text\":" + Quote(reasoning) + "}");
                }

                string toolCalls = ExtractToolCallsArray(resp);
                if (toolCalls.Length == 0)
                {
                    lastContent = ExtractContent(resp).Trim();
                    if (lastContent.Length == 0)
                    {
                        string err = Regex.Match(resp, "\"message\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"").Groups[1].Value;
                        if (err.Length > 0) err = Unescape(err);
                        else err = "空响应（原始返回 " + resp.Length + " 字节）：" +
                                  (resp.Length > 240 ? resp.Substring(0, 240) + "…" : resp);
                        r.Error = "接口返回异常：" + err;
                        return r;
                    }

                    // DeepSeek 有时把工具调用写成内部 DSML 标记（<｜｜DSML｜｜calls>…），
                    // 这里解析成标准 tool_calls，走正常执行链路
                    string dsmlCalls = ExtractDsmlToolCalls(lastContent);
                    if (dsmlCalls.Length > 0)
                    {
                        toolCalls = dsmlCalls;
                        lastContent = StripDsml(lastContent);
                        Call("aiStep", "{\"round\":" + (round + 1) + ",\"types\":[]," +
                            "\"applied\":" + Quote("解析 DeepSeek 工具标记") + ",\"text\":" +
                            Quote("第 " + (round + 1) + " 轮：解析模型工具标记") + "}");
                    }
                    else
                    {
                        string textReply;
                        string textActions = ExtractTextActions(lastContent, out textReply);
                        if (textActions.Length > 0 && textActionRounds < 3)
                        {
                            textActionRounds++;
                            string result = ExecuteTextActions(textActions);
                            string tTypes = ExtractActionTypes(textActions);
                            string tApplied = ExtractAppliedSummary(result);

                            messages = AppendJsonElement(messages,
                                "{\"role\":\"assistant\",\"content\":" + Quote(lastContent) + "}");
                            messages = AppendJsonElement(messages,
                                "{\"role\":\"user\",\"content\":" +
                                Quote("【系统】上面的 actions 已执行，执行结果：" + result +
                                      "。如果任务还没完成，继续输出 <actions>…</actions>；已经完成就直接给出最终回复。") + "}");

                            string tStep = "第 " + (round + 1) + " 轮：" + (tApplied.Length > 0 ? tApplied : "执行文本指令");
                            steps.Add(tStep);
                            Call("aiStep", "{\"round\":" + (round + 1) + ",\"types\":" + tTypes +
                                ",\"applied\":" + Quote(tApplied) + ",\"text\":" + Quote(tStep) + "}");
                            continue;
                        }

                        // 模型声称已完成、但整轮没有任何 actions 被执行 → 让它重新输出 actions（最多 2 次）
                        if (textActions.Length == 0 && steps.Count == 0 && selfCorrects < 2 && ClaimsDone(textReply))
                        {
                            selfCorrects++;
                            messages = AppendJsonElement(messages,
                                "{\"role\":\"assistant\",\"content\":" + Quote(lastContent) + "}");
                            messages = AppendJsonElement(messages,
                                "{\"role\":\"user\",\"content\":" +
                                Quote("【系统】你刚才只回复了文字，没有任何 actions 被执行，界面不会有任何变化。"
                                    + "请把要做的操作完整放进 <actions>[…]</actions>，不要只在文字里说“已完成”。") + "}");
                            continue;
                        }

                        r.Ok = true;
                        r.Content = StripDsml(textReply.Length > 0 ? textReply : lastContent);
                        r.StepsJson = StepsToJson(steps);
                        r.StepCount = steps.Count;
                        AiMemory.LogTask(message, r.Content);
                        return r;
                    }
                }
                // assistant(tool_calls) → 上下文
                // 注意：DeepSeek 思考模式下带 tools 的请求，后续轮次必须把 reasoning_content 原样回传
                messages = AppendJsonElement(messages,
                    "{\"role\":\"assistant\",\"content\":null" +
                    (reasoningFull.Length > 0 ? ",\"reasoning_content\":" + Quote(reasoningFull) : "") +
                    ",\"tool_calls\":" + toolCalls + "}");

                List<ToolCall> calls = ParseToolCalls(toolCalls);
                if (calls.Count == 0)
                {
                    r.Error = "模型返回了无法解析的工具调用";
                    return r;
                }

                foreach (ToolCall c in calls)
                {
                    string result, types, applied;

                    if (string.Equals(c.Name, "read_state", StringComparison.OrdinalIgnoreCase))
                    {
                        result = GetAiState();
                        types = "[\"read_state\"]";
                        applied = "读取编辑器最新状态";
                    }
                    else if (string.Equals(c.Name, "web_search", StringComparison.OrdinalIgnoreCase))
                    {
                        string query = AiConfig.JStr(c.Arguments, "query") ?? "";
                        int count = 5, n;
                        if (AiConfig.JInt(c.Arguments, "count", out n)) count = Math.Max(1, Math.Min(8, n));
                        result = WebSearch(query, count);
                        types = "[\"web_search\"]";
                        applied = "联网搜索「" + query + "」";
                    }
                    else if (string.Equals(c.Name, "remember", StringComparison.OrdinalIgnoreCase))
                    {
                        string text = AiConfig.JStr(c.Arguments, "text") ?? "";
                        if (!AiConfig.MemoryEnabled)
                        {
                            result = "{\"ok\":false,\"error\":\"用户已关闭长期记忆\"}";
                            applied = "长期记忆已关闭，未写入";
                        }
                        else
                        {
                            AiMemory.Add(text, "note");
                            result = "{\"ok\":true,\"saved\":true,\"total\":" + AiMemory.Count + "}";
                            applied = "写入长期记忆";
                        }
                        types = "[\"remember\"]";
                    }
                    else
                    {
                        string actions = AiConfig.JRaw(c.Arguments, "actions");
                        result = actions.Length > 0
                            ? ExecuteAiActions(actions)
                            : "{\"ok\":false,\"error\":\"参数中没有 actions\"}";
                        types = ExtractActionTypes(actions);
                        applied = ExtractAppliedSummary(result);
                    }

                    messages = AppendJsonElement(messages,
                        "{\"role\":\"tool\",\"tool_call_id\":" + Quote(c.Id) +
                        ",\"content\":" + Quote(result) + "}");

                    string step = "第 " + (round + 1) + " 轮：" + (applied.Length > 0 ? applied : "执行工具调用");
                    steps.Add(step);
                    Call("aiStep", "{\"round\":" + (round + 1) + ",\"types\":" + types +
                        ",\"applied\":" + Quote(applied) + ",\"text\":" + Quote(step) + "}");
                }
            }

            r.Ok = true;
            r.Content = lastContent.Length > 0 ? lastContent : "已完成多轮操作，但模型没有给出最终说明。";
            r.StepsJson = StepsToJson(steps);
            r.StepCount = steps.Count;
            AiMemory.LogTask(message, lastContent);
            return r;
        }

        /// <summary>
        /// 把当前对话压缩成结构化摘要（参考 GitHub Copilot CLI / CoWork OS 的 compact 方案）：
        /// 保留用户目标、已完成的操作、关键参数与待办；已有摘要时只合并新增部分，避免反复重写导致信息漂移。
        /// </summary>
        private static string CompressContext(string endpoint, string key, string model,
                                              string messages, string prevSummary)
        {
            try
            {
                string sys = "你是对话压缩器。把对话记录压缩成简洁的结构化摘要，必须保留："
                    + "1) 用户的原始目标与全部要求；2) 已经完成的修改与结果；"
                    + "3) 关键参数（颜色、字号、标签、文件名等）；4) 尚未完成的待办。"
                    + "不要编造，不要输出解释或客套话，直接输出摘要正文。";
                string source = messages ?? "";
                if (source.Length > 32000) source = source.Substring(source.Length - 32000);
                string user = (string.IsNullOrEmpty(prevSummary) ? "" : "已有摘要（请合并，不要丢失其中信息）：\n" + prevSummary + "\n\n")
                            + "新增对话记录：\n" + source;
                string body = "{\"model\":" + Quote(model) +
                              ",\"messages\":[{\"role\":\"system\",\"content\":" + Quote(sys) +
                              "},{\"role\":\"user\",\"content\":" + Quote(user) + "}]" +
                              ",\"max_tokens\":1600}";
                string resp = HttpPost(endpoint, key.Trim(), body, 120000);
                return ExtractContent(resp).Trim();
            }
            catch { return ""; }
        }

        /// <summary>各厂商的思考/推理参数（对外层 body 追加）。thinkingOn=false 时退回普通温度采样。</summary>
        private static string ExtraParams(string provider, string model, bool thinkingOn)
        {
            string p = (provider ?? "").Trim().ToLowerInvariant();
            string m = (model ?? "").Trim().ToLowerInvariant();
            string effort = (AiConfig.ReasoningEffort ?? "off").Trim().ToLowerInvariant();
            // 统一映射成各厂商可接受的三档
            string level = effort == "low" ? "low" : (effort == "max" ? "max" : (effort == "medium" ? "medium" : "high"));
            StringBuilder sb = new StringBuilder();

            if (p == "deepseek")
            {
                // 思考模式：thinking 开关 + reasoning_effort（仅 high/max，low/medium 会被映射为 high）
                // 思考模式不支持 temperature，只有关闭时才传
                if (thinkingOn)
                {
                    sb.Append(",\"thinking\":{\"type\":\"enabled\"}");
                    sb.Append(",\"reasoning_effort\":").Append(Quote(level == "max" ? "max" : "high"));
                }
                else
                {
                    sb.Append(",\"thinking\":{\"type\":\"disabled\"},\"temperature\":0.3");
                }
            }
            else if (p == "dashscope")
            {
                // 通义千问：enable_thinking 开关 + thinking_budget 限制思考 token
                if (thinkingOn)
                {
                    sb.Append(",\"enable_thinking\":true");
                    sb.Append(",\"thinking_budget\":").Append(ThinkingBudget(effort));
                }
                else
                {
                    sb.Append(",\"enable_thinking\":false,\"temperature\":0.3");
                }
            }
            else if (p == "moonshot")
            {
                // Kimi：k3 用顶层 reasoning_effort；k2.6/k2.7 用 thinking.type（k2.6 可再开 keep=all）
                if (m.IndexOf("k3", StringComparison.Ordinal) >= 0)
                {
                    if (thinkingOn) sb.Append(",\"reasoning_effort\":").Append(Quote(level));
                }
                else if (m.IndexOf("k2.7", StringComparison.Ordinal) >= 0)
                {
                    sb.Append(",\"thinking\":{\"type\":\"enabled\"}");
                }
                else if (m.IndexOf("k2.6", StringComparison.Ordinal) >= 0 ||
                         m.IndexOf("k2-", StringComparison.Ordinal) >= 0)
                {
                    sb.Append(",\"thinking\":{\"type\":\"").Append(thinkingOn ? "enabled" : "disabled").Append('"');
                    if (thinkingOn) sb.Append(",\"keep\":\"all\"");
                    sb.Append('}');
                    if (!thinkingOn) sb.Append(",\"temperature\":0.3");
                }
                else if (thinkingOn)
                {
                    sb.Append(",\"thinking\":{\"type\":\"enabled\"}");
                }
                else
                {
                    sb.Append(",\"temperature\":0.3");
                }
            }
            else if (p == "zhipu")
            {
                // 智谱 GLM：thinking.type 开关；GLM-5.2+ 支持 reasoning_effort
                sb.Append(",\"thinking\":{\"type\":\"").Append(thinkingOn ? "enabled" : "disabled").Append("\"}");
                if (thinkingOn)
                    sb.Append(",\"reasoning_effort\":").Append(Quote(level == "max" ? "max" : "high"));
                else
                    sb.Append(",\"temperature\":0.3");
            }
            else if (p == "openai")
            {
                // OpenAI：o 系列 / gpt-5 支持 reasoning_effort，且不接受 temperature
                if (thinkingOn) sb.Append(",\"reasoning_effort\":").Append(Quote(level));
                else sb.Append(",\"temperature\":0.3");
            }
            else
            {
                // 自定义 OpenAI 兼容：只发最通用的 reasoning_effort
                if (thinkingOn) sb.Append(",\"reasoning_effort\":").Append(Quote(level));
                else sb.Append(",\"temperature\":0.3");
            }
            return sb.ToString();
        }

        /// <summary>思考强度 → 通义 thinking_budget（思考 token 上限）。</summary>
        private static int ThinkingBudget(string effort)
        {
            switch ((effort ?? "").Trim().ToLowerInvariant())
            {
                case "low": return 1024;
                case "medium": return 4096;
                case "max": return 32768;
                default: return 8192;
            }
        }

        /// <summary>工作结束后在右下角弹出 Windows 通知风格卡片（可在 AI 页关闭）。</summary>
        private ToastForm _toast;

        private void NotifyAiDone(string content, int stepCount)
        {
            if (!AiConfig.NotifyDone) return;
            string summary = (content ?? "").Trim();
            if (summary.Length > 80) summary = summary.Substring(0, 80) + "…";
            if (summary.Length == 0) summary = "已完成。";
            string message = summary + (stepCount > 0 ? "\n共执行 " + stepCount + " 轮操作。" : "");
            NotifyToast("AI 已完成", message);
        }

        /// <summary>通用通知卡片。subtitle 例如「AI 已完成」「已撤回」。</summary>
        private void NotifyToast(string subtitle, string message)
        {
            try
            {
                BeginInvoke(new Action(delegate
                {
                    try
                    {
                        if (_toast != null) { try { _toast.Close(); } catch { } }
                        _toast = new ToastForm("彩色文本生成器", subtitle, message, delegate
                        {
                            try
                            {
                                if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
                                Activate();
                            }
                            catch { }
                        });
                        _toast.Show();
                    }
                    catch { }
                }));
            }
            catch { }
        }

        private static string AppendJsonElement(string arr, string element)
        {
            arr = (arr ?? "").TrimEnd();
            if (!arr.EndsWith("]")) return arr;
            string head = arr.Substring(0, arr.Length - 1).TrimEnd();
            return head + (head.Length > 1 ? "," : "") + element + "]";
        }

        private static string ExtractToolCallsArray(string resp)
        {
            string arr = AiConfig.JRaw(resp ?? "", "tool_calls");
            if (arr.Length == 0 || !arr.StartsWith("[") || arr.IndexOf('{') < 0) return "";
            return arr;
        }

        /// <summary>思考内容（reasoning_content）。</summary>
        private static string ExtractReasoning(string resp)
        {
            Match m = Regex.Match(resp ?? "", "\"reasoning_content\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
            return m.Success ? Unescape(m.Groups[1].Value) : "";
        }

        /// <summary>
        /// 从纯文本回复里提取 actions（接口不支持原生 function calling 时的兜底）：
        /// 1) &lt;actions&gt;[…]&lt;/actions&gt; 标签；2) {"reply":"…","actions":[…]} 整段 JSON。
        /// 返回 actions 数组原文；没有则返回空串，reply 为去掉标签后的正文。
        /// </summary>
        private static string ExtractTextActions(string content, out string reply)
        {
            reply = (content ?? "").Trim();
            if (reply.Length == 0) return "";

            Match m = Regex.Match(reply, "<actions>([\\s\\S]*?)</actions>", RegexOptions.IgnoreCase);
            while (m.Success)
            {
                string arr = NormalizeActions(m.Groups[1].Value);
                if (arr.Length > 0)
                {
                    reply = reply.Substring(0, m.Index).Trim();
                    return arr;
                }
                m = m.NextMatch();
            }

            string s = reply;
            if (s.StartsWith("```"))
            {
                int nl = s.IndexOf('\n');
                if (nl > 0) s = s.Substring(nl + 1);
                int end = s.LastIndexOf("```", StringComparison.Ordinal);
                if (end >= 0) s = s.Substring(0, end);
                s = s.Trim();
            }
            if (s.StartsWith("{"))
            {
                string arr = AiConfig.JRaw(s, "actions");
                if (arr.Length > 0 && arr.StartsWith("["))
                {
                    string r = AiConfig.JStr(s, "reply");
                    reply = string.IsNullOrEmpty(r) ? "" : r;
                    return arr;
                }
            }
            return "";
        }

        private static string NormalizeActions(string raw)
        {
            string s = (raw ?? "").Trim();
            if (s.StartsWith("[")) return s;
            if (s.StartsWith("{"))
            {
                string arr = AiConfig.JRaw(s, "actions");
                if (arr.Length > 0 && arr.StartsWith("[")) return arr;
            }
            return "";
        }

        /// <summary>
        /// 把 DeepSeek 内部 DSML 工具标记（&lt;｜｜DSML｜｜calls&gt;…）解析成标准 tool_calls JSON。
        /// 例如：
        /// &lt;｜｜DSML｜｜invoke name="read_state"&gt; … &lt;/｜｜DSML｜｜invoke&gt;
        /// 没有可解析内容时返回空串。
        /// </summary>
        private static string ExtractDsmlToolCalls(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            string s = NormalizeDsml(text);
            if (s.IndexOf("DSML", StringComparison.OrdinalIgnoreCase) < 0) return "";

            MatchCollection invokes = Regex.Matches(s,
                "<\\|+\\s*DSML\\s*\\|+\\s*invoke\\s+name\\s*=\\s*[\"']([^\"']+)[\"'][^>]*>([\\s\\S]*?)</\\|+\\s*DSML\\s*\\|+\\s*invoke>",
                RegexOptions.IgnoreCase);
            if (invokes.Count == 0) return "";

            StringBuilder arr = new StringBuilder("[");
            int idx = 0;
            foreach (Match m in invokes)
            {
                string name = m.Groups[1].Value.Trim();
                if (name.Length == 0) continue;
                string inner = m.Groups[2].Value;

                StringBuilder args = new StringBuilder("{");
                bool first = true;
                MatchCollection ps = Regex.Matches(inner,
                    "<\\|+\\s*DSML\\s*\\|+\\s*parameter\\s+name\\s*=\\s*[\"']([^\"']+)[\"'][^>]*>([\\s\\S]*?)</\\|+\\s*DSML\\s*\\|+\\s*parameter>",
                    RegexOptions.IgnoreCase);
                foreach (Match p in ps)
                {
                    string pn = p.Groups[1].Value.Trim();
                    string pv = p.Groups[2].Value.Trim();
                    if (pn.Length == 0) continue;
                    if (!first) args.Append(',');
                    first = false;
                    args.Append(Quote(pn)).Append(':');
                    if (pv.StartsWith("{") || pv.StartsWith("[")) args.Append(pv);
                    else args.Append(Quote(pv));
                }
                args.Append('}');

                if (idx > 0) arr.Append(',');
                idx++;
                arr.Append("{\"id\":").Append(Quote("dsml_" + idx))
                   .Append(",\"type\":\"function\",\"function\":{\"name\":").Append(Quote(name))
                   .Append(",\"arguments\":").Append(Quote(args.ToString())).Append("}}");
            }
            arr.Append(']');
            return idx > 0 ? arr.ToString() : "";
        }

        /// <summary>统一全角竖线/尖括号，便于正则匹配 DSML 标记。</summary>
        private static string NormalizeDsml(string text)
        {
            return (text ?? "").Replace('｜', '|').Replace('＜', '<').Replace('＞', '>');
        }

        /// <summary>去掉回复里残留的 DSML 标记，避免用户看到乱码。</summary>
        private static string StripDsml(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            string s = NormalizeDsml(text);
            s = Regex.Replace(s, "</?\\|+\\s*DSML\\s*\\|+\\s*calls\\s*>", "", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, "</?\\|+\\s*DSML\\s*\\|+\\s*invoke[^>]*>", "", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, "</?\\|+\\s*DSML\\s*\\|+\\s*parameter[^>]*>", "", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, "\n{3,}", "\n\n");
            return s.Trim();
        }

        /// <summary>
        /// 执行纯文本形式的 actions。
        /// remember / read_state / web_search 由服务端直接处理（前端不认识），其余交给前端执行。
        /// </summary>
        private string ExecuteTextActions(string actionsJson)
        {
            List<string> keep = new List<string>();
            List<string> done = new List<string>();
            StringBuilder extra = new StringBuilder();

            foreach (string obj in SplitTopLevelObjects(actionsJson))
            {
                string type = AiConfig.JStr(obj, "type") ?? "";
                if (string.Equals(type, "remember", StringComparison.OrdinalIgnoreCase))
                {
                    string text = AiConfig.JStr(obj, "text") ?? "";
                    if (string.IsNullOrEmpty(text)) text = AiConfig.JStr(obj, "content") ?? "";
                    if (!AiConfig.MemoryEnabled) { done.Add("长期记忆已关闭，未写入"); continue; }
                    AiMemory.Add(text, "note");
                    done.Add("写入长期记忆");
                    continue;
                }
                if (string.Equals(type, "read_state", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(type, "readState", StringComparison.OrdinalIgnoreCase))
                {
                    done.Add("读取编辑器状态");
                    extra.Append("当前编辑器状态：").Append(GetAiState()).Append('\n');
                    continue;
                }
                if (string.Equals(type, "web_search", StringComparison.OrdinalIgnoreCase))
                {
                    string q = AiConfig.JStr(obj, "query") ?? "";
                    int n = 5, tmp;
                    if (AiConfig.JInt(obj, "count", out tmp)) n = Math.Max(1, Math.Min(8, tmp));
                    done.Add("联网搜索「" + q + "」");
                    extra.Append(WebSearch(q, n)).Append('\n');
                    continue;
                }
                keep.Add(obj);
            }

            string front = keep.Count > 0
                ? ExecuteAiActions("[" + string.Join(",", keep.ToArray()) + "]")
                : "{\"ok\":true,\"applied\":[]}";

            StringBuilder server = new StringBuilder();
            if (done.Count > 0) server.Append(string.Join("、", done.ToArray()));
            if (extra.Length > 0)
            {
                if (server.Length > 0) server.Append("：");
                server.Append(extra);
            }
            if (server.Length > 0)
            {
                string tail = front.TrimEnd();
                if (tail.EndsWith("}"))
                    front = tail.Substring(0, tail.Length - 1) + ",\"server\":" + Quote(server.ToString()) + "}";
            }
            return front;
        }

        /// <summary>回复里是否声称已经完成了某种修改（用于识别“只说不做”）。</summary>
        private static bool ClaimsDone(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            return Regex.IsMatch(text, "已(经)?(写入|追加|修改|改为|设置|完成|删除|清空|生成|切换)");
        }

        /// <summary>本轮 actions 的类型列表（用于界面展示执行过程）。</summary>
        private static string ExtractActionTypes(string actions)
        {
            MatchCollection ms = Regex.Matches(actions ?? "", "\"type\"\\s*:\\s*\"([^\"]+)\"");
            List<string> list = new List<string>();
            foreach (Match m in ms)
            {
                string t = m.Groups[1].Value;
                if (!list.Contains(t)) list.Add(t);
            }
            StringBuilder sb = new StringBuilder("[");
            for (int i = 0; i < list.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(Quote(list[i]));
            }
            sb.Append(']');
            return sb.ToString();
        }

        private sealed class ToolCall
        {
            public string Id = "";
            public string Name = "";
            public string Arguments = "";
        }

        private static List<ToolCall> ParseToolCalls(string arr)
        {
            List<ToolCall> list = new List<ToolCall>();
            foreach (string obj in SplitTopLevelObjects(arr))
            {
                string id = AiConfig.JStr(obj, "id") ?? "";
                string name = AiConfig.JStr(obj, "name") ?? "";
                string args = AiConfig.JStr(obj, "arguments") ?? "";
                if (args.Length == 0) continue;
                list.Add(new ToolCall { Id = id, Name = name, Arguments = args });
            }
            return list;
        }

        internal static List<string> SplitTopLevelObjects(string arr)
        {
            List<string> list = new List<string>();
            int depth = 0, start = -1;
            bool inStr = false, esc = false;
            for (int i = 0; i < arr.Length; i++)
            {
                char ch = arr[i];
                if (inStr)
                {
                    if (esc) esc = false;
                    else if (ch == '\\') esc = true;
                    else if (ch == '"') inStr = false;
                    continue;
                }
                if (ch == '"') { inStr = true; continue; }
                if (ch == '{') { if (depth == 0) start = i; depth++; }
                else if (ch == '}')
                {
                    depth--;
                    if (depth == 0 && start >= 0) { list.Add(arr.Substring(start, i - start + 1)); start = -1; }
                }
            }
            return list;
        }

        private static string ExtractAppliedSummary(string result)
        {
            try
            {
                string arr = AiConfig.JRaw(result ?? "", "applied");
                if (arr.Length == 0) return "";
                MatchCollection ms = Regex.Matches(arr, "\"((?:[^\"\\\\]|\\\\.)*)\"");
                List<string> items = new List<string>();
                foreach (Match m in ms) items.Add(Unescape(m.Groups[1].Value));
                return string.Join("、", items.ToArray());
            }
            catch { return ""; }
        }

        // ============================================================ 联网搜索（Bing / DuckDuckGo，免密钥）
        private static string HttpGetHtml(string url, int ms)
        {
            try
            {
                ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12 | SecurityProtocolType.Tls11 | SecurityProtocolType.Tls;
                HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
                req.Method = "GET";
                req.Timeout = ms;
                req.ReadWriteTimeout = ms;
                req.UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/122.0 Safari/537.36";
                req.Accept = "text/html,application/xhtml+xml";
                req.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
                using (WebResponse resp = req.GetResponse())
                using (Stream s = resp.GetResponseStream())
                using (StreamReader sr = new StreamReader(s, Encoding.UTF8))
                    return sr.ReadToEnd();
            }
            catch { return ""; }
        }

        private static string StripHtml(string s)
        {
            s = Regex.Replace(s ?? "", "<script[\\s\\S]*?</script>", " ", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, "<[^>]+>", " ");
            s = WebUtility.HtmlDecode(s);
            s = Regex.Replace(s, "\\s+", " ").Trim();
            return s;
        }

        private static string WebSearch(string query, int count)
        {
            query = (query ?? "").Trim();
            if (query.Length == 0) return "{\"ok\":false,\"error\":\"query 为空\"}";

            List<string[]> results = ParseBing(
                HttpGetHtml("https://cn.bing.com/search?q=" + Uri.EscapeDataString(query) + "&setlang=zh-CN", 12000), count);

            if (results.Count == 0)
            {
                results = ParseDdg(
                    HttpGetHtml("https://html.duckduckgo.com/html/?q=" + Uri.EscapeDataString(query), 12000), count);
            }

            StringBuilder sb = new StringBuilder();
            sb.Append("{\"ok\":").Append(results.Count > 0 ? "true" : "false");
            sb.Append(",\"query\":").Append(Quote(query));
            sb.Append(",\"results\":[");
            for (int i = 0; i < results.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append("{\"title\":").Append(Quote(results[i][0]));
                sb.Append(",\"url\":").Append(Quote(results[i][1]));
                sb.Append(",\"snippet\":").Append(Quote(results[i][2])).Append('}');
            }
            sb.Append("]}");
            return sb.ToString();
        }

        private static List<string[]> ParseBing(string html, int count)
        {
            List<string[]> list = new List<string[]>();
            if (string.IsNullOrEmpty(html)) return list;
            string[] blocks = Regex.Split(html, "<li class=\"b_algo\"", RegexOptions.IgnoreCase);
            for (int i = 1; i < blocks.Length && list.Count < count; i++)
            {
                string b = blocks[i];
                Match href = Regex.Match(b, "<h2[^>]*>\\s*<a[^>]*href=\"(https?://[^\"]+)\"", RegexOptions.IgnoreCase);
                if (!href.Success) href = Regex.Match(b, "href=\"(https?://[^\"]+)\"", RegexOptions.IgnoreCase);
                if (!href.Success) continue;
                string url = WebUtility.HtmlDecode(href.Groups[1].Value);
                if (url.IndexOf("bing.com", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                string title = StripHtml(Regex.Match(b, "<h2[^>]*>([\\s\\S]*?)</h2>", RegexOptions.IgnoreCase).Groups[1].Value);
                string snippet = StripHtml(Regex.Match(b, "<p[^>]*>([\\s\\S]*?)</p>", RegexOptions.IgnoreCase).Groups[1].Value);
                if (title.Length == 0) continue;
                list.Add(new string[] { title, url, snippet });
            }
            return list;
        }

        private static List<string[]> ParseDdg(string html, int count)
        {
            List<string[]> list = new List<string[]>();
            if (string.IsNullOrEmpty(html)) return list;
            MatchCollection links = Regex.Matches(html,
                "<a[^>]*class=\"result__a\"[^>]*href=\"([^\"]+)\"[^>]*>([\\s\\S]*?)</a>", RegexOptions.IgnoreCase);
            MatchCollection snips = Regex.Matches(html,
                "<a[^>]*class=\"result__snippet\"[^>]*>([\\s\\S]*?)</a>", RegexOptions.IgnoreCase);
            for (int i = 0; i < links.Count && list.Count < count; i++)
            {
                string url = WebUtility.HtmlDecode(links[i].Groups[1].Value);
                int u = url.IndexOf("uddg=", StringComparison.OrdinalIgnoreCase);
                if (u >= 0)
                {
                    string enc = url.Substring(u + 5);
                    int amp = enc.IndexOf('&');
                    if (amp >= 0) enc = enc.Substring(0, amp);
                    try { url = Uri.UnescapeDataString(enc); } catch { }
                }
                string title = StripHtml(links[i].Groups[2].Value);
                string snippet = i < snips.Count ? StripHtml(snips[i].Groups[1].Value) : "";
                if (title.Length == 0) continue;
                list.Add(new string[] { title, url, snippet });
            }
            return list;
        }

        private static string StepsToJson(List<string> steps)
        {
            StringBuilder sb = new StringBuilder("[");
            for (int i = 0; i < steps.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(Quote(steps[i]));
            }
            sb.Append(']');
            return sb.ToString();
        }

        private static string NormalizeEndpoint(string ep)
        {
            ep = (ep ?? "").Trim().TrimEnd('/');
            if (ep.Length == 0) return "https://api.deepseek.com/chat/completions";
            if (ep.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase)) return ep;
            return ep + "/chat/completions";
        }

        /// <summary>从 OpenAI 兼容响应里取 message.content（跳过 reasoning_content）。</summary>
        private static string ExtractContent(string resp)
        {
            MatchCollection ms = Regex.Matches(resp ?? "", "\"content\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
            if (ms.Count == 0) return "";
            return Unescape(ms[ms.Count - 1].Groups[1].Value);
        }

        /// <summary>取 function calling 的 arguments（JSON 字符串）。</summary>
        private static string ExtractToolArguments(string resp)
        {
            Match m = Regex.Match(resp ?? "", "\"arguments\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
            if (!m.Success) return "";
            return Unescape(m.Groups[1].Value);
        }

        // ============================================================ 操作回桥
        /// <summary>服务线程调用：把 actions 交给前端执行并等待结果。</summary>
        public string ExecuteAiActions(string actionsJson)
        {
            if (string.IsNullOrEmpty(actionsJson)) return "{\"ok\":true,\"applied\":[]}";
            string id = Guid.NewGuid().ToString("N");
            ManualResetEventSlim ev = new ManualResetEventSlim(false);
            lock (_aiWaiters) { _aiWaiters[id] = new object[] { ev, null }; }

            Call("aiApplyActions", "{\"id\":\"" + id + "\",\"actions\":" + actionsJson + "}");

            bool signaled = ev.Wait(20000);
            string result = null;
            lock (_aiWaiters)
            {
                object[] box;
                if (_aiWaiters.TryGetValue(id, out box)) result = box[1] as string;
                _aiWaiters.Remove(id);
            }
            try { ev.Dispose(); } catch { }
            if (!signaled || result == null) return "{\"ok\":false,\"error\":\"前端执行超时\"}";
            return result;
        }

        private void OnAiActionResult(string arg)
        {
            int bar = (arg ?? "").IndexOf('|');
            if (bar <= 0) return;
            string id = arg.Substring(0, bar);
            string json = arg.Substring(bar + 1);
            lock (_aiWaiters)
            {
                object[] box;
                if (_aiWaiters.TryGetValue(id, out box))
                {
                    box[1] = json;
                    try { ((ManualResetEventSlim)box[0]).Set(); } catch { }
                }
            }
        }

        // ============================================================ 端口服务
        /// <summary>启动时若上次开着端口服务，则自动恢复。</summary>
        public void StartAiServerIfEnabled()
        {
            if (!AiConfig.ServerEnabled) return;
            string url = AiServer.Start(this, AiConfig.Port, AiConfig.Token);
            if (url.Length > 0) CallStr("aiServerStatus", "运行中 · " + url);
        }

        public void AiServerApply(string json)
        {
            bool enabled;
            if (!AiConfig.JBool(json, "enabled", out enabled)) enabled = true;
            int port;
            if (!AiConfig.JInt(json, "port", out port) || port <= 0 || port >= 65536) port = AiConfig.Port;
            string token = AiConfig.JStr(json, "token");
            if (token == null) token = AiConfig.Token;

            AiConfig.ServerEnabled = enabled;
            AiConfig.Port = port;
            AiConfig.Token = token ?? "";
            AiConfig.Save();

            if (!enabled)
            {
                AiServer.Stop();
                CallStr("aiServerStatus", "未启动");
                return;
            }

            string url = AiServer.Start(this, port, AiConfig.Token);
            if (url.Length == 0)
            {
                CallStr("aiServerStatus", "启动失败：端口被占用");
                return;
            }
            CallStr("aiServerStatus", "运行中 · " + url);
            CallStr("status", "AI 端口服务已启动：" + url);
        }

        public string AiServerStatus()
        {
            return AiServer.Running ? ("运行中 · http://127.0.0.1:" + AiServer.Port) : "未启动";
        }

        /// <summary>
        /// 打开独立的 AI 对话页（浏览器窗口）。主程序收进托盘后端口服务仍在，
        /// 该页面可以继续对话，并通过 /chat 让 AI 操作主程序里的编辑器。
        /// </summary>
        public void AiOpenChatPage()
        {
            if (!AiConfig.ServerEnabled)
            {
                AiConfig.ServerEnabled = true;
                AiConfig.Save();
            }
            string url = AiServer.Start(this, AiConfig.Port, AiConfig.Token);
            if (url.Length == 0)
            {
                CallStr("status", "独立对话页启动失败：端口被占用");
                return;
            }
            string full = url + "/ui" + (string.IsNullOrEmpty(AiConfig.Token)
                ? "" : "?token=" + Uri.EscapeDataString(AiConfig.Token));
            try
            {
                ChatWindow.ShowOrActivate(full, WebEnv, delegate
                {
                    try { Call("aiWindowClosed", "{}"); } catch { }
                });
                Call("aiWindowOpened", "{}");
                CallStr("aiServerStatus", "运行中 · " + url);
                CallStr("status", "AI 对话窗口已打开");
            }
            catch
            {
                try { OpenBrowser(full); } catch { }
                try { Call("aiWindowClosed", "{}"); } catch { }   // 原生窗口没打开 → 恢复导航项
                CallStr("aiServerStatus", "运行中 · " + url);
                CallStr("status", "已用浏览器打开独立对话页：" + full);
            }
        }
    }

    // ================================================================ 本地 HTTP 端口
    internal static class AiServer
    {
        private static TcpListener _listener;
        private static int _port;
        private static MainForm _form;
        private static string _token = "";

        public static bool Running { get { return _listener != null; } }
        public static int Port { get { return _port; } }

        public static string Start(MainForm form, int port, string token)
        {
            Stop();
            _form = form;
            _token = token ?? "";
            for (int p = port; p < port + 20 && p < 65536; p++)
            {
                try
                {
                    TcpListener l = new TcpListener(IPAddress.Loopback, p);
                    l.Start();
                    _listener = l;
                    _port = p;
                    break;
                }
                catch { }
            }
            if (_listener == null) return "";
            Thread t = new Thread(AcceptLoop);
            t.IsBackground = true;
            t.Start();
            return "http://127.0.0.1:" + _port;
        }

        public static void Stop()
        {
            try { if (_listener != null) _listener.Stop(); } catch { }
            _listener = null;
            _port = 0;
        }

        private static void AcceptLoop()
        {
            while (true)
            {
                TcpClient c;
                try { c = _listener.AcceptTcpClient(); }
                catch { return; }
                ThreadPool.QueueUserWorkItem(delegate
                {
                    try { Handle(c); }
                    catch { }
                    finally { try { c.Close(); } catch { } }
                });
            }
        }

        private static void Handle(TcpClient c)
        {
            c.ReceiveTimeout = 15000;
            c.SendTimeout = 30000;
            NetworkStream ns = c.GetStream();

            string method, path, body;
            Dictionary<string, string> headers;
            if (!ReadRequest(ns, out method, out path, out headers, out body)) { WriteJson(ns, 400, "{\"ok\":false,\"error\":\"bad request\"}"); return; }

            if (method == "OPTIONS") { WriteJson(ns, 204, ""); return; }

            // 鉴权
            if (!string.IsNullOrEmpty(_token))
            {
                string t = null;
                if (!headers.TryGetValue("X-Auth-Token", out t)) t = null;
                if (string.IsNullOrEmpty(t))
                {
                    string query = headers.ContainsKey("__query") ? headers["__query"] : "";
                    t = QueryValue(query, "token");
                }
                if (t != _token) { WriteJson(ns, 401, "{\"ok\":false,\"error\":\"unauthorized\"}"); return; }
            }

            if (path == "/health")
            {
                WriteJson(ns, 200, "{\"ok\":true,\"app\":\"彩色文本生成器\",\"version\":\"" + MainForm.Version +
                    "\",\"ai\":\"beta\",\"server\":\"running\"}");
                return;
            }

            if (path == "/state")
            {
                WriteJson(ns, 200, _form != null ? _form.GetAiState() : "{}");
                return;
            }

            // 独立对话页（HTML）
            if (path == "/ui" || path == "/chat.html")
            {
                WriteHtml(ns, 200, MainForm.LoadChatHtml());
                return;
            }

            // 会话读写（与主界面共用 convs.json）
            if (path == "/convs" && method == "GET")
            {
                WriteJson(ns, 200, _form != null ? _form.AiConvsLoad() : "{\"list\":[]}");
                return;
            }
            if (path == "/convs" && method == "POST")
            {
                if (_form != null) _form.AiConvsSave(body);
                WriteJson(ns, 200, "{\"ok\":true}");
                return;
            }

            // 思考强度 / 联网搜索
            if (path == "/config" && method == "GET")
            {
                WriteJson(ns, 200, "{\"reasoningEffort\":" + AiConfig.J(AiConfig.ReasoningEffort) +
                    ",\"webSearch\":" + (AiConfig.WebSearchEnabled ? "true" : "false") + "}");
                return;
            }
            if (path == "/config" && method == "POST")
            {
                string v = AiConfig.JStr(body, "reasoningEffort");
                if (v != null)
                {
                    v = v.Trim().ToLowerInvariant();
                    if (v == "off" || v == "low" || v == "medium" || v == "high" || v == "max")
                        AiConfig.ReasoningEffort = v;
                }
                bool b;
                if (AiConfig.JBool(body, "webSearch", out b)) AiConfig.WebSearchEnabled = b;
                AiConfig.Save();
                WriteJson(ns, 200, "{\"ok\":true}");
                return;
            }

            // 完整设置（独立窗口用；不返回明文密钥）
            if (path == "/settings" && method == "GET")
            {
                StringBuilder sb = new StringBuilder();
                sb.Append("{\"provider\":").Append(AiConfig.J(AiConfig.Provider));
                sb.Append(",\"endpoint\":").Append(AiConfig.J(AiConfig.Endpoint));
                sb.Append(",\"model\":").Append(AiConfig.J(AiConfig.Model));
                sb.Append(",\"keySet\":").Append(string.IsNullOrEmpty(AiConfig.Key) ? "false" : "true");
                sb.Append(",\"keyMask\":").Append(AiConfig.J(AiConfig.MaskKey(AiConfig.Key)));
                sb.Append(",\"reasoningEffort\":").Append(AiConfig.J(AiConfig.ReasoningEffort));
                sb.Append(",\"webSearch\":").Append(AiConfig.WebSearchEnabled ? "true" : "false");
                sb.Append(",\"memoryEnabled\":").Append(AiConfig.MemoryEnabled ? "true" : "false");
                sb.Append(",\"activePreset\":").Append(AiConfig.J(AiConfig.ActivePreset));
                sb.Append(",\"presets\":[");
                for (int i = 0; i < AiConfig.Presets.Count; i++)
                {
                    AiConfig.AiPreset p = AiConfig.Presets[i];
                    if (i > 0) sb.Append(',');
                    sb.Append("{\"name\":").Append(AiConfig.J(p.Name));
                    sb.Append(",\"platform\":").Append(AiConfig.J(p.Platform));
                    sb.Append(",\"endpoint\":").Append(AiConfig.J(p.Endpoint));
                    sb.Append(",\"levels\":").Append(p.Levels);
                    sb.Append(",\"hasKey\":").Append(string.IsNullOrEmpty(p.Key) ? "false" : "true").Append('}');
                }
                sb.Append("]}");
                WriteJson(ns, 200, sb.ToString());
                return;
            }

            if (path == "/settings" && method == "POST")
            {
                string v = AiConfig.JStr(body, "provider");
                if (!string.IsNullOrEmpty(v)) AiConfig.Provider = v;
                v = AiConfig.JStr(body, "endpoint"); if (v != null) AiConfig.Endpoint = v;
                v = AiConfig.JStr(body, "model"); if (v != null) AiConfig.Model = v;
                v = AiConfig.JStr(body, "key"); if (!string.IsNullOrEmpty(v)) AiConfig.Key = v;
                v = AiConfig.JStr(body, "activePreset"); if (v != null) AiConfig.ActivePreset = v;
                v = AiConfig.JStr(body, "reasoningEffort");
                if (v != null)
                {
                    v = v.Trim().ToLowerInvariant();
                    if (v == "off" || v == "low" || v == "medium" || v == "high" || v == "max")
                        AiConfig.ReasoningEffort = v;
                }
                bool b;
                if (AiConfig.JBool(body, "webSearch", out b)) AiConfig.WebSearchEnabled = b;
                if (AiConfig.JBool(body, "memoryEnabled", out b)) AiConfig.MemoryEnabled = b;
                AiConfig.Save();
                WriteJson(ns, 200, "{\"ok\":true}");
                return;
            }

            if (path == "/preset/add" && method == "POST")
            {
                string name = (AiConfig.JStr(body, "name") ?? "").Trim();
                if (name.Length == 0) { WriteJson(ns, 400, "{\"ok\":false,\"error\":\"缺少 name\"}"); return; }
                string platform = AiConfig.JStr(body, "platform") ?? "自定义";
                string ep = AiConfig.JStr(body, "endpoint") ?? "";
                string key = AiConfig.JStr(body, "key") ?? "";
                int lv = 4;
                if (!AiConfig.JInt(body, "levels", out lv) || lv < 2 || lv > 5) lv = 4;

                AiConfig.AiPreset found = null;
                foreach (AiConfig.AiPreset p in AiConfig.Presets)
                    if (string.Equals(p.Name, name, StringComparison.Ordinal)) { found = p; break; }
                if (found == null)
                {
                    found = new AiConfig.AiPreset();
                    found.Id = "p" + Guid.NewGuid().ToString("N").Substring(0, 8);
                    found.Name = name;
                    AiConfig.Presets.Add(found);
                }
                found.Platform = platform;
                found.Endpoint = ep;
                found.Levels = lv;
                if (!string.IsNullOrEmpty(key)) found.Key = key;
                AiConfig.Save();
                WriteJson(ns, 200, "{\"ok\":true}");
                return;
            }

            if (path == "/preset/delete" && method == "POST")
            {
                string name = (AiConfig.JStr(body, "name") ?? "").Trim();
                for (int i = AiConfig.Presets.Count - 1; i >= 0; i--)
                    if (string.Equals(AiConfig.Presets[i].Name, name, StringComparison.Ordinal))
                        AiConfig.Presets.RemoveAt(i);
                if (AiConfig.ActivePreset == name) AiConfig.ActivePreset = "";
                AiConfig.Save();
                WriteJson(ns, 200, "{\"ok\":true}");
                return;
            }

            if (path == "/memory" && method == "GET")
            {
                WriteJson(ns, 200, AiMemory.ToJson());
                return;
            }
            if (path == "/memory/add" && method == "POST")
            {
                AiMemory.Add(AiConfig.JStr(body, "text") ?? "", "note");
                WriteJson(ns, 200, AiMemory.ToJson());
                return;
            }
            if (path == "/memory/remove" && method == "POST")
            {
                AiMemory.Remove(AiConfig.JStr(body, "id") ?? "");
                WriteJson(ns, 200, AiMemory.ToJson());
                return;
            }
            if (path == "/memory/clear" && method == "POST")
            {
                AiMemory.Clear();
                WriteJson(ns, 200, AiMemory.ToJson());
                return;
            }
            if (path == "/memory/compress" && method == "POST")
            {
                string arr = AiConfig.JRaw(body, "messages");
                int added = MainForm.AiCompressMemory(arr);
                WriteJson(ns, 200, "{\"ok\":true,\"added\":" + added + "}");
                return;
            }

            if (path == "/providers")
            {
                WriteJson(ns, 200, AiServer.ProvidersJson());
                return;
            }

            if (path == "/action" && method == "POST")
            {
                string actions = AiConfig.JRaw(body, "actions");
                if (actions.Length == 0) { WriteJson(ns, 400, "{\"ok\":false,\"error\":\"缺少 actions\"}"); return; }
                WriteJson(ns, 200, _form != null ? _form.ExecuteAiActions(actions) : "{\"ok\":false,\"error\":\"no ui\"}");
                return;
            }

            if (path == "/chat" && method == "POST")
            {
                string message = AiConfig.JStr(body, "message");
                if (string.IsNullOrEmpty(message)) { WriteJson(ns, 400, "{\"ok\":false,\"error\":\"缺少 message\"}"); return; }
                string history = AiConfig.JRaw(body, "history");   // 可选：[{role,content},...]
                string images = AiConfig.JRaw(body, "images");     // 可选：["data:image/png;base64,..."]
                AgentResult r = _form != null ? _form.AiChatSync(message, history, images) : new AgentResult { Error = "no ui" };
                if (!r.Ok) { WriteJson(ns, 200, "{\"ok\":false,\"error\":" + AiConfig.J(r.Error) + "}"); return; }
                WriteJson(ns, 200, "{\"ok\":true,\"reply\":" + AiConfig.J(r.Content) +
                    ",\"steps\":" + r.StepsJson + "}");
                return;
            }

            WriteJson(ns, 200,
                "{\"ok\":true,\"app\":\"彩色文本生成器\",\"ai\":\"beta\",\"endpoints\":[" +
                "\"GET /health\",\"GET /state\",\"GET /providers\",\"GET /ui\",\"GET /convs\",\"POST /convs\"," +
                "\"GET /config\",\"POST /config\",\"POST /action\",\"POST /chat\"]}");
        }

        private static string ProvidersJson()
        {
            return
                "{\"ok\":true,\"providers\":[" +
                "{\"id\":\"deepseek\",\"name\":\"DeepSeek\",\"endpoint\":\"https://api.deepseek.com\",\"model\":\"deepseek-chat\"}," +
                "{\"id\":\"openai\",\"name\":\"OpenAI\",\"endpoint\":\"https://api.openai.com/v1\",\"model\":\"gpt-4o-mini\"}," +
                "{\"id\":\"dashscope\",\"name\":\"通义千问（阿里云）\",\"endpoint\":\"https://dashscope.aliyuncs.com/compatible-mode/v1\",\"model\":\"qwen-plus\"}," +
                "{\"id\":\"zhipu\",\"name\":\"智谱 GLM\",\"endpoint\":\"https://open.bigmodel.cn/api/paas/v4\",\"model\":\"glm-4.6\"}," +
                "{\"id\":\"moonshot\",\"name\":\"Moonshot Kimi\",\"endpoint\":\"https://api.moonshot.cn/v1\",\"model\":\"kimi-k2.6\"}," +
                "{\"id\":\"custom\",\"name\":\"自定义（OpenAI 兼容）\",\"endpoint\":\"\",\"model\":\"\"}" +
                "]}";
        }

        private static string StripFences(string s)
        {
            s = (s ?? "").Trim();
            if (!s.StartsWith("```")) return s;
            int nl = s.IndexOf('\n');
            if (nl > 0) s = s.Substring(nl + 1);
            int end = s.LastIndexOf("```", StringComparison.Ordinal);
            if (end >= 0) s = s.Substring(0, end);
            return s.Trim();
        }

        private static bool ReadRequest(NetworkStream ns, out string method, out string path,
                                        out Dictionary<string, string> headers, out string body)
        {
            method = ""; path = ""; body = "";
            headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            MemoryStream ms = new MemoryStream();
            byte[] one = new byte[1];
            int blank = 0;
            while (ms.Length < 65536)
            {
                int n = ns.Read(one, 0, 1);
                if (n <= 0) break;
                ms.WriteByte(one[0]);
                if (one[0] == (byte)'\n') { if (++blank == 2) break; }
                else if (one[0] != (byte)'\r') blank = 0;
            }
            string head = Encoding.UTF8.GetString(ms.ToArray());
            string[] lines = head.Split(new string[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length == 0) return false;

            string[] parts = lines[0].Split(' ');
            if (parts.Length < 2) return false;
            method = parts[0].ToUpperInvariant();
            string raw = parts[1];
            int q = raw.IndexOf('?');
            string query = q >= 0 ? raw.Substring(q + 1) : "";
            path = q >= 0 ? raw.Substring(0, q) : raw;
            headers["__query"] = query;

            for (int i = 1; i < lines.Length; i++)
            {
                int c = lines[i].IndexOf(':');
                if (c > 0) headers[lines[i].Substring(0, c).Trim()] = lines[i].Substring(c + 1).Trim();
            }

            int len = 0;
            string cl;
            if (headers.TryGetValue("Content-Length", out cl)) int.TryParse(cl, out len);
            if (len > 0 && len < 4 * 1024 * 1024)
            {
                byte[] buf = new byte[len];
                int read = 0;
                while (read < len)
                {
                    int n = ns.Read(buf, read, len - read);
                    if (n <= 0) break;
                    read += n;
                }
                body = Encoding.UTF8.GetString(buf, 0, read);
            }
            return true;
        }

        private static void WriteHtml(NetworkStream ns, int code, string html)
        {
            byte[] body = Encoding.UTF8.GetBytes(html ?? "");
            string head = "HTTP/1.1 " + code + " OK\r\n" +
                          "Content-Type: text/html; charset=utf-8\r\n" +
                          "Content-Length: " + body.Length + "\r\n" +
                          "Cache-Control: no-store\r\n" +
                          "Connection: close\r\n\r\n";
            byte[] hb = Encoding.ASCII.GetBytes(head);
            ns.Write(hb, 0, hb.Length);
            if (body.Length > 0) ns.Write(body, 0, body.Length);
        }

        private static void WriteJson(NetworkStream ns, int code, string json)
        {
            byte[] body = Encoding.UTF8.GetBytes(json ?? "");
            string head = "HTTP/1.1 " + code + " " + Reason(code) + "\r\n" +
                          "Content-Type: application/json; charset=utf-8\r\n" +
                          "Content-Length: " + body.Length + "\r\n" +
                          "Access-Control-Allow-Origin: *\r\n" +
                          "Access-Control-Allow-Headers: Content-Type, X-Auth-Token\r\n" +
                          "Access-Control-Allow-Methods: GET, POST, OPTIONS\r\n" +
                          "Connection: close\r\n\r\n";
            byte[] hb = Encoding.ASCII.GetBytes(head);
            ns.Write(hb, 0, hb.Length);
            if (body.Length > 0) ns.Write(body, 0, body.Length);
        }

        private static string Reason(int code)
        {
            switch (code)
            {
                case 200: return "OK";
                case 204: return "No Content";
                case 400: return "Bad Request";
                case 401: return "Unauthorized";
                default: return "OK";
            }
        }

        private static string QueryValue(string query, string key)
        {
            if (string.IsNullOrEmpty(query)) return "";
            string[] parts = query.Split('&');
            for (int i = 0; i < parts.Length; i++)
            {
                int eq = parts[i].IndexOf('=');
                if (eq <= 0) continue;
                if (parts[i].Substring(0, eq) == key)
                {
                    try { return Uri.UnescapeDataString(parts[i].Substring(eq + 1)); }
                    catch { return parts[i].Substring(eq + 1); }
                }
            }
            return "";
        }
    }
}
