using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Automation;
using System.Windows.Automation.Text;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace RichTextGen
{
    /// <summary>
    /// 系统级浮动输入面板（HTML 界面版）。
    ///
    /// 交互流程（跟主程序一致的「选中 → 套格式」逻辑，但目标是任意软件的输入框）：
    ///   1. 在任何软件的输入框里点击 / 拖选文字（全局鼠标钩子检测）；
    ///   2. 光标旁弹出面板，并用 UI Automation 捕获目标窗口里的选中文字；
    ///   3. 点任意颜色 / 格式按钮 → 自动把标签套到那段选区上 → 粘贴回原窗口（自动回填）；
    ///      没有选区时自动进入「手动输入」模式：在面板里输入、像主程序一样选中后套格式，再点回填。
    ///
    /// 窗口用 ShowWithoutActivation 显示，弹出时绝不抢焦点；
    /// 回填时先把焦点交还目标窗口，再走剪贴板 + Ctrl+V，粘贴会自然覆盖原选区。
    /// </summary>
    public sealed class FloatingInputForm : Form
    {
        // ---------------- Win32 ----------------
        private const int WH_MOUSE_LL = 14;
        private const int WM_LBUTTONDOWN = 0x0201;
        private const int WM_LBUTTONUP = 0x0202;
        private const uint GA_ROOT = 2;

        private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int X; public int Y; }

        [StructLayout(LayoutKind.Sequential)]
        private struct MSLLHOOKSTRUCT
        {
            public POINT pt;
            public uint mouseData;
            public uint flags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);
        [DllImport("user32.dll")]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);
        [DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
        [DllImport("kernel32.dll")]
        private static extern IntPtr GetModuleHandle(string lpModuleName);
        [DllImport("user32.dll")]
        private static extern IntPtr WindowFromPoint(POINT p);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);
        [DllImport("user32.dll")]
        private static extern IntPtr GetAncestor(IntPtr hwnd, uint gaFlags);
        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")]
        private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, IntPtr dwExtraInfo);

        // ---- SendInput：走 Raw Input 管道，游戏（Unity / DirectInput）也能收到 ----
        private const uint INPUT_KEYBOARD = 1;
        private const uint KEYEVENTF_KEYUP = 0x0002;
        private const uint KEYEVENTF_UNICODE = 0x0004;

        [StructLayout(LayoutKind.Sequential)]
        private struct KEYBDINPUT
        {
            public ushort wVk;
            public ushort wScan;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct INPUT
        {
            public uint type;
            public KEYBDINPUT ki;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

        /// <summary>
        /// SendInput 要求 cbSize 等于系统 INPUT 结构大小（其中含 MOUSEINPUT 的 union），
        /// 用 Marshal.SizeOf(我们的精简结构) 会偏小（x86 算出来 20，系统要 28），
        /// 那样 SendInput 会直接返回 0 什么也不发。这里按平台给正确值。
        /// </summary>
        private static int InputSize { get { return IntPtr.Size == 4 ? 28 : 40; } }

        // ---------------- 静态状态（全局只挂一个钩子 / 一个面板） ----------------
        private static FloatingInputForm _inst;
        private static bool _enabled;
        private static IntPtr _hook;
        private static LowLevelMouseProc _proc;          // 必须保活，否则回调被回收
        private static System.Windows.Forms.Timer _clickTimer;
        private static Point _clickPt;
        private static Point _downPt;
        private static bool _wasDrag;
        private static int _clickSeq;
        private static Action<Point, IntPtr> _pickCallback;   // 非空 = 正在"拾取触发点"

        // ---------------- 面板实例状态 ----------------
        private readonly WebView2 _web = new WebView2();
        private AutomationElement _target;
        private string _selText;
        private string _selApp;
        private bool _isTerminal;            // 目标是不是控制台 / 终端类窗口
        private bool _hasRealSelection;      // 是否真的读到了选区（决定回填时要不要先删掉原选区）
        private IntPtr _targetRoot;          // 目标顶层窗口句柄（回填时用来把焦点还回去）
        private bool _webReady;
        private string _pendingJson;
        private readonly float _s;

        // ============================================================ 开关
        public static bool IsEnabled { get { return _enabled; } }

        public static void SetEnabled(bool on)
        {
            if (on == _enabled) return;
            if (on)
            {
                AppRules.Load();                         // 先读名单，第一次点击就要判断
                if (_inst == null || _inst.IsDisposed) _inst = new FloatingInputForm();
                IntPtr unused = _inst.Handle;            // 提前建句柄，后台线程才能 BeginInvoke
                _inst.InitWeb();                         // 预热 WebView2，第一次弹出更快
                _proc = HookProc;
                _hook = SetWindowsHookEx(WH_MOUSE_LL, _proc, GetModuleHandle(null), 0);
                _enabled = _hook != IntPtr.Zero;
                if (!_enabled) _proc = null;
            }
            else
            {
                if (_hook != IntPtr.Zero) { UnhookWindowsHookEx(_hook); _hook = IntPtr.Zero; }
                _enabled = false;
                _proc = null;
                if (_clickTimer != null) { _clickTimer.Stop(); }
                HidePanelStatic();
                AppRules.Flush();                        // 关掉开关时也把名单写盘
            }
        }

        public static void Shutdown()
        {
            SetEnabled(false);
            if (_clickTimer != null) { try { _clickTimer.Dispose(); } catch { } _clickTimer = null; }
            if (_inst != null) { try { _inst.Dispose(); } catch { } _inst = null; }
            AppRules.Flush();                            // 退出前把名单写盘
        }

        public static void HidePanelStatic()
        {
            if (_inst != null && !_inst.IsDisposed) _inst.HidePanel();
        }

        /// <summary>进入「拾取触发点」模式：下一次鼠标点击的坐标与窗口会被回调，然后自动退出该模式。</summary>
        public static void BeginPickPoint(Action<Point, IntPtr> cb)
        {
            if (cb == null) return;
            if (!_enabled) SetEnabled(true);
            if (_inst == null || _inst.IsDisposed) return;
            _pickCallback = cb;
            try { _inst.HidePanel(); } catch { }
        }

        public static void CancelPickPoint()
        {
            _pickCallback = null;
        }

        /// <summary>
        /// 全局热键触发：在鼠标位置弹出面板，不做「必须是输入框」的判断。
        /// 游戏 / 自绘界面里 UIA 看不到输入控件，热键是这类场景最可靠的开面板方式。
        /// 触发热键时不发复制快捷键（避免误触 Ctrl+C 打断目标程序），只尝试用 UIA 读选区。
        /// </summary>
        public static void ShowAtCursor()
        {
            if (!_enabled) SetEnabled(true);            // 按了热键就是想用，顺手打开
            if (_inst == null || _inst.IsDisposed) return;
            Point pt = Cursor.Position;
            ThreadPool.QueueUserWorkItem(delegate
            {
                AutomationElement target = null;
                bool term = false;
                string sel = "", app = "";
                IntPtr root = IntPtr.Zero;
                try
                {
                    IntPtr hw = WindowFromPoint(new POINT { X = pt.X, Y = pt.Y });
                    if (hw != IntPtr.Zero) root = GetAncestor(hw, GA_ROOT);
                    string exe = ExeOfHwnd(root);
                    app = TitleOfHwnd(root);
                    if (!string.IsNullOrEmpty(exe)) AppRules.Touch(exe, app);

                    try { target = FindEditable(pt, out term); } catch { }
                    if (target != null) { try { sel = ReadSelection(target); } catch { } }
                    else term = IsTerminalWindow(root);
                }
                catch { }

                try
                {
                    if (_inst == null || _inst.IsDisposed) return;
                    AutomationElement t = target;
                    string s2 = sel, a2 = app;
                    bool t2 = term;
                    IntPtr r2 = root;
                    _inst.BeginInvoke((MethodInvoker)delegate
                    {
                        try
                        {
                            if (_inst == null || _inst.IsDisposed) return;
                            _inst.ShowFor(t, pt, s2, a2, t2, r2);
                        }
                        catch { }
                    });
                }
                catch { }
            });
        }

        // ============================================================ 全局鼠标钩子
        private static IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
        {
            try
            {
                if (nCode >= 0)
                {
                    int msg = (int)wParam;
                    MSLLHOOKSTRUCT info = (MSLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(MSLLHOOKSTRUCT));
                    Point pt = new Point(info.pt.X, info.pt.Y);

                    if (msg == WM_LBUTTONDOWN)
                    {
                        _downPt = pt;                    // 记下按下位置，松开时判断是不是拖选
                        _wasDrag = false;
                    }
                    else if (msg == WM_LBUTTONUP)
                    {
                        // 拾取触发点模式：这一次点击只用来记录坐标，不弹面板
                        Action<Point, IntPtr> pick = _pickCallback;
                        if (pick != null)
                        {
                            _pickCallback = null;
                            IntPtr ph = WindowFromPoint(new POINT { X = pt.X, Y = pt.Y });
                            IntPtr pr = ph == IntPtr.Zero ? IntPtr.Zero : GetAncestor(ph, GA_ROOT);
                            try
                            {
                                if (_inst != null && !_inst.IsDisposed)
                                    _inst.BeginInvoke((MethodInvoker)delegate
                                    {
                                        try { pick(pt, pr); } catch { }
                                    });
                            }
                            catch { }
                            return CallNextHookEx(_hook, nCode, wParam, lParam);
                        }

                        if (!IsOwnWindow(pt))
                        {
                            int dx = pt.X - _downPt.X;
                            int dy = pt.Y - _downPt.Y;
                            _wasDrag = dx * dx + dy * dy > 16;   // 拖动超过 4px 视为拖选文字
                            _clickPt = pt;
                            _clickSeq++;
                            EnsureTimer();
                            _clickTimer.Stop();
                            _clickTimer.Start();         // 90ms 后再判断，等目标窗口完成聚焦
                        }
                    }
                }
            }
            catch { }
            return CallNextHookEx(_hook, nCode, wParam, lParam);
        }

        private static void EnsureTimer()
        {
            if (_clickTimer != null) return;
            _clickTimer = new System.Windows.Forms.Timer();
            _clickTimer.Interval = 90;
            _clickTimer.Tick += delegate
            {
                _clickTimer.Stop();
                OnClickSettled(_clickPt, _clickSeq);
            };
        }

        /// <summary>点在自己窗口上（面板 / 主程序 / 弹窗）不处理，避免自己弹自己。</summary>
        private static bool IsOwnWindow(Point pt)
        {
            try
            {
                IntPtr h = WindowFromPoint(new POINT { X = pt.X, Y = pt.Y });
                if (h == IntPtr.Zero) return false;
                IntPtr root = GetAncestor(h, GA_ROOT);
                uint pid;
                GetWindowThreadProcessId(root, out pid);
                return pid == (uint)Process.GetCurrentProcess().Id;
            }
            catch { return false; }
        }

        // ============================================================ 点击判定 + 选区捕获
        private static void OnClickSettled(Point pt, int seq)
        {
            if (!_enabled || _inst == null || _inst.IsDisposed) return;
            int drag = _wasDrag ? 1 : 0;
            // 趁面板还没弹，先记住点击处的顶层窗口（回填时用它把焦点还回去）
            IntPtr root = IntPtr.Zero;
            try
            {
                IntPtr hw = WindowFromPoint(new POINT { X = pt.X, Y = pt.Y });
                if (hw != IntPtr.Zero) root = GetAncestor(hw, GA_ROOT);
            }
            catch { }
            ThreadPool.QueueUserWorkItem(delegate
            {
                // 第一次点击进入未聚焦的窗口时，窗口激活/焦点可能还没就位，
                // 检测失败就隔 220ms 再试两次 —— 修复「要点两下才弹」的问题。
                AutomationElement target = null;
                bool term = false;
                for (int i = 0; i < 3; i++)
                {
                    if (!_enabled || _clickSeq != seq) return;
                    if (i > 0) Thread.Sleep(220);
                    if (!_enabled || _clickSeq != seq) return;
                    try { target = FindEditable(pt, out term); } catch { }
                    if (target != null) break;
                }

                string sel = "";
                string app = "";
                bool forced = false;

                // 触发点：该应用设了触发点后，只有点在这些点附近才响应（把触发范围缩到一个位置）
                string curExe = ExeOfHwnd(root);
                if (!string.IsNullOrEmpty(curExe) && AppRules.HasPoints(curExe) && !AppRules.HitPoint(curExe, pt))
                {
                    if (_inst != null && !_inst.IsDisposed && _inst.Visible)
                    {
                        try { _inst.BeginInvoke((MethodInvoker)delegate { _inst.HidePanel(); }); }
                        catch { }
                    }
                    return;
                }

                if (target != null)
                {
                    // 先记录名单（被排除的应用也要记，用户才能回到名单里改回来）
                    string exe = ProcessExeOf(target);
                    app = AppNameOf(target);
                    AppRules.Touch(exe, app);
                    if (!AppRules.IsAllowed(exe))
                    {
                        if (_inst != null && !_inst.IsDisposed && _inst.Visible)
                        {
                            try { _inst.BeginInvoke((MethodInvoker)delegate { _inst.HidePanel(); }); }
                            catch { }
                        }
                        return;                       // 名单不让弹 → 直接静默
                    }

                    sel = ReadSelection(target);             // 优先用 UIA 读选区
                    // 拖选了但 UIA 读不到 → 发复制快捷键兜底（终端用 Ctrl+Shift+C / Ctrl+Insert，避免 Ctrl+C 中断命令）
                    if (sel.Length == 0 && drag == 1) sel = ReadSelectionByCopy(term);
                }
                else if (root != IntPtr.Zero)
                {
                    // UIA 完全看不到输入控件（Unity 游戏控制台等）：名单里给这个应用开了「强制」就点击即弹
                    string exe = ExeOfHwnd(root);
                    if (!string.IsNullOrEmpty(exe))
                    {
                        app = TitleOfHwnd(root);
                        AppRules.Touch(exe, app);
                        if (AppRules.IsForced(exe) && AppRules.IsAllowed(exe))
                        {
                            forced = true;
                            term = IsTerminalWindow(root);
                        }
                    }
                }

                AutomationElement t = target;
                string s2 = sel, a2 = app;
                bool f2 = forced;
                IntPtr r2 = root;
                try
                {
                    if (!_enabled || _inst == null || _inst.IsDisposed) return;
                    if (_clickSeq != seq) return;            // 又点了别处，这次作废
                    _inst.BeginInvoke((MethodInvoker)delegate
                    {
                        try
                        {
                            if (!_enabled || _inst == null || _inst.IsDisposed) return;
                            if (_clickSeq != seq) return;
                            if (t == null && !f2)
                            {
                                if (_inst.Visible) _inst.HidePanel();
                                return;
                            }
                            _inst.ShowFor(t, pt, s2, a2, term, r2);
                        }
                        catch { }
                    });
                }
                catch { }
            });
        }

        /// <summary>
        /// 控制台 / 终端类窗口：UIA 树里没有 Edit / Document 控件，
        /// 但整块客户区都是输入区，需要按窗口类名特殊识别。
        /// </summary>
        private static readonly string[] TerminalClasses =
        {
            "consolewindowclass",              // cmd / PowerShell / conhost
            "cascadia_hosting_window_class",   // Windows Terminal（Win11 默认终端）
            "mintty",                          // Git Bash / MSYS2
            "putty",
            "tmobaxterm",
            "virtualconsoleclass",             // ConEmu
            "terminterface",                   // 部分第三方终端
        };

        private static bool IsTerminalWindow(IntPtr root)
        {
            if (root == IntPtr.Zero) return false;
            try
            {
                StringBuilder sb = new StringBuilder(256);
                if (GetClassName(root, sb, sb.Capacity) <= 0) return false;
                string cls = sb.ToString().Trim().ToLowerInvariant();
                foreach (string t in TerminalClasses)
                    if (cls == t) return true;
            }
            catch { }
            return false;
        }

        /// <summary>文字控件判定：Edit / Document / ComboBox 直接认；其余靠 TextPattern 兜底。</summary>
        private static bool IsTextControl(AutomationElement e)
        {
            try
            {
                ControlType t = e.GetCurrentPropertyValue(AutomationElement.ControlTypeProperty) as ControlType;
                if (t == ControlType.Edit || t == ControlType.Document || t == ControlType.ComboBox) return true;
                // 按钮 / 菜单 / 列表这类即使有 TextPattern 也不是输入区，排除
                if (t == ControlType.Button || t == ControlType.MenuItem || t == ControlType.ListItem ||
                    t == ControlType.Hyperlink || t == ControlType.TabItem) return false;
                // Chromium 的 contenteditable（Trae / VS Code / 网页编辑器）常被暴露成普通 Pane，
                // 但支持 TextPattern；这类也要认，否则点了没反应。
                object tp = e.GetCurrentPropertyValue(AutomationElement.IsTextPatternAvailableProperty);
                if (tp is bool && (bool)tp) return true;
                object vp = e.GetCurrentPropertyValue(AutomationElement.IsValuePatternAvailableProperty);
                if (vp is bool && (bool)vp)
                {
                    // 有值但只读的不算（如只读输入框、进度文本）
                    try
                    {
                        ValuePattern p = e.GetCurrentPattern(ValuePattern.Pattern) as ValuePattern;
                        if (p != null && !p.Current.IsReadOnly) return true;
                    }
                    catch { }
                }
            }
            catch { }
            return false;
        }

        /// <summary>
        /// 从点击点向上找 6 层，命中可输入控件即返回。
        /// 终端窗口没有 Edit/Document，按类名兜底返回点击处的元素。
        /// </summary>
        private static AutomationElement FindEditable(Point pt, out bool isTerminal)
        {
            isTerminal = false;
            try
            {
                IntPtr hwnd = WindowFromPoint(new POINT { X = pt.X, Y = pt.Y });
                IntPtr root = hwnd == IntPtr.Zero ? IntPtr.Zero : GetAncestor(hwnd, GA_ROOT);
                bool term = IsTerminalWindow(root);

                AutomationElement hit = AutomationElement.FromPoint(new System.Windows.Point(pt.X, pt.Y));
                AutomationElement e = hit;
                for (int i = 0; e != null && i < 6; i++)
                {
                    if (IsTextControl(e))
                    {
                        try
                        {
                            if (e.Current.IsEnabled)
                            {
                                isTerminal = term;      // 终端窗口即使命中了文字控件，也按终端方式复制 / 粘贴
                                return e;
                            }
                        }
                        catch { }
                    }
                    try { e = TreeWalker.ControlViewWalker.GetParent(e); }
                    catch { e = null; }
                }

                // 控制台 / 终端：整窗可输入
                if (term)
                {
                    isTerminal = true;
                    if (hit != null) return hit;
                    if (root != IntPtr.Zero)
                    {
                        try { return AutomationElement.FromHandle(root); } catch { }
                    }
                }
            }
            catch { }
            return null;
        }

        /// <summary>用 TextPattern 读目标窗口当前选中的文字（不碰剪贴板、不抢焦点）。</summary>
        private static string ReadSelection(AutomationElement target)
        {
            try
            {
                TextPattern tp = target.GetCurrentPattern(TextPattern.Pattern) as TextPattern;
                if (tp == null) return "";
                TextPatternRange[] ranges = tp.GetSelection();
                if (ranges == null || ranges.Length == 0) return "";
                StringBuilder sb = new StringBuilder();
                foreach (TextPatternRange r in ranges)
                {
                    string t = r.GetText(-1);
                    if (t != null) sb.Append(t);
                }
                return sb.ToString();
            }
            catch { return ""; }
        }

        /// <summary>
        /// 兜底：向当前焦点窗口发复制快捷键读取选区（会保护并恢复原剪贴板）。
        /// 普通应用用 Ctrl+C；Windows Terminal 用 Ctrl+Shift+C；传统控制台用 Ctrl+Insert 更安全
        /// （控制台在无选区时按 Ctrl+C 会发送中断符，所以终端类把 Ctrl+C 放到最后）。
        /// </summary>
        private static string ReadSelectionByCopy(bool isTerminal)
        {
            string old = "";
            try { old = Clipboard.GetText(); } catch { }

            byte[][] normal =
            {
                new byte[] { 0x11, 0x43 },          // Ctrl+C
                new byte[] { 0x11, 0x10, 0x43 },    // Ctrl+Shift+C
                new byte[] { 0x11, 0x2D },          // Ctrl+Insert
            };
            byte[][] terminal =
            {
                new byte[] { 0x11, 0x10, 0x43 },    // Ctrl+Shift+C（Windows Terminal 的复制）
                new byte[] { 0x11, 0x2D },          // Ctrl+Insert（传统控制台）
                new byte[] { 0x11, 0x43 },          // Ctrl+C（有选区时也是复制）
            };
            byte[][] combos = isTerminal ? terminal : normal;

            try
            {
                foreach (byte[] c in combos)
                {
                    SendKeys(c);
                    Thread.Sleep(120);
                    string cur = "";
                    try { cur = Clipboard.GetText(); } catch { }
                    if (!string.IsNullOrEmpty(cur) && cur != old)
                    {
                        try { Clipboard.SetText(old); } catch { }
                        return cur;
                    }
                }
            }
            catch { }
            try { Clipboard.SetText(old); } catch { }
            return "";
        }

        /// <summary>按顺序按下并逆序抬起一组虚拟键（修饰键在前）。</summary>
        private static void SendKeys(byte[] vks)
        {
            for (int i = 0; i < vks.Length; i++) keybd_event(vks[i], 0, 0, IntPtr.Zero);
            for (int i = vks.Length - 1; i >= 0; i--) keybd_event(vks[i], 0, 2, IntPtr.Zero);
        }

        /// <summary>窗口句柄 → 进程名（如 scpsl.exe）。不依赖 UIA，Unity 游戏也能拿到。</summary>
        private static string ExeOfHwnd(IntPtr hwnd)
        {
            try
            {
                if (hwnd == IntPtr.Zero) return "";
                uint pid;
                GetWindowThreadProcessId(hwnd, out pid);
                if (pid == 0) return "";
                using (Process p = Process.GetProcessById((int)pid))
                    return (p.ProcessName ?? "").ToLowerInvariant() + ".exe";
            }
            catch { return ""; }
        }

        /// <summary>窗口句柄 → 窗口标题。</summary>
        private static string TitleOfHwnd(IntPtr hwnd)
        {
            try
            {
                if (hwnd == IntPtr.Zero) return "";
                StringBuilder sb = new StringBuilder(512);
                if (GetWindowText(hwnd, sb, sb.Capacity) <= 0) return "";
                return sb.ToString();
            }
            catch { return ""; }
        }

        /// <summary>目标元素所属进程的可执行文件名（如 SCPSL.exe），名单以此为准。</summary>
        private static string ProcessExeOf(AutomationElement target)
        {
            try
            {
                int pid = target.Current.ProcessId;
                using (Process p = Process.GetProcessById(pid))
                    return (p.ProcessName ?? "").ToLowerInvariant() + ".exe";
            }
            catch { return ""; }
        }

        /// <summary>取目标窗口的顶层窗口标题，作为面板里显示的来源名。</summary>
        private static string AppNameOf(AutomationElement target)
        {
            try
            {
                int h = target.Current.NativeWindowHandle;
                if (h != 0)
                {
                    IntPtr root = GetAncestor((IntPtr)h, GA_ROOT);
                    if (root != IntPtr.Zero)
                    {
                        AutomationElement top = AutomationElement.FromHandle(root);
                        if (top != null)
                        {
                            string n = top.Current.Name;
                            if (!string.IsNullOrEmpty(n)) return n;
                        }
                    }
                }
            }
            catch { }
            try
            {
                string pn = target.Current.ProcessId.ToString();
                foreach (Process p in Process.GetProcesses())
                {
                    if (p.Id.ToString() == pn)
                    {
                        string name = SafeProcessName(p.ProcessName);
                        if (name.Length > 0) return name;
                    }
                }
            }
            catch { }
            return "目标窗口";
        }

        private static string SafeProcessName(string n)
        {
            if (string.IsNullOrEmpty(n)) return "";
            if (n.Equals("notepad", StringComparison.OrdinalIgnoreCase)) return "记事本";
            if (n.Equals("msedge", StringComparison.OrdinalIgnoreCase) ||
                n.Equals("chrome", StringComparison.OrdinalIgnoreCase)) return "浏览器";
            if (n.Equals("explorer", StringComparison.OrdinalIgnoreCase)) return "资源管理器";
            return n;
        }

        private static void SendCtrlV()
        {
            keybd_event(0x11, 0, 0, IntPtr.Zero);   // Ctrl ↓
            keybd_event(0x56, 0, 0, IntPtr.Zero);   // V ↓
            keybd_event(0x56, 0, 2, IntPtr.Zero);   // V ↑
            keybd_event(0x11, 0, 2, IntPtr.Zero);   // Ctrl ↑
        }

        // ============================================================ 键盘输入（SendInput）
        /// <summary>
        /// 用 SendInput + KEYEVENTF_UNICODE 把文字直接"打"进目标窗口。
        /// 这条路走 Raw Input 管道，Unity 游戏 / DirectInput 程序也能收到；
        /// 而 keybd_event 走的是旧的窗口消息队列，游戏根本不读那条队列。
        /// 换行转成 VK_RETURN，其余字符按 Unicode 逐字发送。
        /// </summary>
        private static bool TypeText(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            try
            {
                int okCount = 0;
                for (int i = 0; i < text.Length; i++)
                {
                    char ch = text[i];
                    if (ch == '\r') continue;

                    INPUT[] pair;
                    if (ch == '\n')
                        pair = new INPUT[] { VkInput(0x0D, false), VkInput(0x0D, true) };
                    else
                        pair = new INPUT[] { UniInput(ch, false), UniInput(ch, true) };

                    // 逐字符发送：一次塞整串时系统/目标程序会丢事件（实测会漏掉前半段）
                    uint sent = SendInput(2, pair, InputSize);
                    if (sent == 2) okCount++;
                    Thread.Sleep(6);
                }
                return okCount > 0;
            }
            catch { return false; }
        }

        private static INPUT UniInput(char ch, bool up)
        {
            INPUT i = new INPUT();
            i.type = INPUT_KEYBOARD;
            i.ki.wVk = 0;
            i.ki.wScan = ch;
            i.ki.dwFlags = KEYEVENTF_UNICODE | (up ? KEYEVENTF_KEYUP : 0);
            return i;
        }

        private static INPUT VkInput(ushort vk, bool up)
        {
            INPUT i = new INPUT();
            i.type = INPUT_KEYBOARD;
            i.ki.wVk = vk;
            i.ki.wScan = 0;
            i.ki.dwFlags = up ? KEYEVENTF_KEYUP : 0;
            return i;
        }

        /// <summary>发送一个功能键（如回车），同样走 SendInput 管道。</summary>
        private static void SendVkKey(ushort vk)
        {
            try
            {
                INPUT[] arr = new INPUT[] { VkInput(vk, false), VkInput(vk, true) };
                SendInput((uint)arr.Length, arr, InputSize);
            }
            catch { }
        }

        // ============================================================ 面板本体
        private FloatingInputForm()
        {
            int dpi = 96;
            try { dpi = DeviceDpi; } catch { }
            _s = Math.Max(0.8f, Math.Min(2.5f, dpi / 96f));

            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            AutoScaleMode = AutoScaleMode.None;
            BackColor = Color.FromArgb(243, 243, 243);
            ClientSize = new Size(S(560), S(150));
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);

            _web.Dock = DockStyle.Fill;
            _web.DefaultBackgroundColor = Color.FromArgb(243, 243, 243);
            Controls.Add(_web);
        }

        protected override bool ShowWithoutActivation { get { return true; } }   // 弹出绝不抢焦点

        private int S(int v) { return (int)Math.Round(v * _s); }

        private async void InitWeb()
        {
            try
            {
                if (MainForm.WebEnv != null) await _web.EnsureCoreWebView2Async(MainForm.WebEnv);
                else await _web.EnsureCoreWebView2Async();

                _web.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
                _web.CoreWebView2.Settings.IsStatusBarEnabled = false;

                const string origin = MainForm.WebOrigin;
                string html = LoadFloatHtml();
                _web.CoreWebView2.AddWebResourceRequestedFilter(origin + "*", CoreWebView2WebResourceContext.All);
                _web.CoreWebView2.WebResourceRequested += delegate (object s2, CoreWebView2WebResourceRequestedEventArgs e2)
                {
                    try
                    {
                        if (!e2.Request.Uri.StartsWith(origin, StringComparison.OrdinalIgnoreCase)) return;
                        string abs = new Uri(e2.Request.Uri).AbsolutePath;
                        byte[] body = abs.StartsWith("/__float", StringComparison.OrdinalIgnoreCase)
                            ? Encoding.UTF8.GetBytes(html)
                            : new byte[0];
                        bool ok = body.Length > 0;
                        e2.Response = _web.CoreWebView2.Environment.CreateWebResourceResponse(
                            new MemoryStream(body), ok ? 200 : 404, ok ? "OK" : "Not Found",
                            ok ? "Content-Type: text/html; charset=utf-8" : "Content-Type: text/plain");
                    }
                    catch { }
                };

                _web.CoreWebView2.NavigationCompleted += delegate { _webReady = true; PushPending(); };
                _web.CoreWebView2.WebMessageReceived += OnFMessage;
                _web.CoreWebView2.Navigate(origin + "__float.html");
            }
            catch { }
        }

        private static string LoadFloatHtml()
        {
            try
            {
                Assembly asm = Assembly.GetExecutingAssembly();
                foreach (string n in asm.GetManifestResourceNames())
                {
                    if (n.EndsWith("floating.html", StringComparison.OrdinalIgnoreCase))
                        using (Stream s = asm.GetManifestResourceStream(n))
                        using (StreamReader sr = new StreamReader(s, Encoding.UTF8))
                            return sr.ReadToEnd();
                }
            }
            catch { }
            return "<html><body>floating.html 资源缺失</body></html>";
        }

        // ============================================================ 显示 / 隐藏
        internal void ShowFor(AutomationElement target, Point pt, string selText, string app, bool isTerminal, IntPtr root)
        {
            _target = target;
            _selText = selText ?? "";
            _selApp = app ?? "";
            _isTerminal = isTerminal;
            _hasRealSelection = _selText.Length > 0;
            _targetRoot = root;
            if (!Visible) Show();
            PlaceAt(pt);
            PushState();
        }

        private void PushState()
        {
            string json = StateJson();
            if (_webReady && _web.CoreWebView2 != null)
            {
                try { _web.CoreWebView2.ExecuteScriptAsync("window.fpState && window.fpState(" + JsString(json) + ")"); return; }
                catch { }
            }
            // 失败（WebView 未就绪 / 正忙）→ 记下来，稍后重试
            _pendingJson = json;
            System.Windows.Forms.Timer t = new System.Windows.Forms.Timer();
            t.Interval = 500;
            t.Tick += delegate
            {
                try
                {
                    t.Stop(); t.Dispose();
                    if (_webReady && _web.CoreWebView2 != null && _pendingJson == json)
                    {
                        try
                        {
                            _web.CoreWebView2.ExecuteScriptAsync("window.fpState && window.fpState(" + JsString(json) + ")");
                            _pendingJson = null;
                        }
                        catch { }
                    }
                }
                catch { }
            };
            t.Start();
        }

        private void PushPending()
        {
            if (_pendingJson == null || _web.CoreWebView2 == null) return;
            string j = _pendingJson;
            _pendingJson = null;
            try { _web.CoreWebView2.ExecuteScriptAsync("window.fpState && window.fpState(" + JsString(j) + ")"); }
            catch { }
        }

        private string StateJson()
        {
            int len = _selText.Length;
            return "{\"mode\":\"" + (len > 0 ? "sel" : "type") + "\"" +
                   ",\"len\":" + len +
                   ",\"text\":" + Quote(_selText) +
                   ",\"app\":" + Quote(_selApp) + "}";
        }

        private void PlaceAt(Point pt)
        {
            Rectangle wa = Screen.FromPoint(pt).WorkingArea;
            int x = pt.X + S(16);
            int y = pt.Y + S(24);
            if (x + Width > wa.Right) x = pt.X - Width - S(16);
            if (y + Height > wa.Bottom) y = pt.Y - Height - S(12);
            x = Math.Max(wa.Left + S(4), Math.Min(x, wa.Right - Width - S(4)));
            y = Math.Max(wa.Top + S(4), Math.Min(y, wa.Bottom - Height - S(4)));
            Location = new Point(x, y);
        }

        internal void HidePanel()
        {
            if (Visible) Hide();
        }

        // ============================================================ 面板 → 宿主 消息
        private void OnFMessage(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            string raw = "";
            try { raw = e.TryGetWebMessageAsString(); } catch { }
            if (string.IsNullOrEmpty(raw)) return;
            string[] parts = raw.Split('|');
            string cmd = parts[0];
            try
            {
                switch (cmd)
                {
                    case "f":
                        DispatchF(parts);
                        break;
                }
            }
            catch { }
        }

        /// <summary>parts[1]=子命令，后面是参数（base64 编码，避免 | 转义问题）。</summary>
        private void DispatchF(string[] parts)
        {
            string sub = parts.Length > 1 ? parts[1] : "";
            switch (sub)
            {
                case "close":
                    HidePanel();
                    break;

                // 前端已经算好最终文本（含正确的标签嵌套），这里只负责粘回原窗口
                case "paste":
                    if (parts.Length >= 3) PasteBack(B64(parts[2]), _hasRealSelection);
                    break;

                case "pickSelColor":
                    PickSelColor();
                    break;

                // 新界面统一入口：选完颜色交给前端，由前端决定是替换选区还是改输入框
                case "pickColor":
                    PickSelColor();
                    break;

                case "pickColorTyp":
                    PickTypColor();
                    break;

                case "type":
                    if (parts.Length >= 3) PasteBack(B64(parts[2]), false);
                    break;

                case "copy":
                    if (parts.Length >= 3) SetClipboard(B64(parts[2]), 4);
                    break;

                case "move":
                    if (parts.Length >= 4)
                    {
                        int dx, dy;
                        if (int.TryParse(parts[2], out dx) && int.TryParse(parts[3], out dy))
                        {
                            if (Math.Abs(dx) <= 400 && Math.Abs(dy) <= 400)
                                Location = new Point(Left + dx, Top + dy);
                        }
                    }
                    break;
            }
        }

        private static string B64(string s)
        {
            try { return Encoding.UTF8.GetString(Convert.FromBase64String(s ?? "")); }
            catch { return ""; }
        }

        /// <summary>
        /// 把文字送回原输入框。两条路：
        ///   1) SendInput + KEYEVENTF_UNICODE 逐字符打字 —— 走 Raw Input 管道，游戏也能收到；
        ///   2) 剪贴板 + Ctrl+V —— 普通桌面软件更快，但游戏多半收不到。
        /// 自动模式下：强制模式 / 终端 / 短文本走键盘，其余走剪贴板。
        /// replaceSel = true 时先按一次 Delete —— 控制台的选区不会被粘贴自动替换。
        /// </summary>
        private void PasteBack(string text, bool replaceSel)
        {
            if (text.Length == 0) return;
            AutomationElement t = _target;
            IntPtr root = _targetRoot;
            bool forced = (t == null);          // 强制模式下没有 UIA 元素
            HidePanel();
            try
            {
                if (root == IntPtr.Zero && t != null)
                {
                    try
                    {
                        int h = t.Current.NativeWindowHandle;
                        if (h != 0) root = GetAncestor((IntPtr)h, GA_ROOT);
                    }
                    catch { }
                }
                if (root != IntPtr.Zero) SetForegroundWindow(root);
                if (t != null) { try { t.SetFocus(); } catch { } }
                Thread.Sleep(90);

                if (replaceSel)
                {
                    SendVkKey(0x2E);                // Delete
                    Thread.Sleep(50);
                }

                // 选输入方式：游戏（强制模式）/ 终端 一律走键盘打字（只有这条管道它们收得到）；
                // 普通桌面程序短文本也直接打字（不污染剪贴板），长文本才用粘贴。
                bool useKeyboard = forced || _isTerminal || text.Length <= 200;

                if (useKeyboard && TypeText(text)) return;

                if (!SetClipboard(text, 6)) return;
                Thread.Sleep(40);
                SendCtrlV();
            }
            catch { }
        }

        /// <summary>选区模式的自定义颜色：色板选完把 hex 交给前端套色（前端统一算嵌套）。</summary>
        private void PickSelColor()
        {
            using (ColorDialog d = new ColorDialog())
            {
                d.FullOpen = true;
                if (d.ShowDialog(this) == DialogResult.OK)
                {
                    string hex = "#" + d.Color.R.ToString("X2") + d.Color.G.ToString("X2") + d.Color.B.ToString("X2");
                    if (_webReady && _web.CoreWebView2 != null)
                        try { _web.CoreWebView2.ExecuteScriptAsync("window.fpSelColor && window.fpSelColor(" + JsString(hex) + ")"); }
                        catch { }
                }
                if (Visible) Activate();
            }
        }

        private void PickTypColor()
        {
            using (ColorDialog d = new ColorDialog())
            {
                d.FullOpen = true;
                if (d.ShowDialog(this) == DialogResult.OK)
                {
                    string hex = "#" + d.Color.R.ToString("X2") + d.Color.G.ToString("X2") + d.Color.B.ToString("X2");
                    if (_webReady && _web.CoreWebView2 != null)
                        try { _web.CoreWebView2.ExecuteScriptAsync("window.fpColorTyp && window.fpColorTyp(" + JsString(hex) + ")"); }
                        catch { }
                }
                if (Visible) Activate();
            }
        }

        private static bool SetClipboard(string s, int tries)
        {
            for (int i = 0; i < tries; i++)
            {
                try { Clipboard.SetText(s); return true; }
                catch { Thread.Sleep(50); }
            }
            return false;
        }

        // ---------------- 小工具 ----------------
        private static string Quote(string s)
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

        /// <summary>把 JSON 字符串包成 JS 字符串字面量（用于 ExecuteScriptAsync）。</summary>
        private static string JsString(string json)
        {
            StringBuilder sb = new StringBuilder("\"");
            foreach (char ch in json)
            {
                switch (ch)
                {
                    case '\\': sb.Append("\\\\"); break;
                    case '"': sb.Append("\\\""); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': break;
                    default: sb.Append(ch); break;
                }
            }
            sb.Append('"');
            return sb.ToString();
        }
    }
}