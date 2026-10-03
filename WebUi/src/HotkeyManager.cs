using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace RichTextGen
{
    internal sealed class HotkeyManager
    {
        private const int WmHotkey = 0x0312;
        private const int IdBase = 0x5100;
        private readonly Form owner;
        private readonly Action<string> callback;
        private readonly Dictionary<int, string> actions = new Dictionary<int, string>();
        private readonly List<string> failed = new List<string>();
        private int nextId;
        internal string StatusText
        {
            get
            {
                if (failed.Count == 0) return "已注册 5/5 个全局热键";
                return "已注册 " + actions.Count + "/5；占用或失败：" + string.Join("、", failed.ToArray());
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
            Register("toggle", Keys.Control | Keys.Alt | Keys.T);
            Register("import", Keys.Control | Keys.Alt | Keys.O);
            Register("generate", Keys.Control | Keys.Alt | Keys.G);
            Register("copy", Keys.Control | Keys.Alt | Keys.C);
            Register("send", Keys.Control | Keys.Alt | Keys.S);
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

        private void Register(string action, Keys combination)
        {
            int id = IdBase + nextId++;
            uint modifiers = 0;
            Keys key = combination & Keys.KeyCode;
            Keys mods = combination & Keys.Modifiers;
            if ((mods & Keys.Control) != 0) modifiers |= 0x0002;
            if ((mods & Keys.Alt) != 0) modifiers |= 0x0001;
            if ((mods & Keys.Shift) != 0) modifiers |= 0x0004;
            try
            {
                if (RegisterHotKey(owner.Handle, id, modifiers, (uint)key))
                    actions[id] = action;
                else
                    failed.Add(action);
            }
            catch { failed.Add(action); }
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint key);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    }
}
