using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows.Forms;

namespace RichTextGen
{
    public partial class MainForm : Form
    {
        public const string Version = "5.3.0";
        public static bool StartupDark;
        public static bool StartupSend;
        public static bool StartupDemo;
        public static bool StartupFree;
        public static string StartupBgTest = "";

        /// <summary>外部（UI 配色窗口）改完主题后刷新自身</summary>
        public void AfterThemeChanged()
        {
            previewReady = false;
            RefreshColorUi();
            UpdatePreviewNow();
        }

        private TabControl tabs;
        private TabPage pageGen;
        private TabPage pageSend;

        // 顶部
        private ComboBox cboTheme;
        private ComboBox cboZoom;
        private CheckBox chkFreeEdit;
        private Label lblOnline;
        private Timer timerOnline;

        // 文本生成页 —— 左
        private Panel leftPanel;
        private TextBox txtText;
        private ComboBox cboColorMode;
        private TextBox txtColor;
        private Panel pnlColorSwatch;
        private Label lblColorInfo;
        private ComboBox cboFrame;
        private ComboBox cboBracket;
        private ComboBox cboCommand;
        private CheckBox chkPrefix;

        private CheckBox chkGrad;
        private TextBox txtGradStart;
        private TextBox txtGradEnd;
        private CheckBox chkLoopGrad;
        private CheckBox chkAxisGrad;
        private NumericUpDown numPeriod;

        private Dictionary<string, CheckBox> tagCheck = new Dictionary<string, CheckBox>();
        private Dictionary<string, Control> tagInput = new Dictionary<string, Control>();
        private Dictionary<string, TextBox> tagExtra = new Dictionary<string, TextBox>();
        private Dictionary<string, GroupBox> tagGroups = new Dictionary<string, GroupBox>();

        // 文本生成页 —— 右
        private WebBrowser wbPreview;
        private TextBox txtResult;
        private Label lblStatus;

        private Timer timerPreview;
        private SplitContainer splitMain;
        private SplitContainer rightSplit;
        

        /// <summary>整窗体合成（大幅减少多控件重绘的闪烁与卡顿）</summary>
        protected override CreateParams CreateParams
        {
            get { CreateParams cp = base.CreateParams; cp.ExStyle |= 0x02000000; return cp; }   // WS_EX_COMPOSITED
        }

        public MainForm()
        {
            BuildUi();
            Theme.Set(ThemeMode.Light);
            Theme.Apply(this);
            RefreshColorUi();
            UpdatePreviewNow();

            timerOnline = new Timer();
            timerOnline.Interval = 5000;
            timerOnline.Tick += delegate { RefreshOnlineLabel(); };
            timerOnline.Start();
            RefreshOnlineLabel();
            if (StartupDark) cboTheme.SelectedIndex = 1;
            if (StartupSend) tabs.SelectedTab = pageSend;
        }

        /// <summary>整窗背景：由窗体自己把同一张图画满客户区，子容器透明后自然透出</summary>
        protected override void OnPaintBackground(PaintEventArgs e)
        {
            if (UiBackground.Active && UiBackground.PaintBack != null)
            {
                e.Graphics.DrawImageUnscaled(UiBackground.PaintBack, 0, 0);
                return;
            }
            base.OnPaintBackground(e);
        }

        private Timer _bgResizeTimer;
        private void OnFormResizedForBg()
        {
            if (!UiBackground.Active) return;
            if (_bgResizeTimer == null)
            {
                _bgResizeTimer = new Timer();
                _bgResizeTimer.Interval = 250;
                _bgResizeTimer.Tick += delegate { _bgResizeTimer.Stop(); UiBackground.ApplyTo(this); };
            }
            _bgResizeTimer.Stop();
            _bgResizeTimer.Start();
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            uiReady = true;
            if (splitMain != null)
            {
                try
                {
                    splitMain.Panel1MinSize = 280;
                    splitMain.Panel2MinSize = 240;
                    int want = 600;
                    int max = splitMain.Width - splitMain.Panel2MinSize - 8;
                    if (want > max) want = max;
                    if (want < splitMain.Panel1MinSize) want = splitMain.Panel1MinSize;
                    splitMain.SplitterDistance = want;                    if (rightSplit != null)                    {                        int rw = rightSplit.Height - rightSplit.Panel2MinSize - 8;                        int rwant = (int)(rightSplit.Height * 0.56);                        if (rwant > rw) rwant = rw;                        if (rwant < rightSplit.Panel1MinSize) rwant = rightSplit.Panel1MinSize;                        rightSplit.SplitterDistance = rwant;                    }
                }
                catch { }
            }
            EnsureEmbeddedBackground();   // 首次运行释放内置图片
            ReleaseEmbeddedAssets();
            if (StartupBgTest.Length > 0)
            {
                string err;
                if (UiBackground.Load(StartupBgTest, out err)) UiBackground.ApplyTo(this);
                else status("背景载入失败：" + err);
            }
            Resize += delegate { OnFormResizedForBg(); };
            InitNetAndUpdate();
            InitTray();
            ApplyWorkMode(_mode);
            UiZoom.Capture(this);
            if (StartupDemo) ApplyDemo();
            if (StartupFree && chkFreeEdit != null) chkFreeEdit.Checked = true;
            previewReady = false;   // 首次显示后强制走一次 DocumentText，确保预览底色正确
            UpdatePreviewNow();
        }

        // ================================================================ UI
        private void BuildUi()
        {
            Text = "彩色文本生成器 v" + Version + "  ·  Unity 6 / TextMeshPro 富文本";
            try { Icon = LoadAppIcon(); } catch { }
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
            Font = new Font(Theme.UiFont, 9f);
            Theme.EnableDoubleBuffer(this);
            ClientSize = new Size(1120, 780);
            MinimumSize = new Size(900, 600);
            StartPosition = FormStartPosition.CenterScreen;

            // ---- 顶部条 ----
            Panel top = new Panel();
            top.Dock = DockStyle.Top;
            top.Height = 74;
            Controls.Add(top);

            Label l1 = new Label();
            l1.Text = "配色";
            l1.Location = new Point(12, 11);
            l1.AutoSize = true;
            l1.Tag = "hint";
            top.Controls.Add(l1);

            cboTheme = new ComboBox();
            cboTheme.DropDownStyle = ComboBoxStyle.DropDownList;
            cboTheme.Location = new Point(52, 7);
            cboTheme.Size = new Size(96, 24);
            cboTheme.Items.AddRange(new object[] { "亮色模式", "深色模式" });
            cboTheme.SelectedIndex = 0;
            cboTheme.SelectedIndexChanged += delegate
            {
                Theme.Set(cboTheme.SelectedIndex == 0 ? ThemeMode.Light : ThemeMode.Dark);
                Theme.Apply(this);
                previewReady = false;
                RefreshColorUi();
                UpdatePreviewNow();
            };
            top.Controls.Add(cboTheme);

            btnUpdate = new Button();
            btnUpdate.Text = "⬆ 更新";
            btnUpdate.Size = new Size(150, 26);
            btnUpdate.Location = new Point(960, 6);
            btnUpdate.Visible = false;
            btnUpdate.Tag = "primary";
            btnUpdate.Click += delegate { DoUpdate(); };
            top.Controls.Add(btnUpdate);

            lblNetState = new Label();
            lblNetState.Location = new Point(1120, 11);
            lblNetState.AutoSize = true;
            lblNetState.Text = "● 检测中";
            top.Controls.Add(lblNetState);

            lblOnline = new Label();
            lblOnline.Location = new Point(168, 11);
            lblOnline.AutoSize = true;
            top.Controls.Add(lblOnline);

            Label lz = new Label();
            lz.Text = "缩放";
            lz.Location = new Point(320, 11);
            lz.AutoSize = true;
            lz.Tag = "hint";
            top.Controls.Add(lz);

            cboZoom = new ComboBox();
            cboZoom.DropDownStyle = ComboBoxStyle.DropDownList;
            cboZoom.Location = new Point(356, 7);
            cboZoom.Size = new Size(72, 24);
            cboZoom.Items.AddRange(new object[] { "70%", "80%", "90%", "100%", "110%", "125%", "150%", "175%", "200%" });
            cboZoom.SelectedIndex = 3;
            cboZoom.SelectedIndexChanged += delegate
            {
                float f = (float)Convert.ToInt32(Convert.ToString(cboZoom.SelectedItem).TrimEnd('%')) / 100f;
                UiZoom.Apply(f);
                AfterZoom();
                previewReady = false;
                UpdatePreviewNow();
            };
            top.Controls.Add(cboZoom);

            Button btnUiColors = new Button();
            btnUiColors.Text = "UI 配色…";
            Theme.Icon(btnUiColors, (char)0xE790, 10f);
            btnUiColors.Size = new Size(126, 26);
            btnUiColors.Location = new Point(436, 6);
            btnUiColors.Click += delegate { new UiColorsDialog(this).ShowDialog(this); };
            top.Controls.Add(btnUiColors);

            Button btnBg = new Button();
            btnBg.Text = "背景图…";
            Theme.Icon(btnBg, (char)0xE8B9, 10f);
            btnBg.Size = new Size(114, 26);
            btnBg.Location = new Point(570, 6);
            btnBg.Click += delegate { new BackgroundDialog(this).ShowDialog(this); };
            top.Controls.Add(btnBg);

            Button btnHelp = new Button();
            btnHelp.Text = "使用教程";
            Theme.Icon(btnHelp, (char)0xE897, 10f);
            btnHelp.Size = new Size(122, 26);
            btnHelp.Location = new Point(692, 6);
            btnHelp.Click += delegate { new HelpWindow().ShowDialog(this); };
            top.Controls.Add(btnHelp);

            chkFreeEdit = new CheckBox();
            chkFreeEdit.Text = "自由编辑系统（展开全部）";
            chkFreeEdit.Location = new Point(822, 10);
            chkFreeEdit.AutoSize = true;
            chkFreeEdit.CheckedChanged += delegate
            {
                SetAllGroupsExpanded(chkFreeEdit.Checked);
                if (chkFreeEdit.Checked && WindowState == FormWindowState.Normal && Width < 1320) Width = 1320;
            };
            top.Controls.Add(chkFreeEdit);

            // ---- 第二行：工作模式 / 热键 / 图片系统 ----
            Label lmo = new Label();
            lmo.Text = "工作模式";
            lmo.Location = new Point(12, 47);
            lmo.AutoSize = true;
            lmo.Tag = "hint";
            top.Controls.Add(lmo);

            _cboMode = new ComboBox();
            _cboMode.DropDownStyle = ComboBoxStyle.DropDownList;
            _cboMode.Location = new Point(76, 43);
            _cboMode.Size = new Size(150, 24);
            _cboMode.Items.AddRange(new object[] { "极简模式（小窗·功能完整）", "半全模式（常用+基础热键）", "全工模式（全展开+全热键）" });
            _cboMode.SelectedIndex = 1;
            _cboMode.SelectedIndexChanged += delegate { ApplyWorkMode((WorkMode)_cboMode.SelectedIndex); };
            top.Controls.Add(_cboMode);

            Button bhKey = new Button();
            bhKey.Text = "热键…";
            Theme.Icon(bhKey, (char)0xE765, 10f);
            bhKey.Size = new Size(102, 26);
            bhKey.Location = new Point(238, 42);
            bhKey.Click += delegate
            {
                using (HotkeyDialog d = new HotkeyDialog())
                {
                    if (d.ShowDialog(this) == DialogResult.OK) RegisterAllHotkeys();
                }
            };
            top.Controls.Add(bhKey);

            Button bhImg = new Button();
            bhImg.Text = "图片系统…";
            Theme.Icon(bhImg, (char)0xE91B, 10f);
            bhImg.Size = new Size(128, 26);
            bhImg.Location = new Point(350, 42);
            bhImg.Click += delegate { new ImageSystemDialog(this).Show(this); };
            top.Controls.Add(bhImg);

            Label lhint2 = new Label();
            lhint2.Text = "关窗口 = 收到托盘继续后台运行；热键默认 Ctrl+F3 唤回，可在「热键…」里改。";
            lhint2.Location = new Point(490, 47);
            lhint2.AutoSize = true;
            lhint2.Tag = "hint";
            top.Controls.Add(lhint2);

            // ---- 主功能分页 ----
            tabs = new TabControl();
            tabs.Dock = DockStyle.Fill;
            tabs.Padding = new Point(14, 5);
            Controls.Add(tabs);
            tabs.BringToFront();

            pageGen = new TabPage("文本生成");
            pageSend = new TabPage("发送到游戏");
            tabs.TabPages.Add(pageGen);
            tabs.TabPages.Add(pageSend);

            BuildGenPage(pageGen);
            BuildSendPage(pageSend);

            timerPreview = new Timer();
            timerPreview.Interval = 250;
            timerPreview.Tick += delegate
            {
                timerPreview.Stop();
                UpdatePreviewNow();
            };
        }

        private void BuildGenPage(TabPage page)
        {
            SplitContainer split = new SplitContainer();
            split.Dock = DockStyle.Fill;
            split.Orientation = Orientation.Vertical;
            splitMain = split;
            page.Controls.Add(split);

            // ================= 左：设置 =================
            leftPanel = new Panel();
            leftPanel.Dock = DockStyle.Fill;
            leftPanel.AutoScroll = true;
            split.Panel1.Controls.Add(leftPanel);

            int y = 10;

            // 文本输入
            GroupBox gbText = MkGroup("文本内容", 10, y, 566, 132);
            leftPanel.Controls.Add(gbText);
            Label lh = MkHint("支持多行；留空则只输出标签（用于自备文本）", 10, 22, gbText);
            txtText = new TextBox();
            txtText.Multiline = true;
            txtText.ScrollBars = ScrollBars.Vertical;
            txtText.Location = new Point(10, 44);
            txtText.Size = new Size(544, 78);
            txtText.TextChanged += delegate { QueuePreview(); };
            txtText.AllowDrop = true;
            txtText.DragEnter += delegate (object s2, DragEventArgs e2)
            {
                e2.Effect = e2.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
            };
            txtText.DragDrop += delegate (object s2, DragEventArgs e2)
            {
                string[] files = e2.Data.GetData(DataFormats.FileDrop) as string[];
                if (files == null || files.Length == 0) return;
                try
                {
                    string txt2 = ReadTextFile(files[0]);
                    txtText.Text = txt2;
                    status("已导入 " + Path.GetFileName(files[0]) + "（" + txt2.Length + " 字符）");
                }
                catch (Exception ex) { status("导入失败：" + ex.Message); }
            };
            gbText.Controls.Add(txtText);
            y += 140;

            // 颜色
            GroupBox gbColor = MkGroup("颜色（三种模式：Hex / RGB / 英文色名）", 10, y, 566, 116);
            leftPanel.Controls.Add(gbColor);

            Label lm = MkHint("颜色模式", 10, 24, gbColor);
            cboColorMode = new ComboBox();
            cboColorMode.DropDownStyle = ComboBoxStyle.DropDownList;
            cboColorMode.Location = new Point(76, 20);
            cboColorMode.Size = new Size(210, 24);
            cboColorMode.Items.AddRange(new object[] { "#十六进制（#FF8800）", "RGB 十进制（255,136,0）", "英文色名（orange）" });
            cboColorMode.SelectedIndex = 0;
            cboColorMode.SelectedIndexChanged += delegate { RefreshColorUi(); UpdatePreviewNow(); };
            gbColor.Controls.Add(cboColorMode);

            Label lc = MkHint("颜色值", 300, 24, gbColor);
            txtColor = new TextBox();
            txtColor.Location = new Point(348, 20);
            txtColor.Size = new Size(130, 24);
            txtColor.Text = "#FFFFFF";   // 颜色系统初始为白色（选色即生效，不是摆设）
            txtColor.TextChanged += delegate { RefreshColorUi(); QueuePreview(); };
            gbColor.Controls.Add(txtColor);

            pnlColorSwatch = new Panel();
            pnlColorSwatch.Location = new Point(486, 20);
            pnlColorSwatch.Size = new Size(68, 24);
            pnlColorSwatch.BorderStyle = BorderStyle.FixedSingle;
            pnlColorSwatch.Tag = "swatch";
            gbColor.Controls.Add(pnlColorSwatch);

            Button btnPick = new Button();
            btnPick.Text = "拾色器 / 颜色库…";
            btnPick.Location = new Point(76, 52);
            btnPick.Size = new Size(210, 28);
            btnPick.Click += delegate { OpenPicker(0); };
            gbColor.Controls.Add(btnPick);

            lblColorInfo = MkHint("", 300, 58, gbColor);
            lblColorInfo.Size = new Size(254, 44);
            gbColor.Controls.Add(lblColorInfo);
            y += 124;

            // 渐变
            GroupBox gbGrad = MkGroup("渐变（逐字符，可选）", 10, y, 566, 96);
            leftPanel.Controls.Add(gbGrad);
            chkGrad = new CheckBox();
            chkGrad.Text = "启用逐字符渐变";
            chkGrad.Location = new Point(10, 22);
            chkGrad.AutoSize = true;
            chkGrad.CheckedChanged += delegate { QueuePreview(); };
            gbGrad.Controls.Add(chkGrad);

            MkHint("起始色", 170, 24, gbGrad);
            txtGradStart = new TextBox();
            txtGradStart.Location = new Point(218, 20);
            txtGradStart.Size = new Size(96, 24);
            txtGradStart.Text = "#FF0000";
            txtGradStart.TextChanged += delegate { QueuePreview(); };
            gbGrad.Controls.Add(txtGradStart);
            Button bp1 = new Button();
            bp1.Text = "…";
            bp1.Location = new Point(318, 19);
            bp1.Size = new Size(30, 26);
            bp1.Click += delegate { OpenPicker(1); };
            gbGrad.Controls.Add(bp1);

            MkHint("结束色", 356, 24, gbGrad);
            txtGradEnd = new TextBox();
            txtGradEnd.Location = new Point(404, 20);
            txtGradEnd.Size = new Size(96, 24);
            txtGradEnd.Text = "#0000FF";
            txtGradEnd.TextChanged += delegate { QueuePreview(); };
            gbGrad.Controls.Add(txtGradEnd);
            Button bp2 = new Button();
            bp2.Text = "…";
            bp2.Location = new Point(504, 19);
            bp2.Size = new Size(30, 26);
            bp2.Click += delegate { OpenPicker(2); };
            gbGrad.Controls.Add(bp2);

            chkAxisGrad = new CheckBox();
            chkAxisGrad.Text = "轴对称（中心镜像 A→B→A）";
            chkAxisGrad.Location = new Point(300, 56);
            chkAxisGrad.AutoSize = true;
            chkAxisGrad.CheckedChanged += delegate { QueuePreview(); };
            gbGrad.Controls.Add(chkAxisGrad);

            chkLoopGrad = new CheckBox();
            chkLoopGrad.Text = "循环渐变";
            chkLoopGrad.Location = new Point(10, 56);
            chkLoopGrad.AutoSize = true;
            chkLoopGrad.CheckedChanged += delegate { QueuePreview(); };
            gbGrad.Controls.Add(chkLoopGrad);
            MkHint("周期(字符)", 120, 58, gbGrad);
            numPeriod = new NumericUpDown();
            numPeriod.Location = new Point(196, 54);
            numPeriod.Size = new Size(56, 24);
            numPeriod.Minimum = 0;
            numPeriod.Maximum = 200;
            numPeriod.Value = 5;
            numPeriod.ValueChanged += delegate { QueuePreview(); };
            gbGrad.Controls.Add(numPeriod);
            MkHint("设 0 表示按文本长度", 258, 58, gbGrad);
            y += 104;

            // 标签分组区（由标签注册表生成）
            BuildTagGroupPanel(y);

            // 外观 / 指令
            int yFooter = tagStackBottom + 12;
            GroupBox gbOut = MkGroup("外观与指令", 10, yFooter, 566, 104);
            gbAppearance = gbOut;
            leftPanel.Controls.Add(gbOut);

            MkHint("装饰边框", 10, 26, gbOut);
            cboFrame = new ComboBox();
            cboFrame.DropDownStyle = ComboBoxStyle.DropDownList;
            cboFrame.Location = new Point(76, 22);
            cboFrame.Size = new Size(140, 24);
            cboFrame.Items.AddRange(new object[] { "无", "▌ 左右竖条", "【】方括号符", "◆ 菱形点阵", "★ 星标" });
            cboFrame.SelectedIndex = 0;
            cboFrame.SelectedIndexChanged += delegate { QueuePreview(); };
            gbOut.Controls.Add(cboFrame);

            MkHint("括号包裹", 236, 26, gbOut);
            cboBracket = new ComboBox();
            cboBracket.DropDownStyle = ComboBoxStyle.DropDownList;
            cboBracket.Location = new Point(302, 22);
            cboBracket.Size = new Size(140, 24);
            cboBracket.Items.AddRange(new object[] { "无", "（ ）", "【 】", "「 」", "『 』", "《 》", "{ }", "[ ]", "( )" });
            cboBracket.SelectedIndex = 0;
            cboBracket.SelectedIndexChanged += delegate { QueuePreview(); };
            gbOut.Controls.Add(cboBracket);

            MkHint("指令", 456, 26, gbOut);
            cboCommand = new ComboBox();
            cboCommand.DropDownStyle = ComboBoxStyle.DropDownList;
            cboCommand.Location = new Point(496, 22);
            cboCommand.Size = new Size(58, 24);
            cboCommand.Items.AddRange(new object[] { ".BC", ".C", ".NC" });
            cboCommand.SelectedIndex = 0;
            gbOut.Controls.Add(cboCommand);

            chkPrefix = new CheckBox();
            chkPrefix.Text = "生成结果带上指令前缀（.NC 表示不加）";
            chkPrefix.Location = new Point(10, 60);
            chkPrefix.AutoSize = true;
            chkPrefix.Checked = true;
            chkPrefix.CheckedChanged += delegate { QueuePreview(); };
            gbOut.Controls.Add(chkPrefix);

            // ---- 使用教程速查（顺便把左下角空出来的区域用起来）----
            GroupBox gbTip = MkGroup("使用教程 / 快速上手", 10, yFooter + 112, 566, 156);
            leftPanel.Controls.Add(gbTip);
            Label lt = MkHint("① 填文本 ② 在「颜色」区选模式并填色 ③ 勾选标签并填参数 ④ 看右侧实时预览 ⑤ 复制或去发送。\\n渐变=逐字符上色（配「循环 + 周期」可做彩虹流动）；指令前缀 .BC=全服 / .C=团队 / .NC=不加。", 10, 24, gbTip);
            lt.Size = new Size(544, 46);
            Button bHelp = new Button();
            bHelp.Text = "使用教程（逐项说明）";
            bHelp.Size = new Size(170, 30);
            bHelp.Location = new Point(10, 74);
            bHelp.Click += delegate { new HelpWindow().ShowDialog(this); };
            gbTip.Controls.Add(bHelp);
            Button bCol = new Button();
            bCol.Text = "UI 配色…";
            bCol.Size = new Size(90, 30);
            bCol.Location = new Point(190, 74);
            bCol.Click += delegate { new UiColorsDialog(this).ShowDialog(this); };
            gbTip.Controls.Add(bCol);
            Button bBg = new Button();
            bBg.Text = "背景图…";
            bBg.Size = new Size(84, 30);
            bBg.Location = new Point(290, 74);
            bBg.Click += delegate { new BackgroundDialog(this).ShowDialog(this); };
            gbTip.Controls.Add(bBg);
            Label lt2 = MkHint("本地图片 → URL：打开「使用教程」，把图片拖进左下角方框即自动生成局域网 URL。", 10, 114, gbTip);
            lt2.Size = new Size(544, 34);

            // ================= 右：预览 / 输出（可拖分隔条，不再留大片空白）=================
            Panel right = new Panel();
            right.Dock = DockStyle.Fill;
            split.Panel2.Controls.Add(right);

            Panel btnPanel = new Panel();
            btnPanel.Height = 42;
            btnPanel.Dock = DockStyle.Bottom;
            right.Controls.Add(btnPanel);

            rightSplit = new SplitContainer();
            rightSplit.Dock = DockStyle.Fill;
            rightSplit.Orientation = Orientation.Horizontal;
            rightSplit.Panel1MinSize = 110;
            rightSplit.Panel2MinSize = 90;
            right.Controls.Add(rightSplit);

            Panel pv = new Panel();
            pv.Dock = DockStyle.Fill;
            rightSplit.Panel1.Controls.Add(pv);

            wbPreview = new WebBrowser();
            wbPreview.Dock = DockStyle.Fill;
            wbPreview.AllowWebBrowserDrop = false;
            wbPreview.IsWebBrowserContextMenuEnabled = false;
            wbPreview.ScriptErrorsSuppressed = true;
            pv.Controls.Add(wbPreview);

            Label lp = new Label();                     // 后加 → 先占顶部
            lp.Text = "实时预览（浏览器渲染，与游戏内大致一致）";
            lp.Dock = DockStyle.Top;
            lp.Height = 22;
            lp.Tag = "hint";
            pv.Controls.Add(lp);

            Panel op = new Panel();
            op.Dock = DockStyle.Fill;
            rightSplit.Panel2.Controls.Add(op);

            txtResult = new TextBox();
            txtResult.Multiline = true;
            txtResult.ScrollBars = ScrollBars.Vertical;
            txtResult.ReadOnly = true;
            txtResult.Dock = DockStyle.Fill;
            op.Controls.Add(txtResult);

            Label lo = new Label();
            lo.Text = "生成的代码（可直接用于游戏指令 / 复制）";
            lo.Dock = DockStyle.Top;
            lo.Height = 22;
            lo.Tag = "hint";
            op.Controls.Add(lo);

            Button btnGen = new Button();
            btnGen.Text = "生成代码";
            Theme.Icon(btnGen, (char)0xE943, 10f);
            btnGen.Size = new Size(132, 32);
            btnGen.Location = new Point(8, 5);
            btnGen.Tag = "primary";
            btnGen.Click += delegate { UpdatePreviewNow(); CopyIfRequested(); };
            btnPanel.Controls.Add(btnGen);

            Button btnCopy = new Button();
            btnCopy.Text = "复制";
            Theme.Icon(btnCopy, (char)0xE8C8, 10f);
            btnCopy.Size = new Size(96, 32);
            btnCopy.Location = new Point(148, 5);
            btnCopy.Click += delegate { CopyResult(); };
            btnPanel.Controls.Add(btnCopy);

            Button btnToSend = new Button();
            btnToSend.Text = "去发送…";
            Theme.Icon(btnToSend, (char)0xE724, 10f);
            btnToSend.Size = new Size(118, 32);
            btnToSend.Location = new Point(252, 5);
            btnToSend.Click += delegate { tabs.SelectedTab = pageSend; SendReloadFromGen(); };
            btnPanel.Controls.Add(btnToSend);

            lblStatus = new Label();
            lblStatus.Location = new Point(378, 13);
            lblStatus.AutoSize = true;
            lblStatus.Tag = "hint";
            btnPanel.Controls.Add(lblStatus);
        }

        /// <summary>缩放/改尺寸后统一重排（避免标签组按旧坐标排布留下空白）</summary>
        public void AfterZoom()
        {
            try { RelayoutTagGroups(); if (leftPanel != null) leftPanel.PerformLayout(); } catch { }
        }

        /// <summary>读取文本文件（自动识别 UTF-8 / BOM / GBK）</summary>
        private static string ReadTextFile(string path)
        {
            byte[] b = File.ReadAllBytes(path);
            if (b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF) return Encoding.UTF8.GetString(b, 3, b.Length - 3);
            try { return new UTF8Encoding(false, true).GetString(b); }
            catch { return Encoding.GetEncoding(936).GetString(b); }
        }

        private GroupBox MkGroup(string text, int x, int y, int w, int h)
        {
            GroupBox gb = new GroupBox();
            gb.Text = text;
            gb.Location = new Point(x, y);
            gb.Size = new Size(w, h);
            return gb;
        }

        private Label MkHint(string text, int x, int y, Control parent)
        {
            Label l = new Label();
            l.Text = text;
            l.Location = new Point(x, y);
            l.AutoSize = true;
            l.Tag = "hint";
            return l;
        }

        private void QueuePreview()
        {
            if (!uiReady || timerPreview == null) return;   // 建界面期间不触发生成
            timerPreview.Stop();
            timerPreview.Start();
        }

        private void RefreshOnlineLabel()
        {
            bool on = OnlineColors.IsOnline();
            lblOnline.Text = on ? "● 在线（联网取色可用）" : "● 离线（使用内置 140 色）";
            lblOnline.ForeColor = on ? Color.FromArgb(0, 140, 60) : Color.FromArgb(190, 60, 60);
        }

        /// <summary>刷新颜色区：色块、三形态读数、模式提示</summary>
        private void RefreshColorUi()
        {
            Rgb c;
            if (!ColorUtil.TryParse(txtColor.Text, out c))
            {
                lblColorInfo.Text = "颜色解析失败，请输入 #RRGGBB / 255,136,0 / orange";
                lblColorInfo.ForeColor = Theme.SubText;
                return;
            }
            pnlColorSwatch.BackColor = c.ToColor();
            string exact;
            string name;
            if (ColorUtil.HasExactName(c, out exact)) name = exact;
            else
            {
                int nd;
                name = ColorUtil.NearestName(c, out nd) + "（近似）";
            }
            ColorOutputMode mode = (ColorOutputMode)Math.Max(0, cboColorMode.SelectedIndex);
            string tagVal = ColorUtil.ToTagValue(c, mode);
            lblColorInfo.Text = string.Format("HEX {0}    RGB {1}\r\nNAME {2}\r\n输出 <color={3}>", c.HexSharp, c.RgbText, name, tagVal);
            lblColorInfo.ForeColor = Theme.Mode == ThemeMode.Dark ? Color.FromArgb(150, 200, 255) : Color.FromArgb(0, 90, 175);
        }

        private void OpenPicker(int which)
        {
            Rgb cur;
            if (which == 0)
            {
                if (!ColorUtil.TryParse(txtColor.Text, out cur)) cur = new Rgb(255, 136, 0);
            }
            else if (which == 1)
            {
                if (!ColorUtil.TryParse(txtGradStart.Text, out cur)) cur = new Rgb(255, 0, 0);
            }
            else
            {
                if (!ColorUtil.TryParse(txtGradEnd.Text, out cur)) cur = new Rgb(0, 0, 255);
            }

            ColorPickerDialog dlg = new ColorPickerDialog(cur, (ColorOutputMode)Math.Max(0, cboColorMode.SelectedIndex));
            if (dlg.ShowDialog(this) != DialogResult.OK) return;

            string v = (dlg.OutputMode == ColorOutputMode.Name) ? ColorUtil.ToTagValue(dlg.SelectedColor, ColorOutputMode.Name)
                                                               : dlg.SelectedColor.HexSharp;
            if (which == 0) txtColor.Text = v;
            else if (which == 1) txtGradStart.Text = v;
            else txtGradEnd.Text = v;
            if (cboColorMode.SelectedIndex != (int)dlg.OutputMode)
                cboColorMode.SelectedIndex = (int)dlg.OutputMode;
            status("颜色已应用：" + v + "（拾色器「刷新并关闭」）");
            UpdatePreviewNow();
        }

        private void status(string s)
        {
            if (lblStatus == null) return;
            lblStatus.Text = s;
        }

        private void CopyResult()
        {
            if (txtResult.Text.Length == 0) { status("没有可复制的内容"); return; }
            try
            {
                Clipboard.SetText(txtResult.Text);
                status("已复制 " + txtResult.Text.Length + " 个字符到剪贴板");
            }
            catch { status("复制失败（剪贴板被占用）"); }
        }

        private bool copyAfterGen;
        private void CopyIfRequested()
        {
            if (copyAfterGen) { copyAfterGen = false; }
        }
    }
}
