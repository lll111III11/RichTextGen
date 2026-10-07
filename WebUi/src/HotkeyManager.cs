using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace RichTextGen
{
    /// <summary>
    /// 全局热键注册（5.7.0.9 起支持自定义）。
    /// 默认方案：
    ///   Ctrl+F2        隐藏 ↔ 显示 切换（收后台后托盘图标也可唤回）
    ///   Ctrl+Alt+O     导入 TXT（完成后自动隐藏）
    ///   Ctrl+Alt+G     重新生成（完成后自动隐藏）
    /// 用户可在设置页改键，配置持久化到 %APPDATA%\RichTextGen\hotkeys.json，
    /// 下次启动自动加载；未配置的动作回退默认键。
    /// 注：注册时一律带 MOD_NOREPEAT，否则长按会连续触发。
    /// </summary>
    internal sealed class HotkeyManager
    {
        private const int WmHotkey = 0x0312;
        private const int IdBase = 0x5100;
        private const uint ModAlt = 0x0001;
        private const uint ModControl = 0x0002;
        private const uint ModShift = 0x0004;
        private const uint ModNoRepeat = 0x4000;   // 长按不连发（Vista+）

        private sealed class HotkeyDef
        {
            public string Action;
            public string Desc;
            public Keys Keys;
        }

        // 内置默认键（未自定义时使用）
        private static readonly HotkeyDef[] Defaults =
        {
            new HotkeyDef { Action = "hide",     Keys = Keys.Control | Keys.F2,
                            Desc = "隐藏 / 显示窗口（托盘图标可唤回）" },
            new HotkeyDef { Action = "import",   Keys = Keys.Control | Keys.Alt | Keys.O,
                            Desc = "导入 TXT（完成后自动隐藏）" },
            new HotkeyDef { Action = "generate", Keys = Keys.Control | Keys.Alt | Keys.G,
                            Desc = "重新生成（完成后自动隐藏）" }
        };

        private readonly Form owner;
        private readonly Action<string> callback;
        private readonly Dictionary<int, string> actions = new Dictionary<int, string>();
        private readonly List<string> failed = new List<string>();
        private readonly List<HotkeyDef> defs = new List<HotkeyDef>();
        private int nextId;

        private static string ConfigPath
        {
            get
            {
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RichTextGen");
                return Path.Combine(dir, "hotkeys.json");
            }
        }

        /// <summary>热键说明（供界面显示，随当前定义自动变化）</summary>
        internal string KeyList()
        {
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < defs.Count; i++)
            {
                if (i > 0) sb.Append(" · ");
                sb.Append(defs[i].Desc);
            }
            return sb.ToString();
        }

        /// <summary>当前全部热键定义（JSON：action / keys / desc），供界面显示与改键回显</summary>
        internal string HotkeysJson()
        {
            StringBuilder sb = new StringBuilder("[");
            for (int i = 0; i < defs.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append("{\"action\":\"").Append(defs[i].Action)
                  .Append("\",\"keys\":").Append((int)defs[i].Keys)
                  .Append(",\"desc\":\"").Append(defs[i].Desc.Replace("\"", "\\\""))
                  .Append("\"}");
            }
            sb.Append(']');
            return sb.ToString();
        }

        internal string StatusText
        {
            get
            {
                int total = defs.Count;
                if (failed.Count == 0) return "已注册 " + actions.Count + "/" + total + " 个全局热键";
                return "已注册 " + actions.Count + "/" + total + "；被占用或注册失败：" + string.Join("、", failed.ToArray());
            }
        }

        internal HotkeyManager(Form owner, Action<string> callback)
        {
            this.owner = owner;
            this.callback = callback;
            Load();
        }

        /// <summary>从本地配置加载自定义热键（缺失回退默认）</summary>
        internal void Load()
        {
            defs.Clear();
            defs.AddRange(Defaults);
            try
            {
                if (!File.Exists(ConfigPath)) return;
                string text = File.ReadAllText(ConfigPath);
                foreach (var d in defs)
                {
                    var m = Regex.Match(text, "\"" + d.Action + "\"\\s*:\\s*(\\d+)");
                    int v;
                    if (m.Success && int.TryParse(m.Groups[1].Value, out v) && v != 0)
                        d.Keys = (Keys)v;
                }
            }
            catch { }
        }

        internal void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath));
                StringBuilder sb = new StringBuilder("{");
                for (int i = 0; i < defs.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append('"').Append(defs[i].Action).Append("\":").Append((int)defs[i].Keys);
                }
                sb.Append('}');
                File.WriteAllText(ConfigPath, sb.ToString());
            }
            catch { }
        }

        /// <summary>修改某动作的快捷键；冲突/非法组合返回 false 并给出原因</summary>
        internal bool UpdateDef(string action, Keys keys, out string err)
        {
            err = null;
            Keys key = keys & Keys.KeyCode;
            Keys mods = keys & Keys.Modifiers;
            if (key == Keys.None || (mods & (Keys.Control | Keys.Alt | Keys.Shift)) == 0)
            {
                err = "快捷键需同时包含修饰键（Ctrl / Alt / Shift）与功能键";
                return false;
            }
            for (int i = 0; i < defs.Count; i++)
            {
                if (defs[i].Action != action && defs[i].Keys == keys)
                {
                    err = "该组合已被「" + defs[i].Action + "」占用";
                    return false;
                }
            }
            for (int i = 0; i < defs.Count; i++)
            {
                if (defs[i].Action == action) { defs[i].Keys = keys; break; }
            }
            Save();
            RegisterAll();
            return true;
        }

        /// <summary>恢复全部默认快捷键并持久化</summary>
        internal void ResetDefaults()
        {
            defs.Clear();
            defs.AddRange(Defaults);
            Save();
            RegisterAll();
        }

        internal bool HandleMessage(ref Message message)
        {
            if (message.Msg != WmHotkey) return false;
            string action;
            if (actions.TryGetValue(message.WParam.ToInt32(), out action))
            {
                try { callback(action); } catch { }
            }
            return true;
        }

        internal void RegisterAll()
        {
            UnregisterAll();
            nextId = 0;
            failed.Clear();
            if (!owner.IsHandleCreated) return;
            for (int i = 0; i < defs.Count; i++) Register(defs[i]);
        }

        internal void UnregisterAll()
        {
            if (!owner.IsHandleCreated)
            {
                actions.Clear();
                return;
            }
            foreach (int id in new List<int>(actions.Keys))
            {
                try { UnregisterHotKey(owner.Handle, id); } catch { }
            }
            actions.Clear();
        }

        private void Register(HotkeyDef def)
        {
            int id = IdBase + nextId++;
            uint modifiers = ModNoRepeat;
            Keys key = def.Keys & Keys.KeyCode;
            Keys mods = def.Keys & Keys.Modifiers;
            if ((mods & Keys.Control) != 0) modifiers |= ModControl;
            if ((mods & Keys.Alt) != 0) modifiers |= ModAlt;
            if ((mods & Keys.Shift) != 0) modifiers |= ModShift;
            try
            {
                if (RegisterHotKey(owner.Handle, id, modifiers, (uint)key))
                    actions[id] = def.Action;
                else
                    failed.Add(def.Desc + "（" + FormatKeys(def.Keys) + "）");
            }
            catch { failed.Add(def.Desc + "（" + FormatKeys(def.Keys) + "）"); }
        }

        /// <summary>Keys → 可读文本（如 Ctrl+Alt+G）</summary>
        internal static string FormatKeys(Keys k)
        {
            StringBuilder sb = new StringBuilder();
            Keys mods = k & Keys.Modifiers;
            if ((mods & Keys.Control) != 0) sb.Append("Ctrl+");
            if ((mods & Keys.Alt) != 0) sb.Append("Alt+");
            if ((mods & Keys.Shift) != 0) sb.Append("Shift+");
            Keys kc = k & Keys.KeyCode;
            if (kc >= Keys.F1 && kc <= Keys.F24) sb.Append("F" + (kc - Keys.F1 + 1));
            else if (kc >= Keys.A && kc <= Keys.Z) sb.Append((char)kc);
            else if (kc >= Keys.D0 && kc <= Keys.D9) sb.Append((char)kc);
            else sb.Append(kc.ToString());
            return sb.ToString();
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint key);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    }
}
