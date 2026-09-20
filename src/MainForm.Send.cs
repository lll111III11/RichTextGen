using System;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace RichTextGen
{
    public partial class MainForm
    {
        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, IntPtr pid);

        private Label lblProc;
        private TextBox txtSend;
        private CheckBox chkUseGen;
        private ComboBox cboSendPrefix;
        private TextBox txtSendPrefix;
        private RadioButton rbClip;
        private RadioButton rbType;
        private CheckBox chkKeepConsole;
        private TextBox txtSendPreview;
        private TextBox txtSendLog;
        private System.Windows.Forms.Timer timerProc;
        private GroupBox gbLogRef;

        private void BuildSendPage(TabPage page)
        {
            Panel host = new Panel();
            host.Dock = DockStyle.Fill;
            host.AutoScroll = true;
            page.Controls.Add(host);

            int y = 14;

            GroupBox gbProc = MkGroup("游戏进程", 12, y, 1000, 56);
            host.Controls.Add(gbProc);
            lblProc = MkHint("检测中…", 12, 26, gbProc);
            lblProc.AutoSize = true;
            gbProc.Controls.Add(lblProc);

            Button btnRe = new Button();
            btnRe.Text = "重新检测";
            btnRe.Location = new Point(880, 14);
            btnRe.Size = new Size(100, 28);
            btnRe.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            btnRe.Click += delegate { RefreshProcStatus(); };
            gbProc.Controls.Add(btnRe);
            y += 66;

            GroupBox gbBody = MkGroup("要发送的内容（可直接粘贴富文本代码）", 12, y, 1000, 142);
            host.Controls.Add(gbBody);
            txtSend = new TextBox();
            txtSend.Multiline = true;
            txtSend.ScrollBars = ScrollBars.Vertical;
            txtSend.Location = new Point(12, 24);
            txtSend.Size = new Size(976, 78);
            txtSend.TextChanged += delegate { UpdateSendPreview(); };
            gbBody.Controls.Add(txtSend);

            chkUseGen = new CheckBox();
            chkUseGen.Text = "使用「文本生成」页的最新结果（勾选后忽略上方输入）";
            chkUseGen.Location = new Point(12, 114);
            chkUseGen.AutoSize = true;
            chkUseGen.CheckedChanged += delegate { UpdateSendPreview(); };
            gbBody.Controls.Add(chkUseGen);

            Button btnReload = new Button();
            btnReload.Text = "读取生成结果";
            btnReload.Location = new Point(840, 108);
            btnReload.Size = new Size(120, 28);
            btnReload.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            btnReload.Click += delegate { SendReloadFromGen(); };
            gbBody.Controls.Add(btnReload);
            y += 150;

            GroupBox gbOpt = MkGroup("发送选项", 12, y, 1000, 92);
            host.Controls.Add(gbOpt);

            MkHint("指令前缀", 12, 30, gbOpt);
            cboSendPrefix = new ComboBox();
            cboSendPrefix.DropDownStyle = ComboBoxStyle.DropDownList;
            cboSendPrefix.Location = new Point(80, 26);
            cboSendPrefix.Size = new Size(150, 24);
            cboSendPrefix.Items.AddRange(new object[] { ".BC 全服广播", ".C 团队", ".NC 无前缀", "自定义（右侧）" });
            cboSendPrefix.SelectedIndex = 0;
            cboSendPrefix.SelectedIndexChanged += delegate { UpdateSendPreview(); };
            gbOpt.Controls.Add(cboSendPrefix);

            txtSendPrefix = new TextBox();
            txtSendPrefix.Location = new Point(238, 26);
            txtSendPrefix.Size = new Size(130, 24);
            txtSendPrefix.TextChanged += delegate { UpdateSendPreview(); };
            gbOpt.Controls.Add(txtSendPrefix);

            MkHint("发送方式", 400, 30, gbOpt);
            rbClip = new RadioButton();
            rbClip.Text = "剪贴板粘贴（推荐）";
            rbClip.Location = new Point(464, 26);
            rbClip.AutoSize = true;
            rbClip.Checked = true;
            gbOpt.Controls.Add(rbClip);

            rbType = new RadioButton();
            rbType.Text = "直接键入（较慢）";
            rbType.Location = new Point(614, 26);
            rbType.AutoSize = true;
            gbOpt.Controls.Add(rbType);

            chkKeepConsole = new CheckBox();
            chkKeepConsole.Text = "发送后保留游戏控制台打开";
            chkKeepConsole.Location = new Point(764, 26);
            chkKeepConsole.AutoSize = true;
            gbOpt.Controls.Add(chkKeepConsole);

            MkHint("提示：发送前请把游戏切到「窗口化」，并用管理员身份运行本程序以便正常激活游戏窗口。", 12, 62, gbOpt);
            y += 100;

            GroupBox gbPrev = MkGroup("发送预览（实际送进游戏控制台的内容）", 12, y, 1000, 84);
            host.Controls.Add(gbPrev);
            txtSendPreview = new TextBox();
            txtSendPreview.Multiline = true;
            txtSendPreview.ReadOnly = true;
            txtSendPreview.ScrollBars = ScrollBars.Vertical;
            txtSendPreview.Location = new Point(12, 24);
            txtSendPreview.Size = new Size(976, 50);
            gbPrev.Controls.Add(txtSendPreview);
            y += 92;

            Button btnSend = new Button();
            btnSend.Text = "发送到游戏";
            btnSend.Location = new Point(12, y);
            btnSend.Size = new Size(164, 34);
            btnSend.Tag = "primary";
            btnSend.Click += delegate { DoSend(); };
            host.Controls.Add(btnSend);

            Button btnCopy2 = new Button();
            btnCopy2.Text = "仅复制";
            btnCopy2.Location = new Point(186, y);
            btnCopy2.Size = new Size(100, 34);
            btnCopy2.Click += delegate
            {
                string t = BuildSendText();
                if (t.Length == 0) { status("没有可复制的内容"); return; }
                try { Clipboard.SetText(t); status("已复制发送内容"); } catch { status("复制失败"); }
            };
            host.Controls.Add(btnCopy2);
            y += 44;

            GroupBox gbLog = MkGroup("最近发送记录", 12, y, 1000, 120);
            gbLogRef = gbLog;
            host.Controls.Add(gbLog);
            txtSendLog = new TextBox();
            txtSendLog.Multiline = true;
            txtSendLog.ReadOnly = true;
            txtSendLog.ScrollBars = ScrollBars.Vertical;
            txtSendLog.Location = new Point(12, 24);
            txtSendLog.Size = new Size(976, 84);
            gbLog.Controls.Add(txtSendLog);

            host.Resize += delegate   // 组框跟着窗口宽度走，避免右侧大片空白
            {
                int w = host.ClientSize.Width - 26;
                if (w < 420) w = 420;
                foreach (Control c2 in host.Controls)
                {
                    GroupBox gb = c2 as GroupBox;
                    if (gb == null) continue;
                    if (gb == gbLogRef)
                    {
                        int hh = host.ClientSize.Height - gb.Top - 16;
                        if (hh < 120) hh = 120;
                        gb.Height = hh;
                        if (txtSendLog != null) txtSendLog.Height = gb.ClientSize.Height - 36;
                    }
                    gb.Width = w;
                    foreach (Control ic in gb.Controls)
                    {
                        TextBox tb = ic as TextBox;
                        if (tb != null && tb.Multiline) tb.Width = gb.ClientSize.Width - 24;
                    }
                }
            };

            timerProc = new System.Windows.Forms.Timer();
            timerProc.Interval = 2000;
            timerProc.Tick += delegate { RefreshProcStatus(); };
            timerProc.Start();
            RefreshProcStatus();
            if (host.ClientSize.Width > 0) host.PerformLayout();
        }

        // ---------------------------------------------------------------- 进程
        private string gameProcName = "";
        private IntPtr gameHwnd = IntPtr.Zero;

        private void RefreshProcStatus()
        {
            if (lblProc == null) return;
            string[] names = new string[] { "SCPSL", "SCP Secret Laboratory" };
            string found = null;
            IntPtr hwnd = IntPtr.Zero;
            for (int i = 0; i < names.Length; i++)
            {
                Process[] ps = Process.GetProcessesByName(names[i]);
                for (int j = 0; j < ps.Length; j++)
                {
                    if (ps[j].MainWindowHandle != IntPtr.Zero)
                    {
                        found = ps[j].ProcessName + " (PID " + ps[j].Id + ")";
                        hwnd = ps[j].MainWindowHandle;
                        break;
                    }
                    if (found == null) found = ps[j].ProcessName + " (PID " + ps[j].Id + "，窗口未就绪)";
                }
                if (hwnd != IntPtr.Zero) break;
            }

            if (hwnd != IntPtr.Zero)
            {
                gameProcName = found;
                gameHwnd = hwnd;
                lblProc.Text = "✓ 已锁定 " + found;
                lblProc.ForeColor = Color.FromArgb(0, 150, 70);
            }
            else if (found != null)
            {
                gameProcName = found;
                gameHwnd = IntPtr.Zero;
                lblProc.Text = "◐ 进程存在但无窗口：" + found;
                lblProc.ForeColor = Color.FromArgb(0, 110, 200);
            }
            else
            {
                gameProcName = "";
                gameHwnd = IntPtr.Zero;
                lblProc.Text = "✗ 未检测到 SCP:SL 进程";
                lblProc.ForeColor = Color.FromArgb(190, 60, 60);
            }
        }

        // ---------------------------------------------------------------- 发送
        internal void SendReloadFromGen()
        {
            if (txtSend == null) return;
            string gen = BuildFullCodeFromGen();
            txtSend.Text = gen;
            chkUseGen.Checked = false;
            UpdateSendPreview();
            status(gen.Length == 0 ? "生成页暂无内容" : "已读取生成页结果（" + gen.Length + " 字符）");
        }

        /// <summary>取生成页结果但不改动生成页 UI（发送页可能已切走）</summary>
        private string BuildFullCodeFromGen()
        {
            return BuildFullCode();
        }

        private string BuildSendText()
        {
            if (chkUseGen != null && chkUseGen.Checked) return BuildFullCodeFromGen();
            return txtSend == null ? "" : txtSend.Text.Trim();
        }

        private string BuildSendFull()
        {
            string body = BuildSendText();
            if (body.Length == 0) return "";
            string pref = ".BC";
            switch (cboSendPrefix.SelectedIndex)
            {
                case 0: pref = ".BC"; break;
                case 1: pref = ".C"; break;
                case 2: pref = ""; break;
                default: pref = txtSendPrefix.Text.Trim(); break;
            }
            return (pref.Length > 0) ? (pref + " " + body) : body;
        }

        private void UpdateSendPreview()
        {
            if (txtSendPreview == null) return;
            txtSendPreview.Text = BuildSendFull();
        }

        private void DoSend()
        {
            string full = BuildSendFull();
            if (full.Length == 0) { status("没有内容可发送"); return; }

            RefreshProcStatus();
            if (gameHwnd == IntPtr.Zero)
            {
                MessageBox.Show(this,
                    gameProcName.Length > 0
                        ? "检测到进程但窗口未就绪：" + gameProcName + "\n请等游戏完全启动后重试。"
                        : "未检测到 SCP:SL 进程，请先启动游戏。",
                    "未找到游戏窗口", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            IntPtr target = gameHwnd;
            ShowWindow(target, 9);   // SW_RESTORE
            SetForegroundWindow(target);
            Thread.Sleep(450);
            if (GetForegroundWindow() != target)
            {
                DialogResult r = MessageBox.Show(this,
                    "无法把游戏窗口切到前台（可能被遮挡或处于独占全屏）。\n仍要继续发送吗？\n建议游戏使用「窗口化」模式。",
                    "窗口未激活", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                if (r != DialogResult.Yes) return;
            }

            bool keep = chkKeepConsole.Checked;
            _stopRequested = false;
            try
            {
                if (_stopRequested) { status("已停止发送"); return; }
                if (rbType.Checked)
                {
                    SendKeys.SendWait("`");
                    Thread.Sleep(420);
                    SendKeys.SendWait(EscapeSendKeys(full));
                }
                else
                {
                    Clipboard.SetText(full);
                    SendKeys.SendWait("`");
                    Thread.Sleep(420);
                    SendKeys.SendWait("^a");
                    Thread.Sleep(120);
                    SendKeys.SendWait("^v");
                }
                if (_stopRequested) { status("已停止发送"); return; }
                Thread.Sleep(150);
                SendKeys.SendWait("{ENTER}");
                Thread.Sleep(220);
                if (!keep) SendKeys.SendWait("`");
            }
            catch (Exception ex)
            {
                status("发送失败：" + ex.Message);
                return;
            }

            string stamp = DateTime.Now.ToString("MM-dd HH:mm:ss");
            string entry = "[" + stamp + "] " + (full.Length > 110 ? full.Substring(0, 110) + "…" : full);
            txtSendLog.Text = entry + "\r\n" + txtSendLog.Text;
            if (txtSendLog.Text.Length > 4000) txtSendLog.Text = txtSendLog.Text.Substring(0, 4000);
            status("✓ 已发送：" + (full.Length > 40 ? full.Substring(0, 40) + "…" : full));
        }

        private static string EscapeSendKeys(string s)
        {
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '+' || c == '^' || c == '%' || c == '~' || c == '(' || c == ')' || c == '{' || c == '}' || c == '[' || c == ']')
                    sb.Append('{').Append(c).Append('}');
                else if (c == '\r' || c == '\n')
                    sb.Append("{ENTER}");
                else
                    sb.Append(c);
            }
            return sb.ToString();
        }
    }
}
