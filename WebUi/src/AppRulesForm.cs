using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace RichTextGen
{
    /// <summary>
    /// 「应用名单」独立窗口：展示浮动输入面板记录过的所有应用，
    /// 支持三种模式（全部 / 仅白名单 / 黑名单）与逐项勾选。
    /// 用 WebView2 + HTML，与浮动面板同一套视觉。
    /// </summary>
    internal class AppRulesForm : Form
    {
        private static AppRulesForm _inst;
        private readonly WebView2 _web = new WebView2();
        private bool _ready;

        public static void ShowOrActivate()
        {
            if (_inst != null && !_inst.IsDisposed)
            {
                try
                {
                    if (_inst.WindowState == FormWindowState.Minimized)
                        _inst.WindowState = FormWindowState.Normal;
                    _inst.Activate();
                    _inst.BringToFront();
                }
                catch { }
                _inst.Push();
                return;
            }
            _inst = new AppRulesForm();
            _inst.Show();
            _inst.Activate();
        }

        private AppRulesForm()
        {
            Text = "应用名单 · 浮动输入面板";
            StartPosition = FormStartPosition.CenterParent;
            AutoScaleMode = AutoScaleMode.None;
            ClientSize = new Size(660, 640);
            MinimumSize = new Size(560, 460);
            BackColor = Color.FromArgb(243, 243, 243);
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            _web.Dock = DockStyle.Fill;
            _web.DefaultBackgroundColor = Color.FromArgb(243, 243, 243);
            Controls.Add(_web);

            FormClosed += delegate { _inst = null; };
            Load += delegate { InitWeb(); };
        }

        private async void InitWeb()
        {
            try
            {
                if (MainForm.WebEnv != null) await _web.EnsureCoreWebView2Async(MainForm.WebEnv);
                else await _web.EnsureCoreWebView2Async();

                _web.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
                _web.CoreWebView2.Settings.IsStatusBarEnabled = false;

                const string origin = MainForm.WebOrigin;
                string html = LoadHtml();
                _web.CoreWebView2.AddWebResourceRequestedFilter(origin + "*", CoreWebView2WebResourceContext.All);
                _web.CoreWebView2.WebResourceRequested += delegate (object s, CoreWebView2WebResourceRequestedEventArgs e)
                {
                    try
                    {
                        if (!e.Request.Uri.StartsWith(origin, StringComparison.OrdinalIgnoreCase)) return;
                        string abs = new Uri(e.Request.Uri).AbsolutePath;
                        byte[] body = abs.StartsWith("/__apps", StringComparison.OrdinalIgnoreCase)
                            ? Encoding.UTF8.GetBytes(html) : new byte[0];
                        bool ok = body.Length > 0;
                        e.Response = _web.CoreWebView2.Environment.CreateWebResourceResponse(
                            new MemoryStream(body), ok ? 200 : 404, ok ? "OK" : "Not Found",
                            ok ? "Content-Type: text/html; charset=utf-8" : "Content-Type: text/plain");
                    }
                    catch { }
                };

                _web.CoreWebView2.NavigationCompleted += delegate { _ready = true; Push(); };
                _web.CoreWebView2.WebMessageReceived += OnMessage;
                _web.CoreWebView2.Navigate(origin + "__apps.html");
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "应用名单窗口初始化失败：" + ex.Message,
                    "应用名单", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private static string LoadHtml()
        {
            try
            {
                Assembly asm = Assembly.GetExecutingAssembly();
                foreach (string n in asm.GetManifestResourceNames())
                {
                    if (n.EndsWith("apps.html", StringComparison.OrdinalIgnoreCase))
                        using (Stream s = asm.GetManifestResourceStream(n))
                        using (StreamReader sr = new StreamReader(s, Encoding.UTF8))
                            return sr.ReadToEnd();
                }
            }
            catch { }
            return "<html><body>apps.html 资源缺失</body></html>";
        }

        private void Push()
        {
            if (!_ready || _web.CoreWebView2 == null) return;
            try
            {
                string json = AppRules.ToJson();
                _web.CoreWebView2.ExecuteScriptAsync("window.apState && window.apState(" + JsString(json) + ")");
            }
            catch { }
        }

        private void OnMessage(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            string raw = "";
            try { raw = e.TryGetWebMessageAsString(); } catch { }
            if (string.IsNullOrEmpty(raw)) return;
            string[] p = raw.Split('|');
            if (p.Length < 2 || p[0] != "a") return;
            string sub = p[1];
            try
            {
                switch (sub)
                {
                    case "list":
                        Push();
                        break;
                    case "mode":
                        if (p.Length >= 3) { AppRules.SetMode(p[2]); Push(); }
                        break;
                    case "toggle":
                        if (p.Length >= 3)
                        {
                            AppEntry en = AppRules.Find(p[2]);
                            AppRules.SetEnabled(p[2], en == null || !en.On);
                            Push();
                        }
                        break;
                    case "force":
                        if (p.Length >= 3)
                        {
                            AppEntry en = AppRules.Find(p[2]);
                            AppRules.SetForce(p[2], en == null || !en.Force);
                            Push();
                        }
                        break;
                    case "add":
                        if (p.Length >= 3)
                        {
                            string exe = p[2].Trim();
                            string title = p.Length >= 4 ? Uri.UnescapeDataString(p[3] ?? "") : "";
                            bool force = p.Length >= 5 && p[4] == "1";
                            if (AppRules.AddManual(exe, title, force)) Push();
                        }
                        break;
                    case "remove":
                        if (p.Length >= 3) { AppRules.Remove(p[2]); Push(); }
                        break;
                    case "purge":
                        AppRules.Purge();
                        Push();
                        break;
                }
            }
            catch { }
        }

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

        protected override void Dispose(bool disposing)
        {
            if (disposing) { try { AppRules.Flush(); } catch { } }
            base.Dispose(disposing);
        }
    }
}