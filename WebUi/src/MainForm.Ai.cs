using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

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
                sb.Append('}');
                File.WriteAllText(FilePath, sb.ToString(), new UTF8Encoding(false));
            }
            catch { }
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
            sb.Append('}');
            return sb.ToString();
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
            int n;
            if (JInt(json, "port", out n) && n > 0 && n < 65536) Port = n;
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

    internal class AiSyncResult
    {
        public bool Ok;
        public string Content = "";
        public string Error = "";
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
            AiConfig.FromJson(json);
            AiConfig.Save();
            CallStr("aiSaved", "AI 设置已保存");
        }

        // ============================================================ 状态同步
        public string GetAiState() { return string.IsNullOrEmpty(_aiState) ? "{}" : _aiState; }

        private void SetAiState(string json)
        {
            if (!string.IsNullOrEmpty(json) && json.TrimStart().StartsWith("{")) _aiState = json;
        }

        // ============================================================ 界面聊天（异步）
        /// <summary>JS 发起：把状态 + 用户要求发给模型，结果回调 ui.aiReply。</summary>
        public void AiChat(string json)
        {
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    string message = Field(json, "message");
                    string state = Field(json, "state");
                    string endpoint = AiConfig.Endpoint;
                    string key = AiConfig.Key;
                    string model = AiConfig.Model;
                    string provider = AiConfig.Provider;

                    string tmp = Field(json, "endpoint"); if (tmp.Length > 0) endpoint = tmp;
                    tmp = Field(json, "key"); if (tmp.Length > 0) key = tmp;
                    tmp = Field(json, "model"); if (tmp.Length > 0) model = tmp;
                    tmp = Field(json, "provider"); if (tmp.Length > 0) provider = tmp;

                    AiSyncResult r = RequestAi(endpoint, key, model, provider, message,
                        string.IsNullOrEmpty(state) ? _aiState : state);
                    if (!r.Ok) { Call("aiReply", "{\"ok\":false,\"content\":" + Quote(r.Error) + "}"); return; }
                    Call("aiReply", "{\"ok\":true,\"content\":" + Quote(r.Content) + "}");
                    CallStr("status", "AI 已回复（Beta）");
                }
                catch (Exception ex)
                {
                    Call("aiReply", "{\"ok\":false,\"content\":" + Quote("请求出错：" + ex.Message) + "}");
                }
            });
        }

        /// <summary>端口服务调用：同步返回模型原始内容。</summary>
        internal AiSyncResult AiChatSync(string message)
        {
            return RequestAi(AiConfig.Endpoint, AiConfig.Key, AiConfig.Model, AiConfig.Provider, message, _aiState);
        }

        // ============================================================ 模型请求
        private static string BuildSystemPrompt(string state, string provider, string model)
        {
            string who = string.IsNullOrEmpty(provider) ? "你当前接入的 AI 服务商" : provider;
            string mdl = string.IsNullOrEmpty(model) ? "" : "（模型：" + model + "）";
            return
                "你是一个通用 AI 助手" + mdl + "，由 " + who + " 提供。\n" +
                "你现在运行在「彩色文本生成器」这个工具里，可以通过下面的 actions 直接操作编辑器。\n" +
                "身份要求：当用户问你是谁、你是什么模型、来自哪个平台时，如实说明你的真实模型与提供方；" +
                "不要自称是「彩色文本生成器」自研的模型，也不要把工具名当成你的身份。\n\n" +
                "当前编辑器状态 JSON：\n" + (string.IsNullOrEmpty(state) ? "{}" : state) + "\n\n" +
                "请只返回一个 JSON 对象，不要 Markdown 代码围栏，格式：\n" +
                "{\"reply\":\"给用户的简短中文说明\",\"actions\":[...]}\n" +
                "可用 actions：\n" +
                "- {\"type\":\"setText\",\"text\":\"...\"} 替换工作台文本\n" +
                "- {\"type\":\"appendText\",\"text\":\"...\"} 追加文本\n" +
                "- {\"type\":\"clearText\"} 清空文本\n" +
                "- {\"type\":\"setColor\",\"color\":\"#FF0000\"} 设置颜色（支持 #RRGGBB / rgb(255,0,0) / red）\n" +
                "- {\"type\":\"setMode\",\"mode\":\"hex|rgb|name\"} 切换颜色写法\n" +
                "- {\"type\":\"setStyle\",\"bold\":true,\"italic\":false,\"underline\":false,\"strike\":false,\"sub\":false,\"sup\":false,\"mark\":false}\n" +
                "- {\"type\":\"setSize\",\"size\":\"40\"} 字号\n" +
                "- {\"type\":\"setAlpha\",\"alpha\":\"FF\"} 透明度\n" +
                "- {\"type\":\"setGradient\",\"enabled\":true,\"axial\":false,\"loop\":false,\"start\":\"#FF0000\",\"end\":\"#0000FF\"}\n" +
                "- {\"type\":\"setLink\",\"url\":\"https://...\",\"text\":\"链接文字\"}\n" +
                "如果不需要修改编辑器，actions 返回空数组。不要输出 JSON 以外的任何内容。";
        }

        private static AiSyncResult RequestAi(string endpoint, string key, string model, string provider, string message, string state)
        {
            AiSyncResult r = new AiSyncResult();
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

            string sys = BuildSystemPrompt(state, provider, model);
            string messages = "[{\"role\":\"system\",\"content\":" + Quote(sys) +
                              "},{\"role\":\"user\",\"content\":" + Quote(message) + "}]";
            string body = "{\"model\":" + Quote(model) + ",\"messages\":" + messages +
                          ",\"temperature\":0.3,\"max_tokens\":2048}";

            string resp = HttpPost(endpoint, key.Trim(), body, 60000);
            if (resp.Length == 0)
            {
                r.Error = "网络不可达或接口无响应，请检查网络、Endpoint 与密钥";
                return r;
            }

            string content = ExtractContent(resp);
            if (content.Length == 0)
            {
                string err = Regex.Match(resp, "\"message\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"").Groups[1].Value;
                if (err.Length == 0) err = resp.Length > 240 ? resp.Substring(0, 240) + "…" : resp;
                r.Error = "接口返回异常：" + Unescape(err);
                return r;
            }
            r.Ok = true;
            r.Content = content;
            return r;
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

        // ============================================================ 操作回桥
        /// <summary>服务线程调用：把 actions 交给前端执行并等待结果。</summary>
        public string ExecuteAiActions(string actionsJson)
        {
            if (string.IsNullOrEmpty(actionsJson)) return "{\"ok\":true,\"applied\":[]}";
            string id = Guid.NewGuid().ToString("N");
            ManualResetEventSlim ev = new ManualResetEventSlim(false);
            lock (_aiWaiters) { _aiWaiters[id] = new object[] { ev, null }; }

            Call("aiApplyActions", "{\"id\":\"" + id + "\",\"actions\":" + actionsJson + "}");

            bool signaled = ev.Wait(8000);
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
                AiSyncResult r = _form != null ? _form.AiChatSync(message) : new AiSyncResult { Error = "no ui" };
                if (!r.Ok) { WriteJson(ns, 200, "{\"ok\":false,\"error\":" + AiConfig.J(r.Error) + "}"); return; }

                string content = StripFences(r.Content);
                string actions = AiConfig.JRaw(content, "actions");
                string reply = AiConfig.JStr(content, "reply");
                if (string.IsNullOrEmpty(reply)) reply = content;
                string applied = actions.Length > 0 && _form != null
                    ? _form.ExecuteAiActions(actions)
                    : "{\"ok\":true,\"applied\":[]}";
                WriteJson(ns, 200, "{\"ok\":true,\"reply\":" + AiConfig.J(reply) +
                    ",\"actions\":" + (actions.Length > 0 ? actions : "[]") +
                    ",\"applied\":" + applied + ",\"raw\":" + AiConfig.J(r.Content) + "}");
                return;
            }

            WriteJson(ns, 200,
                "{\"ok\":true,\"app\":\"彩色文本生成器\",\"ai\":\"beta\",\"endpoints\":[" +
                "\"GET /health\",\"GET /state\",\"GET /providers\",\"POST /action\",\"POST /chat\"]}");
        }

        private static string ProvidersJson()
        {
            return
                "{\"ok\":true,\"providers\":[" +
                "{\"id\":\"deepseek\",\"name\":\"DeepSeek\",\"endpoint\":\"https://api.deepseek.com\",\"model\":\"deepseek-chat\"}," +
                "{\"id\":\"openai\",\"name\":\"OpenAI\",\"endpoint\":\"https://api.openai.com/v1\",\"model\":\"gpt-4o-mini\"}," +
                "{\"id\":\"dashscope\",\"name\":\"通义千问（阿里云）\",\"endpoint\":\"https://dashscope.aliyuncs.com/compatible-mode/v1\",\"model\":\"qwen-plus\"}," +
                "{\"id\":\"zhipu\",\"name\":\"智谱 GLM\",\"endpoint\":\"https://open.bigmodel.cn/api/paas/v4\",\"model\":\"glm-4-flash\"}," +
                "{\"id\":\"moonshot\",\"name\":\"Moonshot Kimi\",\"endpoint\":\"https://api.moonshot.cn/v1\",\"model\":\"moonshot-v1-8k\"}," +
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
