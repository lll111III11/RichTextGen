using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace RichTextGen
{
    /// <summary>被记录过的一个应用（按进程名去重）。</summary>
    internal class AppEntry
    {
        public string Exe { get; set; }        // 进程名，如 SCPSL.exe（小写比较）
        public string Title { get; set; }      // 最近一次的窗口标题
        public long Last { get; set; }         // 最近使用时间（Unix 秒）
        public int Count { get; set; }         // 命中次数
        public bool On { get; set; }           // 在白名单/黑名单里是否勾选
        public bool Force { get; set; }        // 强制弹出：不做 UIA 检测（Unity 游戏等识别不到输入控件时用）
    }

    /// <summary>名单文件（%APPDATA%\RichTextGen\apps.json）的根对象。</summary>
    internal class AppRulesData
    {
        public string Mode { get; set; }
        public List<AppEntry> Apps { get; set; }
    }

    /// <summary>
    /// 浮动输入面板的应用名单：记录点过输入框的应用，支持三种模式
    ///   all   —— 所有应用都生效
    ///   white —— 只对勾选的应用生效
    ///   black —— 对勾选的应用不生效
    /// 名单自动记录（每点一次输入框就更新计数与时间），用户可在名单窗口里勾选、手动添加、
    /// 或对识别不到输入控件的应用（Unity 游戏控制台等）开启「强制」。
    ///
    /// 存盘做了三重加固，避免名单丢失：
    ///   1) 先写 .tmp 再原子替换，进程中途被杀也不会留下半截 JSON；
    ///   2) 每次成功写入前把旧文件留成 .bak，读取失败时自动回退；
    ///   3) 退出、开关切换、名单窗口关闭时都会主动 Flush。
    /// </summary>
    internal static class AppRules
    {
        public const string ModeAll = "all";
        public const string ModeWhite = "white";
        public const string ModeBlack = "black";

        public static string Mode = ModeAll;
        public static readonly List<AppEntry> Apps = new List<AppEntry>();

        private static bool _loaded;
        private static readonly object _lock = new object();
        private static Timer _saveTimer;
        private static bool _dirty;

        private static string Dir
        {
            get
            {
                string d = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RichTextGen");
                try { Directory.CreateDirectory(d); } catch { }
                return d;
            }
        }

        private static string FilePath { get { return Path.Combine(Dir, "apps.json"); } }
        private static string BakPath { get { return Path.Combine(Dir, "apps.bak.json"); } }
        private static string TmpPath { get { return Path.Combine(Dir, "apps.tmp.json"); } }

        public static void Load()
        {
            lock (_lock)
            {
                if (_loaded) return;
                _loaded = true;

                // 正常文件 → 失败则回退备份，任一成功即返回
                if (TryLoad(FilePath)) return;
                if (TryLoad(BakPath)) return;
            }
        }

        private static bool TryLoad(string path)
        {
            try
            {
                if (!File.Exists(path)) return false;
                string raw = File.ReadAllText(path);
                if (string.IsNullOrEmpty(raw.Trim())) return false;

                JavaScriptSerializer js = new JavaScriptSerializer();
                AppRulesData d = js.Deserialize<AppRulesData>(raw);
                if (d == null) return false;

                if (d.Mode == ModeWhite || d.Mode == ModeBlack || d.Mode == ModeAll) Mode = d.Mode;
                if (d.Apps == null) return true;
                Apps.Clear();
                foreach (AppEntry e in d.Apps)
                {
                    if (e == null || string.IsNullOrEmpty(e.Exe)) continue;
                    e.Exe = e.Exe.Trim().ToLowerInvariant();
                    Apps.Add(e);
                }
                return true;
            }
            catch { return false; }
        }

        private static void Ensure()
        {
            if (!_loaded) Load();
        }

        public static AppEntry Find(string exe)
        {
            if (string.IsNullOrEmpty(exe)) return null;
            string k = exe.Trim().ToLowerInvariant();
            for (int i = 0; i < Apps.Count; i++)
                if (string.Equals(Apps[i].Exe, k, StringComparison.Ordinal)) return Apps[i];
            return null;
        }

        /// <summary>记录一次命中（去重到进程名），不立即写盘。</summary>
        public static void Touch(string exe, string title)
        {
            if (string.IsNullOrEmpty(exe)) return;
            Ensure();
            string k = exe.Trim().ToLowerInvariant();
            lock (_lock)
            {
                AppEntry e = Find(k);
                if (e == null)
                {
                    e = new AppEntry { Exe = k, Title = title ?? "", Count = 0, On = false };
                    Apps.Add(e);
                }
                if (!string.IsNullOrEmpty(title)) e.Title = title;
                e.Count++;
                e.Last = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                ScheduleSave();
            }
        }

        /// <summary>当前模式是否允许对该应用弹出面板。</summary>
        public static bool IsAllowed(string exe)
        {
            Ensure();
            if (Mode == ModeAll) return true;
            if (string.IsNullOrEmpty(exe)) return true;   // 拿不到进程名就放行，避免完全不可用
            AppEntry e = Find(exe);
            bool on = e != null && e.On;
            return Mode == ModeWhite ? on : !on;
        }

        /// <summary>该应用是否开启了「强制」——不做 UIA 检测，点击就弹面板。</summary>
        public static bool IsForced(string exe)
        {
            Ensure();
            AppEntry e = Find(exe);
            return e != null && e.Force;
        }

        public static void SetMode(string mode)
        {
            Ensure();
            if (mode != ModeWhite && mode != ModeBlack && mode != ModeAll) return;
            Mode = mode;
            ScheduleSave();
            Flush();                     // 模式切换立刻落盘，防止意外退出丢失
        }

        public static void SetEnabled(string exe, bool on)
        {
            Ensure();
            AppEntry e = EnsureEntry(exe);
            if (e == null) return;
            e.On = on;
            ScheduleSave();
        }

        public static void SetForce(string exe, bool force)
        {
            Ensure();
            AppEntry e = EnsureEntry(exe);
            if (e == null) return;
            e.Force = force;
            ScheduleSave();
        }

        /// <summary>手动添加一个应用（用户填 exe 名，如 SCPSL.exe）。</summary>
        public static bool AddManual(string exe, string title, bool force)
        {
            Ensure();
            AppEntry e = EnsureEntry(exe);
            if (e == null) return false;
            if (!string.IsNullOrEmpty(title)) e.Title = title;
            e.On = true;
            if (force) e.Force = true;
            ScheduleSave();
            Flush();
            return true;
        }

        private static AppEntry EnsureEntry(string exe)
        {
            if (string.IsNullOrEmpty(exe)) return null;
            string k = exe.Trim().ToLowerInvariant();
            if (k.Length == 0) return null;
            AppEntry e = Find(k);
            if (e == null)
            {
                e = new AppEntry { Exe = k, Title = "", Count = 0, On = false };
                Apps.Add(e);
            }
            return e;
        }

        public static void Remove(string exe)
        {
            Ensure();
            AppEntry e = Find(exe);
            if (e != null) { Apps.Remove(e); ScheduleSave(); }
        }

        /// <summary>清掉名单里没勾选、也没什么使用记录的应用。</summary>
        public static int Purge()
        {
            Ensure();
            int n = 0;
            for (int i = Apps.Count - 1; i >= 0; i--)
            {
                if (!Apps[i].On && !Apps[i].Force) { Apps.RemoveAt(i); n++; }
            }
            if (n > 0) ScheduleSave();
            return n;
        }

        private static void ScheduleSave()
        {
            _dirty = true;
            lock (_lock)
            {
                if (_saveTimer == null)
                    _saveTimer = new Timer(delegate { Flush(); }, null, 400, Timeout.Infinite);
                else
                    _saveTimer.Change(400, Timeout.Infinite);
            }
        }

        /// <summary>把当前名单写盘（原子替换 + 留一份备份）。</summary>
        public static void Flush()
        {
            lock (_lock)
            {
                if (!_dirty) return;
                _dirty = false;
                try
                {
                    AppRulesData d = new AppRulesData { Mode = Mode, Apps = Apps };
                    JavaScriptSerializer js = new JavaScriptSerializer();
                    string body = js.Serialize(d);

                    string path = FilePath, bak = BakPath, tmp = TmpPath;
                    File.WriteAllText(tmp, body, new UTF8Encoding(false));

                    if (File.Exists(path))
                    {
                        // 旧文件留成备份，再把新文件原子替换过去
                        try
                        {
                            if (File.Exists(bak)) File.Delete(bak);
                            File.Copy(path, bak, true);
                        }
                        catch { }
                        try { File.Replace(tmp, path, null); }
                        catch
                        {
                            // 某些文件系统不支持 Replace，退化为覆盖
                            try { File.Copy(tmp, path, true); File.Delete(tmp); } catch { }
                        }
                    }
                    else
                    {
                        try { File.Move(tmp, path); }
                        catch { try { File.Copy(tmp, path, true); File.Delete(tmp); } catch { } }
                    }
                }
                catch
                {
                    _dirty = true;      // 写失败保留脏标记，下次再试
                }
            }
        }

        /// <summary>给名单窗口的完整状态（含使用次数与最近时间）。</summary>
        public static string ToJson()
        {
            Ensure();
            lock (_lock)
            {
                List<AppEntry> sorted = new List<AppEntry>(Apps);
                sorted.Sort(delegate (AppEntry a, AppEntry b) { return b.Last.CompareTo(a.Last); });

                StringBuilder sb = new StringBuilder();
                sb.Append("{\"mode\":\"").Append(Mode).Append("\",\"apps\":[");
                for (int i = 0; i < sorted.Count; i++)
                {
                    AppEntry e = sorted[i];
                    if (i > 0) sb.Append(',');
                    sb.Append("{\"exe\":").Append(Q(e.Exe))
                      .Append(",\"title\":").Append(Q(e.Title))
                      .Append(",\"last\":").Append(e.Last)
                      .Append(",\"count\":").Append(e.Count)
                      .Append(",\"on\":").Append(e.On ? "true" : "false")
                      .Append(",\"force\":").Append(e.Force ? "true" : "false")
                      .Append('}');
                }
                sb.Append("]}");
                return sb.ToString();
            }
        }

        private static string Q(string s)
        {
            if (s == null) return "\"\"";
            StringBuilder sb = new StringBuilder("\"");
            foreach (char ch in s)
            {
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
}