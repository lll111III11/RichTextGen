using System;
using System.Drawing;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace RichTextGen
{
    /// <summary>
    /// 独立的 AI 对话窗口：贴在屏幕左侧，从左侧边缘滑出/滑入，
    /// 不覆盖主界面；主窗口收进托盘后仍可继续对话，
    /// 通过本机端口服务（/chat、/action）操作主程序里的编辑器。
    /// </summary>
    internal class ChatWindow : Form
    {
        private static ChatWindow _instance;
        private readonly WebView2 _web = new WebView2();
        private Timer _anim;
        private int _slideTargetX;
        private bool _closingAnim;
        private Action _onClosed;

        /// <summary>独立对话窗口当前是否开着。</summary>
        public static bool IsOpen
        {
            get
            {
                try { return _instance != null && !_instance.IsDisposed && _instance.Visible; }
                catch { return false; }
            }
        }

        public static void ShowOrActivate(string url, CoreWebView2Environment env, Action onClosed)
        {
            if (_instance != null && !_instance.IsDisposed)
            {
                try
                {
                    if (_instance.WindowState == FormWindowState.Minimized)
                        _instance.WindowState = FormWindowState.Normal;
                    _instance.Activate();
                    _instance.BringToFront();
                }
                catch { }
                return;
            }
            _instance = new ChatWindow();
            _instance._onClosed = onClosed;
            _instance.Init(url, env);
            _instance.Show();
            _instance.SlideIn();
        }

        private ChatWindow()
        {
            Text = "AI 对话 · 彩色文本生成器";
            StartPosition = FormStartPosition.Manual;
            AutoScaleMode = AutoScaleMode.None;   // 避免 DPI 切换时窗口被二次放大
            Rectangle wa = Screen.PrimaryScreen.WorkingArea;

            int w = Math.Min(920, Math.Max(560, wa.Width - 120));
            Size = new Size(w, wa.Height);
            MinimumSize = new Size(520, 420);
            Location = new Point(wa.Left - Width, wa.Top);   // 先藏在屏幕左侧外
            BackColor = Color.FromArgb(243, 244, 246);
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            _slideTargetX = wa.Left;
            _web.Dock = DockStyle.Fill;
            Controls.Add(_web);

            FormClosed += delegate
            {
                _instance = null;
                try { if (_onClosed != null) _onClosed(); } catch { }
            };
        }

        private async void Init(string url, CoreWebView2Environment env)
        {
            try
            {
                if (env != null) await _web.EnsureCoreWebView2Async(env);
                else await _web.EnsureCoreWebView2Async();

                _web.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
                _web.CoreWebView2.Settings.IsStatusBarEnabled = false;
                _web.CoreWebView2.Navigate(url);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "独立对话窗口初始化失败：" + ex.Message,
                    "AI 对话", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        /// <summary>从屏幕左侧滑入，并淡入。</summary>
        private void SlideIn()
        {
            int startX = _slideTargetX - Width;
            Location = new Point(startX, Top);
            Opacity = 0.0;
            Animate(startX, _slideTargetX, 0.0, 1.0, null);
        }

        /// <summary>关闭前向左滑出并淡出。</summary>
        private void SlideOutAndClose()
        {
            int endX = _slideTargetX - Width;
            Animate(Left, endX, Opacity, 0.0, delegate
            {
                _closingAnim = true;
                try { Close(); } catch { }
            });
        }

        private void Animate(int fromX, int toX, double fromO, double toO, Action done)
        {
            try { if (_anim != null) { _anim.Stop(); _anim.Dispose(); } } catch { }
            int y = Top;
            int step = 0, steps = 20;
            _anim = new Timer();
            _anim.Interval = 12;
            _anim.Tick += delegate
            {
                step++;
                double t = (double)step / steps;
                if (t > 1) t = 1;
                double e = 1 - Math.Pow(1 - t, 3);   // easeOutCubic
                try
                {
                    Left = (int)Math.Round(fromX + (toX - fromX) * e);
                    Opacity = fromO + (toO - fromO) * e;
                    Top = y;
                }
                catch { }
                if (step >= steps)
                {
                    try { _anim.Stop(); _anim.Dispose(); } catch { }
                    _anim = null;
                    if (done != null) done();
                }
            };
            _anim.Start();
        }

        /// <summary>显示后按当前屏幕实际工作区再夹一次，防止高 DPI 下窗口高过屏幕。</summary>
        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            BeginInvoke((Action)ClampToScreen);
        }

        /// <summary>DPI 变化（WebView2 初始化后可能切换）时重新贴合屏幕，避免高过屏幕底部。</summary>
        protected override void OnDpiChanged(DpiChangedEventArgs e)
        {
            base.OnDpiChanged(e);
            BeginInvoke((Action)ClampToScreen);
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool SystemParametersInfo(uint action, uint param, ref RECT rect, uint winIni);

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        /// <summary>用 Win32 取当前工作区（物理像素），把窗口夹进屏幕内。</summary>
        private void ClampToScreen()
        {
            try
            {
                RECT r = new RECT();
                if (!SystemParametersInfo(0x0030, 0, ref r, 0)) return;   // SPI_GETWORKAREA
                int w = r.Right - r.Left, h = r.Bottom - r.Top;
                if (w <= 0 || h <= 0) return;
                _slideTargetX = r.Left;
                Top = r.Top;
                if (Width > w) Width = w;
                if (Height > h) Height = h;
            }
            catch { }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!_closingAnim && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                SlideOutAndClose();
                return;
            }
            base.OnFormClosing(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                try { if (_anim != null) { _anim.Stop(); _anim.Dispose(); _anim = null; } } catch { }
            }
            base.Dispose(disposing);
        }
    }
}
