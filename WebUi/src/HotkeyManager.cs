using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace RichTextGen
{
    /// <summary>
    /// 全局热键注册。方案（5.7.0.2 起）：
    ///   Ctrl+F2        隐藏窗口（收至后台，用托盘图标唤回）
    ///   Ctrl+Alt+O     导入 TXT（完成后自动隐藏）
    ///   Ctrl+Alt+G     重新生成（完成后自动隐藏）
    ///   Ctrl+Alt+F4    发送到游戏（发送前弹确认）
    /// 已移除 Ctrl+Alt+T（与 Ctrl+F2 重复）、Ctrl+Alt+C（复制），减少系统级热键冲突。
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

        private struct HotkeyDef
        {
            public string Action;
            public string Desc;
            public Keys Keys;
        }

        // 单一来源：注册内容、状态文案、界面提示都由这张表派生，避免各处对不上
        private static readonly HotkeyDef[] Defs =
        {
            new HotkeyDef { Action = "hide",     Keys = Keys.Control | Keys.F2,
                            Desc = "Ctrl+F2 隐藏窗口（收至后台，托盘图标可唤回）" },
            new HotkeyDef { Action = "import",   Keys = Keys.Control | Keys.Alt | Keys.O,
                            Desc = "Ctrl+Alt+O 导入 TXT（完成后自动隐藏）" },
            new HotkeyDef { Action = "generate", Keys = Keys.Control | Keys.Alt | Keys.G,
                            Desc = "Ctrl+Alt+G 重新生成（完成后自动隐藏）" },
            new HotkeyDef { Action = "send",     Keys = Keys.Control | Keys.Alt | Keys.F4,
                            Desc = "Ctrl+Alt+F4 发送到游戏（发送前弹确认）" }
        };

        private readonly Form owner;
        private readonly Action<string> callback;
        private readonly Dictionary<int, string> actions = new Dictionary<int, string>();
        private readonly List<string> failed = new List<string>();
        private int nextId;

        /// <summary>热键说明（供界面显示，随 Defs 自动变化）</summary>
        internal static string KeyList()
        {
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < Defs.Length; i++)
            {
                if (i > 0) sb.Append(" · ");
                sb.Append(Defs[i].Desc);
            }
            return sb.ToString();
        }

        internal string StatusText
        {
            get
            {
                int total = Defs.Length;
                if (failed.Count == 0) return "已注册 " + actions.Count + "/" + total + " 个全局热键";
                return "已注册 " + actions.Count + "/" + total + "；被占用或注册失败：" + string.Join("、", failed.ToArray());
            }
        }

        internal HotkeyManager(Form owner, Action<string> callback)
        {
            this.owner = owner;
            this.callback = callback;
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
            for (int i = 0; i < Defs.Length; i++) Register(Defs[i]);
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
                    failed.Add(def.Desc);
            }
            catch { failed.Add(def.Desc); }
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint key);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    }
}
