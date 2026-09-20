using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace RichTextGen
{
    /// <summary>内置小型 HTTP 服务：把「本地图片」变成游戏/其他设备能访问的 URL</summary>
    public class LocalHttpServer
    {
        private TcpListener _listener;
        private string _root;
        private int _port;
        private volatile bool _run;

        public int Port { get { return _port; } }
        public bool Running { get { return _run; } }

        public bool Start(string root, int port)
        {
            if (_run) return true;
            try
            {
                _root = Path.GetFullPath(root);
                Directory.CreateDirectory(_root);
                _port = port;
                _listener = new TcpListener(IPAddress.IPv6Any, port);
                try { _listener.Server.SetSocketOption(SocketOptionLevel.IPv6, (SocketOptionName)27, false); } catch { }
                _listener.Start();
                _run = true;
                Thread th = new Thread(Loop);
                th.IsBackground = true;
                th.Start();
                return true;
            }
            catch { return false; }
        }

        public void Stop()
        {
            _run = false;
            try { if (_listener != null) _listener.Stop(); } catch { }
        }

        private void Loop()
        {
            while (_run)
            {
                try
                {
                    TcpClient c = _listener.AcceptTcpClient();
                    ThreadPool.QueueUserWorkItem(delegate (object st) { Serve((TcpClient)st); }, c);
                }
                catch { if (!_run) return; }
            }
        }

        private void Serve(TcpClient client)
        {
            try
            {
                using (client)
                using (NetworkStream ns = client.GetStream())
                {
                    ns.ReadTimeout = 4000;
                    byte[] buf = new byte[8192];
                    int n = ns.Read(buf, 0, buf.Length);
                    if (n <= 0) return;
                    string line = Encoding.ASCII.GetString(buf, 0, n).Split('\n')[0].Trim();
                    string[] parts = line.Split(' ');
                    if (parts.Length < 2) return;
                    string path = parts[1];
                    int q = path.IndexOf('?');
                    if (q >= 0) path = path.Substring(0, q);
                    path = Uri.UnescapeDataString(path).TrimStart('/');
                    string full = Path.GetFullPath(Path.Combine(_root, path));
                    if (!full.StartsWith(_root, StringComparison.OrdinalIgnoreCase) || !File.Exists(full))
                    {
                        Send(ns, 404, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("404"));
                        return;
                    }
                    Send(ns, 200, Mime(full), File.ReadAllBytes(full));
                }
            }
            catch { }
        }

        private static string Mime(string f)
        {
            switch (Path.GetExtension(f).ToLowerInvariant())
            {
                case ".png": return "image/png";
                case ".jpg": case ".jpeg": return "image/jpeg";
                case ".gif": return "image/gif";
                case ".bmp": return "image/bmp";
                case ".webp": return "image/webp";
                default: return "application/octet-stream";
            }
        }

        private static void Send(NetworkStream ns, int code, string type, byte[] body)
        {
            string head = "HTTP/1.1 " + code + (code == 200 ? " OK" : " Not Found") + "\r\n"
                + "Content-Type: " + type + "\r\nContent-Length: " + body.Length
                + "\r\nConnection: close\r\n\r\n";
            byte[] hb = Encoding.ASCII.GetBytes(head);
            ns.Write(hb, 0, hb.Length);
            ns.Write(body, 0, body.Length);
            ns.Flush();
        }

        public static string LanIp()
        {
            try
            {
                foreach (IPAddress a in Dns.GetHostAddresses(Dns.GetHostName()))
                    if (a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a)) return a.ToString();
            }
            catch { }
            return "127.0.0.1";
        }
    }

    /// <summary>内置使用教程 + 本地图片 → URL（拖入自动生成）</summary>
    public class HelpWindow : Form
    {
        private readonly ListBox _topics;
        private readonly TextBox _body;
        private readonly LocalHttpServer _server = new LocalHttpServer();
        private readonly Panel _drop;
        private readonly TextBox _url;
        private readonly Label _srvState;

        private static readonly string[] Titles = new string[]
        {
            "0. 三十秒上手",
            "1. 颜色（三种模式）",
            "2. 样式 / 大小写",
            "3. 大小与字体",
            "4. 对齐与位置",
            "5. 换行 / 解析",
            "6. 超链接 <link>",
            "7. 渐变",
            "8. 循环",
            "9. 指令与括号/边框",
            "10. 资源类标签",
            "11. 旧版兼容标签",
            "12. 发送到游戏",
            "13. 在线取色 / 颜色库",
            "14. 本地图片 → URL",
            "15. UI 配色 / 背景 / 缩放",
            "16. 自由编辑系统",
            "17. 常见问题"
        };

        public HelpWindow()
        {
            Text = "使用教程 / 本地 URL";
            Font = new Font("Microsoft YaHei", 9f);
            ClientSize = new Size(900, 620);
            MinimumSize = new Size(760, 520);
            StartPosition = FormStartPosition.CenterParent;

            _topics = new ListBox();
            _topics.Location = new Point(12, 12);
            _topics.Size = new Size(180, 420);
            _topics.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Bottom;
            _topics.Items.AddRange(Titles);
            _topics.SelectedIndex = 0;
            _topics.SelectedIndexChanged += delegate { ShowTopic(); };
            Controls.Add(_topics);

            _body = new TextBox();
            _body.Multiline = true;
            _body.ReadOnly = true;
            _body.ScrollBars = ScrollBars.Both;
            _body.WordWrap = true;
            _body.Location = new Point(200, 12);
            _body.Size = new Size(686, 420);
            _body.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;
            Controls.Add(_body);

            _drop = new Panel();
            _drop.Location = new Point(12, 442);
            _drop.Size = new Size(180, 120);
            _drop.BorderStyle = BorderStyle.FixedSingle;
            _drop.AllowDrop = true;
            _drop.Anchor = AnchorStyles.Left | AnchorStyles.Bottom;
            _drop.Paint += delegate (object s, PaintEventArgs e)
            {
                TextRenderer.DrawText(e.Graphics, "把图片拖到这里\n自动生成本地 URL", Font, _drop.ClientRectangle,
                    Theme.SubText, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
            };
            _drop.DragEnter += delegate (object s, DragEventArgs e)
            {
                e.Effect = (e.Data.GetDataPresent(DataFormats.FileDrop)) ? DragDropEffects.Copy : DragDropEffects.None;
            };
            _drop.DragDrop += delegate (object s, DragEventArgs e)
            {
                string[] files = e.Data.GetData(DataFormats.FileDrop) as string[];
                if (files == null || files.Length == 0) return;
                MakeUrl(files[0]);
            };
            Controls.Add(_drop);

            _url = new TextBox();
            _url.Location = new Point(200, 442);
            _url.Size = new Size(560, 26);
            _url.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;
            Controls.Add(_url);

            Button copy = new Button();
            copy.Text = "复制 URL";
            copy.Size = new Size(100, 28);
            copy.Location = new Point(766, 441);
            copy.Anchor = AnchorStyles.Right | AnchorStyles.Bottom;
            copy.Click += delegate
            {
                if (_url.Text.Length == 0) return;
                try { Clipboard.SetText(_url.Text); } catch { }
            };
            Controls.Add(copy);

            _srvState = new Label();
            _srvState.Location = new Point(200, 474);
            _srvState.Size = new Size(560, 60);
            _srvState.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;
            _srvState.Tag = "hint";
            Controls.Add(_srvState);

            Button stop = new Button();
            stop.Text = "停止本地服务";
            stop.Size = new Size(120, 28);
            stop.Location = new Point(200, 534);
            stop.Anchor = AnchorStyles.Left | AnchorStyles.Bottom;
            stop.Click += delegate { _server.Stop(); UpdateServerState(); };
            Controls.Add(stop);

            Button open = new Button();
            open.Text = "浏览器打开";
            open.Size = new Size(110, 28);
            open.Location = new Point(330, 534);
            open.Anchor = AnchorStyles.Left | AnchorStyles.Bottom;
            open.Click += delegate
            {
                if (_url.Text.Length == 0) return;
                try { System.Diagnostics.Process.Start(_url.Text); } catch { }
            };
            Controls.Add(open);

            Button ok = new Button();
            ok.Text = "刷新并关闭";
            ok.Size = new Size(120, 30);
            ok.Location = new Point(766, 532);
            ok.Anchor = AnchorStyles.Right | AnchorStyles.Bottom;
            ok.Tag = "primary";
            ok.Click += delegate { DialogResult = DialogResult.OK; Close(); };
            Controls.Add(ok);

            FormClosed += delegate { _server.Stop(); };
            Theme.Apply(this);
            ShowTopic();
            UpdateServerState();
        }

        private void MakeUrl(string file)
        {
            string err = UiBackground.Validate(file);
            if (err != null && !IsImage(file)) { _url.Text = ""; _srvState.Text = "只能拖入图片：" + err; return; }

            string dir;
            if (!_server.Running)
            {
                dir = Path.Combine(Path.GetTempPath(), "RichTextGenImage");
                bool ok = false;
                for (int p = 8799; p < 8820 && !ok; p++) ok = _server.Start(dir, p);
                if (!ok) { _srvState.Text = "本地服务启动失败（端口被占用）"; return; }
            }
            dir = Path.Combine(Path.GetTempPath(), "RichTextGenImage");
            string name = Path.GetFileName(file);
            string target = Path.Combine(dir, name);
            try { File.Copy(file, target, true); } catch { }
            _url.Text = "http://" + LocalHttpServer.LanIp() + ":" + _server.Port + "/" + name;
            _srvState.Text = "本地文件：" + target + "\r\n局域网 URL：" + _url.Text
                + "\r\n（同一网络的设备/客户端可访问；游戏内能否显示取决于所用插件是否支持图片 URL）";
            UpdateServerState();
        }

        private static bool IsImage(string f)
        {
            string e = Path.GetExtension(f).ToLowerInvariant();
            return e == ".png" || e == ".jpg" || e == ".jpeg" || e == ".bmp" || e == ".gif" || e == ".webp";
        }

        private void UpdateServerState()
        {
            if (_server.Running)
                _srvState.Text = "本地图片服务：运行中 · 端口 " + _server.Port + " · 目录 " + Path.Combine(Path.GetTempPath(), "RichTextGenImage");
            else if (_srvState.Text.Length == 0)
                _srvState.Text = "本地图片服务：未启动（拖入图片会自动启动）";
        }

        private void ShowTopic()
        {
            int i = Math.Max(0, _topics.SelectedIndex);
            _body.Text = HelpText(i).Replace("\n", "\r\n");
        }

        private static string HelpText(int i)
        {
            switch (Titles[i])
            {
                case "0. 三十秒上手":
                    return
"① 上面输入文本（可多行）。\n" +
"② 「颜色」区选模式（Hex / RGB / 色名）并填颜色，或点「拾色器 / 颜色库…」挑色。\n" +
"③ 在下面各标签组里勾你要的标签并填参数（组标题下的「展开」可折叠/展开）。\n" +
"④ 右侧实时预览就是游戏里的大致样子，「生成的代码」可直接复制进游戏。\n" +
"⑤ 需要直接发进游戏 → 切到「发送到游戏」页。\n\n" +
"生成规则：勾选的标签会按固定顺序从外到内包裹文本，闭合顺序严格反向，保证嵌套合法。\n" +
"没填参数的标签会被跳过（不会产出非法标签）。";

                case "1. 颜色（三种模式）":
                    return
"颜色值支持三种写法，随时切换：\n" +
"  · #十六进制：#FF8800（也支持 #F80 简写、#FF8800AA 带透明度）\n" +
"  · RGB 十进制：255,136,0 或 rgb(255,136,0)\n" +
"  · 英文色名：orange（CSS 标准色名，内置 140 色）\n\n" +
"输出会按当前模式转成合法标签：\n" +
"  Hex / RGB 模式 → <color=#FF8800>\n" +
"  色名模式       → <color=orange>（该色没有精确色名时自动取最近色并在界面标「（近似）」）\n\n" +
"「文字颜色」标签的输入留空时，自动使用「颜色」区里选的主色。\n" +
"「高亮底色 mark」固定输出 #RRGGBBAA（自动补 80 半透明），不吃色名模式。\n" +
"「透明度 alpha」填两位十六进制：#00 全透明 → #FF 不透明。";

                case "2. 样式 / 大小写":
                    return
"样式（包裹正文）：\n" +
"  <b> 粗体 · <i> 斜体 · <u> 下划线 · <s> 删除线 · <sub> 下标 · <sup> 上标\n" +
"  <nobr> 不折行（整段不换行） · <noparse> 不解析（里面的标签原样显示）\n\n" +
"大小写（包裹正文）：\n" +
"  <uppercase> 全大写 · <lowercase> 全小写\n" +
"  <smallcaps> 小型大写 · <allcaps> 大写（含小写字母）\n\n" +
"提示：浏览器预览里 <noparse> 显示为等宽代码样式，便于看出「没被解析」。";

                case "3. 大小与字体":
                    return
"  <size> 字号：可写绝对 50、相对 +4 / -2、2em、50%\n" +
"  <font> 字体名：需要游戏内存在同名字体资源，否则不生效（界面已标「·需资源」）\n" +
"  <font-weight> 字重：100-900，同样需要字体资源支持\n\n" +
"字号是整套工具最常用的标签之一；配合「渐变」可以做逐字符大小+颜色变化。";

                case "4. 对齐与位置":
                    return
"这些是「整段/整行」级别（块级）的标签，会放在最外层，作用于整段文本：\n" +
"  <align=left|center|right|justified|flush> 整段对齐\n" +
"  <pos=50%|120px>      从该水平位置开始排版\n" +
"  <space=20px>         插入等宽空白\n" +
"  <width=200px>        限定文本宽度\n" +
"  <margin=10px>        边距（也可写 margin-left=10px）\n" +
"  <indent=15%>         整段缩进\n" +
"  <line-indent=10%>    首行缩进\n" +
"  <line-height=25px>   行高（25px / 120% / 1.5em）\n\n" +
"注意：游戏内提示/广播通常是单行或多行纯文本，块级标签的效果取决于显示组件，建议先小范围试。";

                case "5. 换行 / 解析":
                    return
"  <br>     在文本末尾插入换行（生成时追加在最后）\n" +
"  <nobr>   整段不折行\n" +
"  <noparse> 包裹内的标签不再解析，原样显示（想给玩家看「标签本身」时用）\n\n" +
"多行输入时：每一行都会独立生成一套标签（同一套样式），行与行之间用真实换行分隔。";

                case "6. 超链接 <link>":
                    return
"用法：在「超链接」组里勾选，然后填两个框：\n" +
"  · 左边 = 链接地址（例如 https://example.com/invite 或任意标识串）\n" +
"  · 右边 = 显示文字（玩家看到的那段字）\n" +
"  · 「替换正文」勾选框：\n" +
"      不勾（默认）→ 正文保留，链接追加在正文之后\n" +
"      勾上         → 显示文字替换正文（正文不再出现）\n\n" +
"它生成的代码形如：<link=\"地址\">显示文字</link>\n\n" +
"重要提醒：TextMeshPro 的 link 需要「鼠标点中文字」才可点，而游戏内的提示/广播一般\n" +
"不支持鼠标交互，所以实际效果通常是「按样式显示文字、点不了」。若你想显示成可读的\n" +
"网址，建议直接把 URL 当正文写。";

                case "7. 渐变":
                    return
"逐字符渐变：\n" +
"  1) 勾「启用逐字符渐变」\n" +
"  2) 填起始色 / 结束色（三种颜色写法都行，点「…」可用拾色器）\n" +
"  3) 生成时每个字符都会套一个 <color=...>，从起始色线性过渡到结束色\n\n" +
"渐变可以和别的标签叠加（渐变在最内层，外面再套 b/size/mark 等）。\n" +
"字符较多时输出会变长（每字符约 20+ 字符），注意游戏指令长度限制。";

                case "8. 循环":
                    return
"循环渐变：\n" +
"  勾「循环渐变」后，颜色按「周期(字符)」反复走一遍 起始色→结束色。\n" +
"  例：周期=5，文本 12 个字 → 每 5 个字一次完整渐变，循环重复；\n" +
"      周期设 0（或留空）→ 按整段文本长度算一次渐变（等于不循环）。\n\n" +
"典型用法：周期 3~6 做「彩虹流动」效果。";

                case "9. 指令与括号/边框":
                    return
"指令前缀：.BC = 全服广播 · .C = 团队 · .NC = 不加前缀。\n" +
"勾「生成结果带上指令前缀」后，生成的代码开头会带 .BC/.C（.NC 不加）。\n\n" +
"括号包裹：给整段外层套 （）【】「」『』《》{} [] ()。\n" +
"装饰边框：给整段加 ▌ ▌ / 【 】 / ◆ ◆ / ★ ★ 等视觉边框。\n\n" +
"顺序：边框 → 标签 → 括号 → 指令前缀（最外层是指令）。";

                case "10. 资源类标签":
                    return
"这三类标签语法完全合法，但浏览器预览无法还原，界面里用徽标（例如 <sprite=0>）如实标出：\n" +
"  <sprite>   精灵图：0 / index=3 / name=\"star\"（需要精灵资源，且该资源在字体资产里）\n" +
"  <style>    样式表：TMP 样式表名（需要样式资源）\n" +
"  <gradient> 渐变预设：TMP 渐变预设名（需要预设资源）\n\n" +
"结论：可以用，但能不能看到效果取决于游戏内是否配置了对应资源；不确定就先试一个小文本。";

                case "11. 旧版兼容标签":
                    return
"  <quad>     旧版 UGUI 图片标签\n" +
"  <material> 旧版 UGUI 材质标签\n\n" +
"这两个属于老版 UnityEngine.UI 富文本，TextMeshPro 一般不支持，实际多半没有效果。\n" +
"保留它们是为了「Unity 引擎支持的富文本标签全量囊括」，界面上已标「·旧版」。";

                case "12. 发送到游戏":
                    return
"切到「发送到游戏」页：\n" +
"  1) 顶部「游戏进程」会每 2 秒检测 SCP:SL，显示 ✓ 已锁定（拿到窗口句柄）/ ◐ 有进程无窗口 / ✗ 未检测到\n" +
"  2) 「要发送的内容」可直接粘贴富文本代码；或勾「使用『文本生成』页的最新结果」\n" +
"  3) 「指令前缀」选 .BC / .C / .NC / 自定义\n" +
"  4) 「发送方式」：\n" +
"       剪贴板粘贴（推荐）→ 激活游戏窗口 → 开控制台(~) → Ctrl+A → Ctrl+V → 回车 → 关控制台\n" +
"       直接键入（较慢）→ 逐字符敲入，适合控制台不接受粘贴的场合\n" +
"     「发送后保留游戏控制台打开」可留着看回显\n" +
"  5) 「发送预览」显示实际送进去的那一行；成功后写入「最近发送记录」\n\n" +
"注意：请把游戏设为「窗口化」，并尽量以管理员身份运行本程序，否则可能无法把游戏切到前台。";

                case "13. 在线取色 / 颜色库":
                    return
"拾色器窗口底部的「▼ 更多颜色（在线 / 内置色块）」：\n" +
"  · 联网时优先抓在线色名表（jsdelivr color-name 148 色 → color.pizza），\n" +
"    失败自动回退内置 140 色，状态栏会写明来源。\n" +
"  · 点任意色块 → 回填到拾色器里，再微调饱和度/明度即可。\n\n" +
"顶部状态栏的「● 在线 / ● 离线」是网络状态指示，每 5 秒刷新。";

                case "14. 本地图片 → URL":
                    return
"本窗口左下角那个方框：把图片拖进去，会自动：\n" +
"  1) 复制到临时目录（%TEMP%\\RichTextGenImage）\n" +
"  2) 启动一个内置的小型 HTTP 服务（自动选 8799 起的空闲端口）\n" +
"  3) 生成局域网 URL，例如 http://192.168.1.2:8799/图.png\n\n" +
"用途：把本地图片变成「能被其他设备/游戏客户端访问的 URL」——\n" +
"  游戏内要显示图片，取决于所用插件是否支持按 URL 加载图片；\n" +
"  同一局域网的手机/浏览器可以直接打开该 URL 验证。\n\n" +
"限制：图片 ≤ 8MB 且单边 ≤ 4096px（与背景图校验一致）；服务只共享这一个目录。\n" +
"用「停止本地服务」可随时关闭。";

                case "15. UI 配色 / 背景 / 缩放":
                    return
"· 缩放：顶部「缩放」下拉，70%~200% 整体缩放（字号 + 布局一起缩放），窗口会跟着变大变小。\n" +
"· UI 配色：顶部「UI 配色…」逐部件改色——窗体背景、面板、主文字/次要文字、边框、输入框、\n" +
"  按钮（底/字/边框）、强调色、预览背景/文字、色块描边。每一项都用 HTML 颜色选择器改，改完立即生效。\n" +
"  「恢复默认（跟随亮/深主题）」可一键回到当前主题的默认配色。\n" +
"· 背景：顶部「背景图…」\n" +
"    - 直接把图片拖进方框即可设为界面背景；\n" +
"    - 「对称显示」= 镜像平铺，四周对称；\n" +
"    - 「自适配」= 按窗口比例拉伸铺满；\n" +
"    - 图片超过 8MB 或单边超过 4096px → 拖入无效并提示（方框保持灰色）；\n" +
"    - 不想用图就用「选背景色…」（同样用 HTML 颜色选择器）当纯色背景。\n" +
"· 亮/深主题：顶部「配色」下拉，一键切换整界面（含预览底色）。";

                case "16. 自由编辑系统":
                    return
"顶部勾选「自由编辑系统」后，界面会整体展开：\n" +
"  · 所有标签分组（含高级：对齐与位置 / 字距与偏移 / 换行 / 资源类 / 旧版兼容）全部展开\n" +
"  · 窗口自动放大到更宽的尺寸，方便一次看到更多设置\n" +
"取消勾选 → 回到默认的收纳状态（常用组展开、其余收起）。\n" +
"它只是个「显示开关」，不影响生成结果。";

                default:
                    return
"Q1 生成出来没效果？\n" +
"   · 先看右侧预览；预览有效果而游戏里没有 → 多半是该标签需要资源（font/sprite/style/gradient）\n" +
"     或该显示组件不支持块级标签（align/pos/width/margin）。\n" +
"Q2 颜色写错了？\n" +
"   颜色框接受 #RRGGBB、#F80、255,136,0、orange；解析失败时下方会提示，此时该标签会被跳过。\n" +
"Q3 输出太长发不进游戏？\n" +
"   逐字符渐变每个字都要一段 <color>，文本长时建议改用「循环渐变」缩短，或减少标签层数。\n" +
"Q4 点「发送到游戏」提示窗口未激活？\n" +
"   游戏切「窗口化」；用管理员身份运行本程序；确认没有被其他置顶窗口遮挡。\n" +
"Q5 背景图拖进去没反应？\n" +
"   检查是不是超过 8MB 或单边超 4096px（会明确提示），或格式不是 png/jpg/bmp/gif。\n" +
"Q6 想回退到旧的 AHK 版？\n" +
"   旧脚本仍在 桌面\\所有快捷方式\\彩色文本生成器_v2.ahk，未做任何改动。";
            }
        }
    }
}
