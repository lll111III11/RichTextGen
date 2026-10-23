using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace RichTextGen
{
    /// <summary>模型里的一个可点击区域（坐标是 0~1 的图片相对位置）。</summary>
    internal sealed class Live2DRegion
    {
        public double X, Y, W = 1, H = 1;
        public string[] Click = new string[0];
        public string[] Hover = new string[0];
    }

    /// <summary>一个互动动作：说话 + 弹跳 / 旋转 / 摇摆。</summary>
    internal sealed class Live2DAction
    {
        public string[] Say = new string[0];
        public double Bounce, Spin, Wobble;
        public int Duration = 600;
        public int Cooldown = 800;
    }

    /// <summary>一个 Live2D / 2D 立绘 / 3D 模型（%APPDATA%\RichTextGen\live2d\&lt;目录&gt;\）。</summary>
    internal sealed class Live2DModelInfo
    {
        public string Id = "";
        public string Name = "";
        public string File = "";
        public string Type = "sprite";       // sprite / glb / vrm
        public string Blink = "";
        public int Width, Height;
        public string Dir = "";
        public string ConfigJson = "";       // model.json 原文（合法 JSON 才保留）
        public List<Live2DRegion> Regions = new List<Live2DRegion>();
        public Dictionary<string, Live2DAction> Actions =
            new Dictionary<string, Live2DAction>(StringComparer.OrdinalIgnoreCase);
        public string[] ClickDefault = new string[0];
        public string[] Chatter = new string[0];
        public int ChatterEvery = 120;

        public bool IsSprite { get { return Type == "sprite"; } }
        public string ConfigPath { get { return Path.Combine(Dir, "model.json"); } }
    }

    /// <summary>
    /// Live2D / 3D 角色支持：
    ///   · 模型 = 一个文件夹：model.png / model.gif（透明立绘）或 model.glb / model.vrm（真 3D）；
    ///   · model.json 描述互动：regions（点击/悬停区域）、actions（台词 + 动作）、idle（闲聊）；
    ///   · 显示方式：窗口内悬浮（立绘与 3D），或「拎出来」成独立透明窗口（立绘）；
    ///   · 3D 用按需下载的 three.js 运行时渲染（%APPDATA%\RichTextGen\live2d\_runtime）。
    /// </summary>
    internal static class Live2D
    {
        internal const string UrlPrefix = "/__l2d/";
        internal static Action Changed;
        internal static Action OpenSettings;

        public static bool Enabled;
        public static string Model = "";
        public static string Mode = "inwin";     // inwin / detached
        public static int Scale = 100;
        public static int Opacity = 100;
        public static bool Activity = true;
        public static bool Follow = true;
        public static bool Topmost = true;
        public static bool ClickThrough;
        public static int X = -1, Y = -1;

        internal static Live2DForm Detached;
        internal static string LastImportNote = "";   // 上次导入的说明（是否抠图等）
        private static bool _loaded;

        public static string Root { get { return Path.Combine(AiConfig.Dir, "live2d"); } }
        public static string RuntimeDir { get { return Path.Combine(Root, "_runtime"); } }
        private static string ConfigPath { get { return Path.Combine(AiConfig.Dir, "live2d.json"); } }

        public static bool ThreeReady
        {
            get
            {
                try
                {
                    return File.Exists(Path.Combine(RuntimeDir, "three.min.js")) &&
                           File.Exists(Path.Combine(RuntimeDir, "GLTFLoader.js"));
                }
                catch { return false; }
            }
        }

        /// <summary>Cubism（.moc3）运行时是否就绪：Core + PIXI + pixi-live2d-display。</summary>
        public static bool CubismReady
        {
            get
            {
                try
                {
                    return File.Exists(Path.Combine(RuntimeDir, "live2dcubismcore.min.js")) &&
                           File.Exists(Path.Combine(RuntimeDir, "pixi.min.js")) &&
                           File.Exists(Path.Combine(RuntimeDir, "cubism4.min.js"));
                }
                catch { return false; }
            }
        }

        // ============================================================ 配置
        public static void Load()
        {
            if (_loaded) return;
            _loaded = true;
            try
            {
                if (!File.Exists(ConfigPath)) return;
                string j = File.ReadAllText(ConfigPath, Encoding.UTF8);
                Enabled = GetBool(j, "enabled", Enabled);
                Model = GetStr(j, "model", Model);
                Mode = GetStr(j, "mode", Mode) == "detached" ? "detached" : "inwin";
                Scale = Clamp(GetInt(j, "scale", Scale), 20, 300);
                Opacity = Clamp(GetInt(j, "opacity", Opacity), 10, 100);
                Activity = GetBool(j, "activity", Activity);
                Follow = GetBool(j, "follow", Follow);
                Topmost = GetBool(j, "topmost", Topmost);
                ClickThrough = GetBool(j, "clickThrough", ClickThrough);
                X = GetInt(j, "x", X);
                Y = GetInt(j, "y", Y);
            }
            catch { }
        }

        public static void Save()
        {
            try
            {
                Directory.CreateDirectory(AiConfig.Dir);
                StringBuilder sb = new StringBuilder();
                sb.Append("{\"enabled\":").Append(Enabled ? "true" : "false");
                sb.Append(",\"model\":").Append(J(Model));
                sb.Append(",\"mode\":").Append(J(Mode));
                sb.Append(",\"scale\":").Append(Scale);
                sb.Append(",\"opacity\":").Append(Opacity);
                sb.Append(",\"activity\":").Append(Activity ? "true" : "false");
                sb.Append(",\"follow\":").Append(Follow ? "true" : "false");
                sb.Append(",\"topmost\":").Append(Topmost ? "true" : "false");
                sb.Append(",\"clickThrough\":").Append(ClickThrough ? "true" : "false");
                sb.Append(",\"x\":").Append(X);
                sb.Append(",\"y\":").Append(Y);
                sb.Append('}');
                File.WriteAllText(ConfigPath, sb.ToString(), new UTF8Encoding(false));
            }
            catch { }
        }

        // ============================================================ 模型扫描
        public static List<Live2DModelInfo> Scan()
        {
            List<Live2DModelInfo> list = new List<Live2DModelInfo>();
            try
            {
                Directory.CreateDirectory(Root);
                foreach (string d in Directory.GetDirectories(Root))
                {
                    if (string.Equals(Path.GetFileName(d), "_runtime", StringComparison.OrdinalIgnoreCase)) continue;

                    string sprite = null;
                    foreach (string cand in new[] { "model.png", "model.gif", "model.jpg", "model.jpeg", "model.bmp" })
                        if (File.Exists(Path.Combine(d, cand))) { sprite = cand; break; }
                    string mesh = null;
                    foreach (string cand in new[] { "model.glb", "model.vrm" })
                        if (File.Exists(Path.Combine(d, cand))) { mesh = cand; break; }
                    string cubism = null;
                    try
                    {
                        string[] m3 = Directory.GetFiles(d, "*.model3.json");
                        if (m3.Length > 0) cubism = Path.GetFileName(m3[0]);
                    }
                    catch { }

                    // model.json 里显式指定 file 时优先
                    Dictionary<string, object> cfg = null;
                    string cfgText = null;
                    try
                    {
                        string cp = Path.Combine(d, "model.json");
                        if (File.Exists(cp))
                        {
                            cfgText = File.ReadAllText(cp, Encoding.UTF8);
                            cfg = ParseObj(cfgText);
                        }
                    }
                    catch { }

                    if (cfg != null)
                    {
                        string f = Str(cfg, "file", "");
                        if (f.Length > 0 && File.Exists(Path.Combine(d, f)))
                        {
                            string fe = Path.GetExtension(f).ToLowerInvariant();
                            if (fe == ".glb" || fe == ".vrm") mesh = f;
                            else if (fe == ".png" || fe == ".gif" || fe == ".jpg" || fe == ".jpeg" || fe == ".bmp") sprite = f;
                            else if (f.ToLowerInvariant().EndsWith(".model3.json")) cubism = f;
                        }
                    }

                    if (sprite == null && mesh == null)
                    {
                        foreach (string f in Directory.GetFiles(d))
                        {
                            string ext = Path.GetExtension(f).ToLowerInvariant();
                            if (ext == ".png" || ext == ".gif" || ext == ".jpg" || ext == ".jpeg" || ext == ".bmp")
                            { sprite = Path.GetFileName(f); break; }
                            if (ext == ".glb" || ext == ".vrm")
                            { mesh = Path.GetFileName(f); break; }
                        }
                    }
                    if (sprite == null && mesh == null && cubism == null) continue;

                    Live2DModelInfo m = new Live2DModelInfo();
                    m.Id = Path.GetFileName(d);
                    m.Dir = d;
                    m.Name = m.Id;
                    if (sprite != null)
                    {
                        m.Type = "sprite";
                        m.File = sprite;
                        string blink = File.Exists(Path.Combine(d, "blink.png")) ? "blink.png" : "";
                        m.Blink = blink;
                        try
                        {
                            using (FileStream fs = new FileStream(Path.Combine(d, sprite), FileMode.Open, FileAccess.Read))
                            using (Image im = Image.FromStream(fs))
                            { m.Width = im.Width; m.Height = im.Height; }
                        }
                        catch { }
                    }
                    else if (mesh != null)
                    {
                        m.File = mesh;
                        m.Type = Path.GetExtension(mesh).ToLowerInvariant() == ".vrm" ? "vrm" : "glb";
                    }
                    else
                    {
                        m.File = cubism;
                        m.Type = "cubism";
                    }

                    if (cfgText != null)
                    {
                        m.ConfigJson = cfgText;
                        ApplyConfig(m, cfg);
                    }
                    list.Add(m);
                }
            }
            catch { }
            list.Sort(delegate (Live2DModelInfo a, Live2DModelInfo b)
            {
                return string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase);
            });
            return list;
        }

        private static void ApplyConfig(Live2DModelInfo m, Dictionary<string, object> root)
        {
            if (root == null) return;
            string nm = Str(root, "name", "");
            if (nm.Trim().Length > 0) m.Name = nm;
            string ty = Str(root, "type", "").ToLowerInvariant();
            if (ty == "glb" || ty == "vrm" || ty == "cubism") m.Type = ty;

            m.ClickDefault = StrArray(root, "click");

            object rv;
            if (root.TryGetValue("regions", out rv))
            {
                object[] arr = rv as object[];
                if (arr != null)
                {
                    foreach (object o in arr)
                    {
                        Dictionary<string, object> d = o as Dictionary<string, object>;
                        if (d == null) continue;
                        Live2DRegion r = new Live2DRegion();
                        r.X = Num(d, "x", 0);
                        r.Y = Num(d, "y", 0);
                        r.W = Num(d, "w", 1);
                        r.H = Num(d, "h", 1);
                        r.Click = StrArray(d, "click");
                        r.Hover = StrArray(d, "hover");
                        if (r.W > 0 && r.H > 0) m.Regions.Add(r);
                    }
                }
            }

            object av;
            if (root.TryGetValue("actions", out av))
            {
                Dictionary<string, object> ad = av as Dictionary<string, object>;
                if (ad != null)
                {
                    foreach (KeyValuePair<string, object> kv in ad)
                    {
                        Dictionary<string, object> d = kv.Value as Dictionary<string, object>;
                        if (d == null) continue;
                        Live2DAction a = new Live2DAction();
                        a.Say = StrArray(d, "say");
                        a.Cooldown = (int)Num(d, "cooldown", 800);
                        a.Duration = (int)Num(d, "duration", 600);
                        object anv;
                        if (d.TryGetValue("anim", out anv))
                        {
                            Dictionary<string, object> an = anv as Dictionary<string, object>;
                            if (an != null)
                            {
                                a.Bounce = Num(an, "bounce", 0);
                                a.Spin = Num(an, "spin", 0);
                                a.Wobble = Num(an, "wobble", 0);
                            }
                        }
                        m.Actions[kv.Key] = a;
                    }
                }
            }

            object iv;
            if (root.TryGetValue("idle", out iv))
            {
                Dictionary<string, object> id = iv as Dictionary<string, object>;
                if (id != null)
                {
                    m.Chatter = StrArray(id, "chatter");
                    m.ChatterEvery = Math.Max(20, (int)Num(id, "every", 120));
                }
            }
        }

        public static Live2DModelInfo Current()
        {
            List<Live2DModelInfo> all = Scan();
            if (all.Count == 0) return null;
            foreach (Live2DModelInfo m in all)
                if (string.Equals(m.Id, Model, StringComparison.OrdinalIgnoreCase)) return m;
            return all[0];
        }

        public static string ModelsJson()
        {
            StringBuilder sb = new StringBuilder("[");
            List<Live2DModelInfo> all = Scan();
            for (int i = 0; i < all.Count; i++)
            {
                Live2DModelInfo m = all[i];
                if (i > 0) sb.Append(',');
                sb.Append("{\"id\":").Append(J(m.Id))
                  .Append(",\"name\":").Append(J(m.Name))
                  .Append(",\"type\":").Append(J(m.Type))
                  .Append(",\"file\":").Append(J(m.File))
                  .Append(",\"w\":").Append(m.Width)
                  .Append(",\"h\":").Append(m.Height)
                  .Append('}');
            }
            sb.Append(']');
            return sb.ToString();
        }

        public static string StateJson()
        {
            Live2DModelInfo m = Current();
            string url = "", blink = "";
            if (m != null)
            {
                string baseUrl = "https://richtextgen.local" + UrlPrefix +
                                 Uri.EscapeDataString(m.Id) + "/";
                url = baseUrl + Uri.EscapeDataString(m.File);
                if (m.Blink.Length > 0) blink = baseUrl + Uri.EscapeDataString(m.Blink);
            }
            StringBuilder sb = new StringBuilder();
            sb.Append("{\"enabled\":").Append(Enabled ? "true" : "false");
            sb.Append(",\"model\":").Append(J(m != null ? m.Id : ""));
            sb.Append(",\"name\":").Append(J(m != null ? m.Name : ""));
            sb.Append(",\"type\":").Append(J(m != null ? m.Type : "sprite"));
            sb.Append(",\"mode\":").Append(J(Mode));
            sb.Append(",\"scale\":").Append(Scale);
            sb.Append(",\"opacity\":").Append(Opacity);
            sb.Append(",\"activity\":").Append(Activity ? "true" : "false");
            sb.Append(",\"follow\":").Append(Follow ? "true" : "false");
            sb.Append(",\"topmost\":").Append(Topmost ? "true" : "false");
            sb.Append(",\"clickThrough\":").Append(ClickThrough ? "true" : "false");
            sb.Append(",\"url\":").Append(J(url));
            sb.Append(",\"blink\":").Append(J(blink));
            sb.Append(",\"cfgJson\":").Append(J(m != null ? m.ConfigJson : ""));
            sb.Append(",\"threeReady\":").Append(ThreeReady ? "true" : "false");
            sb.Append(",\"cubismReady\":").Append(CubismReady ? "true" : "false");
            sb.Append(",\"detachedOk\":").Append(m != null && m.IsSprite ? "true" : "false");
            sb.Append('}');
            return sb.ToString();
        }

        /// <summary>界面提交配置（JSON），保存后同步窗口内 / 独立窗口两种显示方式。</summary>
        public static string Apply(string json)
        {
            Load();
            if (!string.IsNullOrEmpty(json))
            {
                Enabled = GetBool(json, "enabled", Enabled);
                Model = GetStr(json, "model", Model);
                string mode = GetStr(json, "mode", Mode);
                Mode = mode == "detached" ? "detached" : "inwin";
                Scale = Clamp(GetInt(json, "scale", Scale), 20, 300);
                Opacity = Clamp(GetInt(json, "opacity", Opacity), 10, 100);
                Activity = GetBool(json, "activity", Activity);
                Follow = GetBool(json, "follow", Follow);
                Topmost = GetBool(json, "topmost", Topmost);
                ClickThrough = GetBool(json, "clickThrough", ClickThrough);
            }
            Save();
            Sync();
            return StateJson();
        }

        /// <summary>按当前配置创建 / 更新 / 关闭独立角色窗口（3D 仅支持窗口内，不建分离窗）。</summary>
        public static void Sync()
        {
            Load();
            Live2DModelInfo m = Current();
            bool want = Enabled && Mode == "detached" && m != null && m.IsSprite;
            if (!want)
            {
                if (Detached != null)
                {
                    Live2DForm f = Detached;
                    Detached = null;
                    try { f.Close(); f.Dispose(); } catch { }
                }
                return;
            }
            if (Detached == null || Detached.IsDisposed || Detached.ModelId != m.Id)
            {
                if (Detached != null)
                {
                    Live2DForm old = Detached;
                    Detached = null;
                    try { old.Close(); old.Dispose(); } catch { }
                }
                Live2DForm f = new Live2DForm(m);
                Detached = f;
                f.Show();
            }
            else
            {
                Detached.ApplyConfig();
            }
        }

        public static void Shutdown()
        {
            if (Detached != null)
            {
                Live2DForm f = Detached;
                Detached = null;
                try { f.Close(); f.Dispose(); } catch { }
            }
        }

        internal static void Notify()
        {
            try { if (Changed != null) Changed(); } catch { }
        }

        public static void SavePos(int x, int y)
        {
            X = x; Y = y;
            Save();
        }

        // ============================================================ 文件
        /// <summary>把 /__l2d/&lt;id&gt;/&lt;file&gt; 解析成磁盘路径（限制在模型根目录内）。</summary>
        public static string ResolveFile(string id, string file)
        {
            try
            {
                if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(file)) return null;
                if (id.IndexOfAny(new[] { '/', '\\' }) >= 0 || id.Contains("..")) return null;

                // 允许子目录（moc3 模型包里的 textures/xxx.png、motion/xxx.json）
                string rel = file.Replace('\\', '/');
                if (rel.Contains("..") || rel.Contains(":")) return null;
                string root = Path.GetFullPath(Root);
                string p = Path.GetFullPath(Path.Combine(root, id, rel.Replace('/', Path.DirectorySeparatorChar)));
                if (!p.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return null;
                return File.Exists(p) ? p : null;
            }
            catch { return null; }
        }

        public static void OpenFolder()
        {
            try
            {
                Directory.CreateDirectory(Root);
                System.Diagnostics.Process.Start("explorer.exe", "\"" + Root + "\"");
            }
            catch { }
        }

        /// <summary>按角色名联网找模型（打开浏览器搜索）。</summary>
        public static void FindModelOnline(string name)
        {
            try
            {
                name = (name ?? "").Trim();
                if (name.Length == 0)
                {
                    Live2DModelInfo m = Current();
                    name = m != null ? m.Name : "Live2D";
                }
                int p = name.IndexOfAny(new[] { '（', '(' });
                if (p > 0) name = name.Substring(0, p).Trim();
                string q = Uri.EscapeDataString(name + " Live2D 模型 免费 下载");
                System.Diagnostics.Process.Start("https://www.bing.com/search?q=" + q + "&setlang=zh-CN");
            }
            catch { }
        }

        /// <summary>打开当前模型的 model.json（没有就先写一份默认互动配置）。</summary>
        public static void OpenConfig()
        {
            try
            {
                Live2DModelInfo m = Current();
                if (m == null) { OpenFolder(); return; }
                if (!File.Exists(m.ConfigPath)) WriteDefaultConfig(m, true);
                try { System.Diagnostics.Process.Start("notepad.exe", "\"" + m.ConfigPath + "\""); }
                catch { System.Diagnostics.Process.Start(m.ConfigPath); }
            }
            catch { }
        }

        /// <summary>选文件导入成模型；返回新模型目录名（失败返回空）。</summary>
        public static string Import(Form owner)
        {
            try
            {
                using (OpenFileDialog d = new OpenFileDialog())
                {
                    d.Filter = "角色图片 / 3D / Live2D|*.png;*.gif;*.bmp;*.jpg;*.jpeg;*.glb;*.vrm;*.model3.json|所有文件|*.*";
                    d.Title = "选择 Live2D 角色图片、3D 模型或 .model3.json";
                    if (d.ShowDialog(owner) != DialogResult.OK) return "";
                    return ImportFile(d.FileName, true);
                }
            }
            catch { return ""; }
        }

        /// <summary>选一个文件夹导入整包（.moc3 模型通常是一个文件夹）。</summary>
        public static string ImportFolder(Form owner)
        {
            try
            {
                using (FolderBrowserDialog d = new FolderBrowserDialog())
                {
                    d.Description = "选择 Live2D 模型文件夹（里面要有 .model3.json 和 .moc3）";
                    d.ShowNewFolderButton = false;
                    if (d.ShowDialog(owner) != DialogResult.OK) return "";
                    return ImportDirectory(d.SelectedPath);
                }
            }
            catch { return ""; }
        }

        /// <summary>把整个模型目录复制进模型库。</summary>
        public static string ImportDirectory(string srcDir)
        {
            LastImportNote = "";
            try
            {
                if (!Directory.Exists(srcDir)) return "";
                string src = Path.GetFullPath(srcDir);
                string root = Path.GetFullPath(Root);
                if (src.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                { LastImportNote = "该目录已经在模型库里"; return ""; }

                string name = Path.GetFileName(src.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
                if (name.Trim().Length == 0) name = "Live2D模型";

                string dir = Path.Combine(root, name);
                int n = 2;
                while (Directory.Exists(dir) && HasModelFile(dir))
                    dir = Path.Combine(root, name + "-" + (n++));

                if (!Directory.Exists(dir)) CopyDirectory(src, dir);
                else CopyDirectory(src, dir);

                Live2DModelInfo m = ReadModel(dir);
                if (m == null) { LastImportNote = "没有找到 model3.json / 模型文件"; return ""; }
                if (!File.Exists(m.ConfigPath)) WriteDefaultConfig(m, true);

                Model = Path.GetFileName(dir);
                Enabled = true;
                Save();
                LastImportNote = "已导入模型包（" + m.Type + "）";
                return Model;
            }
            catch (Exception ex)
            {
                LastImportNote = "导入失败：" + ex.Message;
                return "";
            }
        }

        private static void CopyDirectory(string src, string dst)
        {
            Directory.CreateDirectory(dst);
            foreach (string f in Directory.GetFiles(src))
            {
                try { File.Copy(f, Path.Combine(dst, Path.GetFileName(f)), true); } catch { }
            }
            foreach (string d in Directory.GetDirectories(src))
            {
                try { CopyDirectory(d, Path.Combine(dst, Path.GetFileName(d))); } catch { }
            }
        }

        /// <summary>
        /// 自动抠图：把与边缘连通的近似纯色背景变透明，做 1px 羽化，再裁掉四周空白，输出透明 PNG。
        /// 适合立绘 / 白底或黑底的角色图；复杂照片背景效果有限。
        /// </summary>
        public static bool CutoutToPng(string src, string dest, out string err)
        {
            err = "";
            Bitmap work = null;
            try
            {
                byte[] data = File.ReadAllBytes(src);
                using (MemoryStream ms = new MemoryStream(data))
                using (Image im = Image.FromStream(ms))
                {
                    work = new Bitmap(im.Width, im.Height, PixelFormat.Format32bppArgb);
                    using (Graphics g = Graphics.FromImage(work))
                    {
                        g.Clear(Color.Transparent);
                        g.DrawImage(im, 0, 0, im.Width, im.Height);
                    }
                }
            }
            catch (Exception ex)
            {
                err = "图片读取失败：" + ex.Message;
                return false;
            }

            try
            {
                int w = work.Width, h = work.Height;
                BitmapData bd = work.LockBits(new Rectangle(0, 0, w, h),
                    ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
                bool removed = false;
                try
                {
                    int stride = bd.Stride;
                    byte[] px = new byte[stride * h];
                    Marshal.Copy(bd.Scan0, px, 0, px.Length);

                    Func<int, int, int> ai = (x, y) => y * stride + x * 4;

                    // 已经有明显透明区域就不再抠图，只裁剪
                    int transparent = 0, border = 0;
                    for (int x = 0; x < w; x += 2)
                    {
                        if (px[ai(x, 0) + 3] < 32) transparent++;
                        if (px[ai(x, h - 1) + 3] < 32) transparent++;
                        border += 2;
                    }
                    for (int y = 0; y < h; y += 2)
                    {
                        if (px[ai(0, y) + 3] < 32) transparent++;
                        if (px[ai(w - 1, y) + 3] < 32) transparent++;
                        border += 2;
                    }
                    bool hasAlpha = border > 0 && transparent * 100 / border > 15;

                    if (!hasAlpha)
                    {
                        // 边缘出现最多的颜色（16 级量化）当背景色
                        Dictionary<int, int> hist = new Dictionary<int, int>();
                        for (int x = 0; x < w; x += 3) { Bucket(px, ai(x, 0), hist); Bucket(px, ai(x, h - 1), hist); }
                        for (int y = 0; y < h; y += 3) { Bucket(px, ai(0, y), hist); Bucket(px, ai(w - 1, y), hist); }
                        int bestKey = 0, bestN = -1;
                        foreach (KeyValuePair<int, int> kv in hist)
                            if (kv.Value > bestN) { bestN = kv.Value; bestKey = kv.Key; }

                        long sr = 0, sg = 0, sb = 0; int cnt = 0;
                        for (int y = 0; y < h; y += 3)
                            for (int x = 0; x < w; x += 3)
                            {
                                int i = ai(x, y);
                                if (Key(px[i + 2], px[i + 1], px[i]) == bestKey)
                                { sr += px[i + 2]; sg += px[i + 1]; sb += px[i]; cnt++; }
                            }
                        if (cnt == 0) { work.UnlockBits(bd); try { work.Save(dest, ImageFormat.Png); } catch { } return false; }

                        double br = (double)sr / cnt, bg = (double)sg / cnt, bb = (double)sb / cnt;

                        // 容差：边缘像素到背景色的平均距离
                        double sum = 0; int n = 0;
                        for (int x = 0; x < w; x += 3)
                        {
                            sum += Dist(px, ai(x, 0), br, bg, bb); n++;
                            sum += Dist(px, ai(x, h - 1), br, bg, bb); n++;
                        }
                        for (int y = 0; y < h; y += 3)
                        {
                            sum += Dist(px, ai(0, y), br, bg, bb); n++;
                            sum += Dist(px, ai(w - 1, y), br, bg, bb); n++;
                        }
                        double mean = n > 0 ? sum / n : 0;
                        double tol = Math.Max(26, Math.Min(80, mean * 1.8 + 24));

                        // 从四边泛洪，只去掉与边缘连通的背景
                        byte[] seen = new byte[w * h];
                        int[] queue = new int[w * h];
                        int qh = 0, qt = 0;
                        for (int x = 0; x < w; x++)
                        {
                            Seed(px, ai(x, 0), x, w, br, bg, bb, tol, seen, queue, ref qt);
                            Seed(px, ai(x, h - 1), x, w, br, bg, bb, tol, seen, queue, ref qt);
                        }
                        for (int y = 0; y < h; y++)
                        {
                            Seed(px, ai(0, y), y * w, w, br, bg, bb, tol, seen, queue, ref qt);
                            Seed(px, ai(w - 1, y), y * w + w - 1, w, br, bg, bb, tol, seen, queue, ref qt);
                        }

                        while (qh < qt)
                        {
                            int idx = queue[qh++];
                            int x = idx % w, y = idx / w;
                            if (x > 0) Try(px, ai(x - 1, y), idx - 1, br, bg, bb, tol, seen, queue, ref qt, w * h);
                            if (x < w - 1) Try(px, ai(x + 1, y), idx + 1, br, bg, bb, tol, seen, queue, ref qt, w * h);
                            if (y > 0) Try(px, ai(x, y - 1), idx - w, br, bg, bb, tol, seen, queue, ref qt, w * h);
                            if (y < h - 1) Try(px, ai(x, y + 1), idx + w, br, bg, bb, tol, seen, queue, ref qt, w * h);
                        }

                        int total = w * h;

                        // 深色背景下，两腿之间之类被轮廓包住的空隙不与边缘连通，
                        // 再按「大面积近似背景色连通块」补一刀（只对深色背景做，避免误删白裙子）
                        double bgLuma = 0.299 * br + 0.587 * bg + 0.114 * bb;
                        if (bgLuma < 60)
                        {
                            byte[] seen2 = new byte[total];
                            List<int> comp = new List<int>();
                            int minArea = Math.Max(400, total / 200);
                            for (int i = 0; i < total; i++)
                            {
                                if (seen[i] != 0 || seen2[i] != 0) continue;
                                int sx = i % w, sy = i / w;
                                int si = ai(sx, sy);
                                if (px[si + 3] == 0 || Dist(px, si, br, bg, bb) > tol * 0.75) continue;

                                comp.Clear();
                                qh = 0; qt = 0;
                                queue[qt++] = i; seen2[i] = 1;
                                while (qh < qt)
                                {
                                    int idx = queue[qh++];
                                    comp.Add(idx);
                                    int x = idx % w, y = idx / w;
                                    int[] nb = { x > 0 ? idx - 1 : -1, x < w - 1 ? idx + 1 : -1,
                                                 y > 0 ? idx - w : -1, y < h - 1 ? idx + w : -1 };
                                    for (int k = 0; k < 4; k++)
                                    {
                                        int nx2 = nb[k];
                                        if (nx2 < 0 || seen[nx2] != 0 || seen2[nx2] != 0) continue;
                                        int ni = ai(nx2 % w, nx2 / w);
                                        if (px[ni + 3] == 0 || Dist(px, ni, br, bg, bb) <= tol * 0.75)
                                        { seen2[nx2] = 1; queue[qt++] = nx2; }
                                    }
                                }
                                if (comp.Count >= minArea)
                                    foreach (int idx in comp) seen[idx] = 1;
                            }
                        }

                        int hit = 0;
                        for (int y = 0; y < h; y++)
                            for (int x = 0; x < w; x++)
                            {
                                if (seen[y * w + x] == 0) continue;
                                int i = ai(x, y);
                                px[i] = 0; px[i + 1] = 0; px[i + 2] = 0; px[i + 3] = 0;
                                hit++;
                            }

                        removed = hit > total / 100;
                        if (!removed)
                        {
                            // 几乎没抠掉东西 → 还原成原图
                            for (int y = 0; y < h; y++)
                                for (int x = 0; x < w; x++)
                                    if (seen[y * w + x] != 0) px[ai(x, y) + 3] = 255;
                        }
                        else
                        {
                            // 边界 1px 羽化，减轻锯齿
                            for (int y = 1; y < h - 1; y++)
                                for (int x = 1; x < w - 1; x++)
                                {
                                    int i = y * w + x;
                                    if (seen[i] != 0) continue;
                                    if (seen[i - 1] != 0 || seen[i + 1] != 0 || seen[i - w] != 0 || seen[i + w] != 0)
                                    {
                                        int a = px[ai(x, y) + 3];
                                        if (a > 170) px[ai(x, y) + 3] = 170;
                                    }
                                }
                        }
                    }

                    Marshal.Copy(px, 0, bd.Scan0, px.Length);
                }
                finally { work.UnlockBits(bd); }

                // 裁掉四周空白（留 4px）
                Rectangle rc = ContentBounds(work, 4);
                Bitmap result = work;
                if (rc.Width > 0 && rc.Height > 0 && (rc.Width < work.Width || rc.Height < work.Height))
                {
                    result = work.Clone(rc, PixelFormat.Format32bppArgb);
                    work.Dispose();
                }
                result.Save(dest, ImageFormat.Png);
                result.Dispose();
                return removed;
            }
            catch (Exception ex)
            {
                err = "抠图失败：" + ex.Message;
                try { work.Dispose(); } catch { }
                return false;
            }
        }

        private static void Bucket(byte[] px, int i, Dictionary<int, int> hist)
        {
            int key = Key(px[i + 2], px[i + 1], px[i]);
            int n;
            hist.TryGetValue(key, out n);
            hist[key] = n + 1;
        }

        private static int Key(int r, int g, int b)
        {
            return ((r >> 4) << 8) | ((g >> 4) << 4) | (b >> 4);
        }

        private static double Dist(byte[] px, int i, double r, double g, double b)
        {
            double dr = px[i + 2] - r, dg = px[i + 1] - g, db = px[i] - b;
            return Math.Sqrt(dr * dr + dg * dg + db * db);
        }

        private static void Seed(byte[] px, int i, int idx, int w, double r, double g, double b,
                                 double tol, byte[] seen, int[] q, ref int qt)
        {
            if (seen[idx] != 0) return;
            if (px[i + 3] == 0 || Dist(px, i, r, g, b) <= tol)
            {
                seen[idx] = 1;
                q[qt++] = idx;
            }
        }

        private static void Try(byte[] px, int i, int idx, double r, double g, double b,
                                double tol, byte[] seen, int[] q, ref int qt, int total)
        {
            if (idx < 0 || idx >= total || seen[idx] != 0) return;
            if (px[i + 3] == 0 || Dist(px, i, r, g, b) <= tol)
            {
                seen[idx] = 1;
                q[qt++] = idx;
            }
        }

        private static Rectangle ContentBounds(Bitmap bmp, int pad)
        {
            int w = bmp.Width, h = bmp.Height;
            int minX = w, minY = h, maxX = -1, maxY = -1;
            BitmapData bd = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                byte[] px = new byte[bd.Stride * h];
                Marshal.Copy(bd.Scan0, px, 0, px.Length);
                for (int y = 0; y < h; y++)
                {
                    int row = y * bd.Stride;
                    for (int x = 0; x < w; x++)
                    {
                        if (px[row + x * 4 + 3] > 8)
                        {
                            if (x < minX) minX = x;
                            if (x > maxX) maxX = x;
                            if (y < minY) minY = y;
                            if (y > maxY) maxY = y;
                        }
                    }
                }
            }
            finally { bmp.UnlockBits(bd); }

            if (maxX < 0) return new Rectangle(0, 0, w, h);
            minX = Math.Max(0, minX - pad); minY = Math.Max(0, minY - pad);
            maxX = Math.Min(w - 1, maxX + pad); maxY = Math.Min(h - 1, maxY + pad);
            return new Rectangle(minX, minY, maxX - minX + 1, maxY - minY + 1);
        }

        /// <summary>把外部文件复制成模型包；withDefault=true 时若没有 model.json 就写一份默认互动配置。</summary>
        public static string ImportFile(string src, bool withDefault)
        {
            LastImportNote = "";
            try
            {
                if (!File.Exists(src)) return "";
                string ext = Path.GetExtension(src).ToLowerInvariant();
                if (src.ToLowerInvariant().EndsWith(".model3.json"))
                    return ImportDirectory(Path.GetDirectoryName(src));
                bool is3d = ext == ".glb" || ext == ".vrm";
                bool isStill = ext == ".png" || ext == ".jpg" || ext == ".jpeg" || ext == ".bmp";
                string name = Path.GetFileNameWithoutExtension(src);
                foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
                if (name.Trim().Length == 0) name = is3d ? "3D角色" : "角色";

                string dir = Path.Combine(Root, name);
                int n = 2;
                while (Directory.Exists(dir) && HasModelFile(dir))
                    dir = Path.Combine(Root, name + "-" + (n++));
                Directory.CreateDirectory(dir);

                if (isStill)
                {
                    // 静态图片：先自动抠图成透明 PNG（黑底 / 白底立绘效果最好）
                    string png = Path.Combine(dir, "model.png");
                    string err;
                    bool cut = CutoutToPng(src, png, out err);
                    if (File.Exists(png))
                        LastImportNote = cut ? "已自动抠图" : (err.Length > 0 ? "未抠图：" + err : "已是透明图，仅裁剪空白");
                    else
                    {
                        File.Copy(src, png, true);
                        LastImportNote = "抠图失败，已按原图导入";
                    }
                }
                else
                {
                    File.Copy(src, Path.Combine(dir, "model" + ext), true);
                }

                if (!is3d)
                {
                    // 同一目录下的 blink.png 顺手带上（也做同样的抠图）
                    try
                    {
                        string blink = Path.Combine(Path.GetDirectoryName(src), "blink.png");
                        if (File.Exists(blink) && !string.Equals(blink, src, StringComparison.OrdinalIgnoreCase))
                        {
                            string bp = Path.Combine(dir, "blink.png");
                            if (isStill)
                            {
                                string e2;
                                if (!CutoutToPng(blink, bp, out e2)) { /* 抠图失败就不带，避免黑底闪一下 */ }
                            }
                            else File.Copy(blink, bp, true);
                        }
                    }
                    catch { }
                }

                string id = Path.GetFileName(dir);
                Live2DModelInfo m = ReadModel(dir);
                if (m != null && withDefault && !File.Exists(Path.Combine(dir, "model.json")))
                    WriteDefaultConfig(m, true);

                Model = id;
                Enabled = true;
                Save();
                return id;
            }
            catch { return ""; }
        }

        private static bool HasModelFile(string dir)
        {
            try
            {
                foreach (string f in Directory.GetFiles(dir))
                {
                    string e = Path.GetExtension(f).ToLowerInvariant();
                    if (e == ".png" || e == ".gif" || e == ".jpg" || e == ".jpeg" ||
                        e == ".bmp" || e == ".glb" || e == ".vrm") return true;
                    if (f.ToLowerInvariant().EndsWith(".model3.json")) return true;
                }
            }
            catch { }
            return false;
        }

        /// <summary>读单个模型目录（不扫描全部）。</summary>
        public static Live2DModelInfo ReadModel(string dir)
        {
            try
            {
                foreach (Live2DModelInfo m in Scan())
                    if (string.Equals(m.Dir, dir, StringComparison.OrdinalIgnoreCase)) return m;
            }
            catch { }
            return null;
        }

        /// <summary>默认互动配置：头 / 身体两块可点区域 + 两句台词 + 闲聊。</summary>
        public static string DefaultConfigJson(Live2DModelInfo m)
        {
            string name = m != null ? m.Name : "角色";
            StringBuilder sb = new StringBuilder();
            sb.Append("{\n  \"name\": ").Append(J(name)).Append(",\n");
            sb.Append("  \"click\": [\"pat\"],\n");
            sb.Append("  \"regions\": [\n");
            sb.Append("    {\"id\": \"head\", \"x\": 0.30, \"y\": 0.03, \"w\": 0.40, \"h\": 0.30, \"click\": [\"pat\"], \"hover\": [\"notice\"]},\n");
            sb.Append("    {\"id\": \"body\", \"x\": 0.15, \"y\": 0.33, \"w\": 0.70, \"h\": 0.64, \"click\": [\"poke\"]}\n");
            sb.Append("  ],\n");
            sb.Append("  \"actions\": {\n");
            sb.Append("    \"pat\": {\"say\": [\"唔…别摸头啦～\", \"再摸一下也可以…\"], \"anim\": {\"bounce\": 8, \"wobble\": 5}, \"cooldown\": 1200},\n");
            sb.Append("    \"poke\": {\"say\": [\"呀！\", \"怎么啦？\"], \"anim\": {\"bounce\": 14}, \"cooldown\": 1000},\n");
            sb.Append("    \"notice\": {\"say\": [\"嗯？\"], \"anim\": {\"wobble\": 3}, \"cooldown\": 4000},\n");
            sb.Append("    \"greet\": {\"say\": [\"你好呀！\"], \"anim\": {\"bounce\": 12, \"spin\": 6}, \"cooldown\": 2000}\n");
            sb.Append("  },\n");
            sb.Append("  \"idle\": {\"chatter\": [\"今天也要加油哦～\", \"有点无聊…\", \"要不要休息一下？\"], \"every\": 150}\n");
            sb.Append("}\n");
            return sb.ToString();
        }

        public static void WriteDefaultConfig(Live2DModelInfo m, bool overwrite)
        {
            try
            {
                if (m == null) return;
                string p = m.ConfigPath;
                if (File.Exists(p) && !overwrite) return;
                File.WriteAllText(p, DefaultConfigJson(m), new UTF8Encoding(false));
            }
            catch { }
        }

        // ============================================================ JSON 小工具
        internal static string J(string s)
        {
            if (s == null) s = "";
            StringBuilder sb = new StringBuilder("\"");
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < ' ') sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            return sb.Append('"').ToString();
        }

        internal static Dictionary<string, object> ParseObj(string json)
        {
            try { return new JavaScriptSerializer().DeserializeObject(json) as Dictionary<string, object>; }
            catch { return null; }
        }

        internal static string Str(Dictionary<string, object> d, string key, string def)
        {
            object v;
            return d != null && d.TryGetValue(key, out v) && v != null ? v.ToString() : def;
        }

        internal static double Num(Dictionary<string, object> d, string key, double def)
        {
            object v;
            if (d == null || !d.TryGetValue(key, out v) || v == null) return def;
            try
            {
                if (v is double) return (double)v;
                if (v is int) return (int)v;
                if (v is long) return (long)v;
                if (v is decimal) return (double)(decimal)v;
                double x;
                return double.TryParse(v.ToString(), out x) ? x : def;
            }
            catch { return def; }
        }

        internal static string[] StrArray(Dictionary<string, object> d, string key)
        {
            object v;
            if (d == null || !d.TryGetValue(key, out v) || v == null) return new string[0];
            string single = v as string;
            if (single != null) return new[] { single };
            object[] arr = v as object[];
            if (arr == null) return new string[0];
            List<string> list = new List<string>();
            foreach (object o in arr)
                if (o != null) list.Add(o.ToString());
            return list.ToArray();
        }

        private static string GetStr(string json, string key, string def)
        {
            try
            {
                Match m = Regex.Match(json, "\"" + key + "\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
                if (!m.Success) return def;
                return Regex.Unescape(m.Groups[1].Value);
            }
            catch { return def; }
        }

        private static int GetInt(string json, string key, int def)
        {
            Match m = Regex.Match(json, "\"" + key + "\"\\s*:\\s*(-?\\d+)");
            int v;
            return m.Success && int.TryParse(m.Groups[1].Value, out v) ? v : def;
        }

        private static bool GetBool(string json, string key, bool def)
        {
            Match m = Regex.Match(json, "\"" + key + "\"\\s*:\\s*(true|false)");
            return m.Success ? m.Groups[1].Value == "true" : def;
        }

        private static int Clamp(int v, int lo, int hi)
        {
            return v < lo ? lo : (v > hi ? hi : v);
        }
    }

    /// <summary>角色台词气泡：无边框、置顶、自动消失。</summary>
    internal sealed class Live2DSayForm : Form
    {
        private readonly Label _lbl;
        private readonly Timer _timer;

        public Live2DSayForm()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            BackColor = Color.FromArgb(32, 32, 38);
            Padding = new Padding(12, 8, 12, 8);
            DoubleBuffered = true;

            _lbl = new Label();
            _lbl.Dock = DockStyle.Fill;
            _lbl.ForeColor = Color.White;
            _lbl.BackColor = Color.Transparent;
            _lbl.Font = new Font("Microsoft YaHei UI", 9.5f);
            _lbl.TextAlign = ContentAlignment.MiddleCenter;
            Controls.Add(_lbl);

            _timer = new Timer();
            _timer.Interval = 3600;
            _timer.Tick += delegate { Hide(); };
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= 0x00000080;    // WS_EX_TOOLWINDOW
                return cp;
            }
        }

        public void ShowText(string text, Point anchor)
        {
            if (string.IsNullOrEmpty(text)) return;
            _lbl.Text = text;
            int maxW = 280;
            Size sz = TextRenderer.MeasureText(text, _lbl.Font, new Size(maxW, 1200), TextFormatFlags.WordBreak);
            Size = new Size(Math.Min(maxW, Math.Max(60, sz.Width)) + 26, sz.Height + 18);
            Rectangle wa = Screen.FromPoint(anchor).WorkingArea;
            int x = anchor.X - Width / 2;
            int y = anchor.Y - Height - 6;
            if (y < wa.Top + 4) y = anchor.Y + 20;
            x = Math.Max(wa.Left + 4, Math.Min(wa.Right - Width - 4, x));
            Location = new Point(x, y);
            if (!Visible) Show();
            BringToFront();
            _timer.Stop();
            _timer.Start();
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            try { _timer.Stop(); _timer.Dispose(); } catch { }
            base.OnFormClosed(e);
        }
    }

    /// <summary>
    /// 「拎出来」的独立角色窗口：无边框、逐像素透明、可拖动、可置顶、可鼠标穿透。
    /// 用 UpdateLayeredWindow 直接合成 32bpp 位图，桌面上只看到角色本身。
    /// 互动：点击 regions 触发 actions（台词 + 弹跳/摆动），悬停触发 hover，闲置时闲聊。
    /// </summary>
    internal sealed class Live2DForm : Form
    {
        private const int WS_EX_LAYERED = 0x00080000;
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const int WM_NCHITTEST = 0x0084;
        private const int WM_LBUTTONDOWN = 0x0201, WM_LBUTTONUP = 0x0202, WM_MOUSEMOVE = 0x0200;
        private const int WM_RBUTTONUP = 0x0205;
        private const int WM_EXITSIZEMOVE = 0x0232;
        private const int HTCLIENT = 1, HTTRANSPARENT = -1;

        public readonly string ModelId;
        private readonly Live2DModelInfo _info;
        private readonly Image _img;
        private readonly Image _blink;
        private readonly MemoryStream _ms, _bms;
        private readonly Timer _timer;
        private readonly Random _rnd = new Random();
        private readonly byte[] _mask;
        private readonly int _mw, _mh;
        private readonly int _iw, _ih;
        private readonly int _frames;
        private readonly int[] _delays;
        private int _frame;
        private readonly double _start = Environment.TickCount;
        private double _nextBlink = 2500;
        private double _blinkUntil;

        // ---- 互动 ----
        private readonly Dictionary<string, int> _cooldown = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private Live2DSayForm _say;
        private double _animStart = -1, _animDur = 600, _animBounce, _animSpin, _animWobble;
        private double _nextChatter = 40;
        private string _lastHover = "";
        private bool _down, _dragging;
        private Point _downScreen, _downClient;
        private int _downTick;

        public Live2DForm(Live2DModelInfo m)
        {
            ModelId = m.Id;
            _info = m;
            _img = LoadImage(Path.Combine(m.Dir, m.File), out _ms);
            if (_img == null) throw new InvalidOperationException("模型图片读取失败");
            _iw = _img.Width;
            _ih = _img.Height;
            if (!string.IsNullOrEmpty(m.Blink))
                _blink = LoadImage(Path.Combine(m.Dir, m.Blink), out _bms);

            try
            {
                _frames = _img.GetFrameCount(FrameDimension.Time);
                _delays = new int[_frames];
                if (_frames > 1)
                {
                    PropertyItem p = _img.GetPropertyItem(0x5100);   // FrameDelay（1/100 秒）
                    for (int i = 0; i < _frames; i++)
                    {
                        int cs = BitConverter.ToInt32(p.Value, i * 4);
                        _delays[i] = (cs > 0 ? cs : 10) * 10;
                    }
                }
            }
            catch { _frames = 1; }

            _mask = BuildMask(_img, out _mw, out _mh);

            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            AutoScaleMode = AutoScaleMode.None;
            TopMost = Live2D.Topmost;
            Text = "Live2D";
            SetStyle(ControlStyles.Opaque, true);

            Size s = CalcSize();
            Size = s;
            Location = StartLocation(s);

            _timer = new Timer();
            _timer.Interval = 33;
            _timer.Tick += delegate { Render(); };
            _timer.Start();

            Render();
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= WS_EX_LAYERED | WS_EX_TOOLWINDOW;
                return cp;
            }
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        public void ApplyConfig()
        {
            TopMost = Live2D.Topmost;
            Size s = CalcSize();
            if (s != Size)
            {
                int cx = Left + Width / 2, by = Top + Height;
                Size = s;
                Location = new Point(cx - s.Width / 2, by - s.Height);
            }
            Render();
        }

        private static Image LoadImage(string path, out MemoryStream keep)
        {
            keep = null;
            try
            {
                byte[] b = File.ReadAllBytes(path);
                keep = new MemoryStream(b);
                return Image.FromStream(keep);
            }
            catch { return null; }
        }

        private Size CalcSize()
        {
            float k = Live2D.Scale / 100f;
            int w = (int)Math.Ceiling(_iw * k * 1.06f) + 8;
            int h = (int)Math.Ceiling(_ih * k * 1.10f) + 12;
            return new Size(Math.Max(24, w), Math.Max(24, h));
        }

        private Point StartLocation(Size s)
        {
            Rectangle wa = Screen.PrimaryScreen.WorkingArea;
            if (Live2D.X > -10000 && Live2D.Y > -10000 &&
                Live2D.X < wa.Right - 40 && Live2D.Y < wa.Bottom - 40)
                return new Point(Live2D.X, Live2D.Y);
            return new Point(wa.Right - s.Width - 40, wa.Bottom - s.Height - 20);
        }

        /// <summary>预存一张 alpha 蒙版，用来做「只点角色本体才算点中」的命中测试。</summary>
        private static byte[] BuildMask(Image img, out int w, out int h)
        {
            w = 0; h = 0;
            try
            {
                using (Bitmap bmp = new Bitmap(img.Width, img.Height, PixelFormat.Format32bppArgb))
                {
                    using (Graphics g = Graphics.FromImage(bmp))
                    {
                        g.Clear(Color.Transparent);
                        g.DrawImage(img, 0, 0, img.Width, img.Height);
                    }
                    w = bmp.Width; h = bmp.Height;
                    byte[] mask = new byte[w * h];
                    BitmapData bd = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                    try
                    {
                        for (int y = 0; y < h; y++)
                        {
                            IntPtr row = new IntPtr(bd.Scan0.ToInt64() + (long)y * bd.Stride);
                            byte[] line = new byte[w * 4];
                            Marshal.Copy(row, line, 0, line.Length);
                            for (int x = 0; x < w; x++) mask[y * w + x] = line[x * 4 + 3];
                        }
                    }
                    finally { bmp.UnlockBits(bd); }
                    return mask;
                }
            }
            catch { return null; }
        }

        private void SelectFrame(double elapsedMs)
        {
            if (_frames <= 1 || _delays == null) return;
            int total = 0;
            foreach (int d in _delays) total += d;
            if (total <= 0) return;
            int pos = (int)(elapsedMs % total);
            int acc = 0;
            for (int i = 0; i < _frames; i++)
            {
                acc += _delays[i];
                if (pos < acc)
                {
                    if (i != _frame)
                    {
                        try { _img.SelectActiveFrame(FrameDimension.Time, i); } catch { }
                        _frame = i;
                    }
                    return;
                }
            }
        }

        private void Render()
        {
            if (!IsHandleCreated || _img == null) return;
            try
            {
                double elapsed = Environment.TickCount - _start;
                double t = elapsed / 1000.0;
                SelectFrame(elapsed);

                float k = Live2D.Scale / 100f;
                float breath = 0f, rot = 0f, dy = 0f;
                if (Live2D.Activity)
                {
                    breath = 0.010f * (float)Math.Sin(t * 1.7);
                    rot = 0.9f * (float)Math.Sin(t * 0.55);
                    dy = 2.5f * (float)Math.Sin(t * 0.9);

                    if (_blink != null)
                    {
                        if (_blinkUntil <= 0 && elapsed >= _nextBlink)
                            _blinkUntil = elapsed + 130;
                        else if (_blinkUntil > 0 && elapsed >= _blinkUntil)
                        {
                            _blinkUntil = 0;
                            _nextBlink = elapsed + 2600 + _rnd.Next(4200);
                        }
                    }

                    if (Live2D.Follow)
                    {
                        Point c = new Point(Left + Width / 2, Top + Height);
                        Point mp = Cursor.Position;
                        float dx = (mp.X - c.X) / 420f;
                        if (dx > 1f) dx = 1f;
                        else if (dx < -1f) dx = -1f;
                        rot += dx * 2.4f;
                        float vy = (c.Y - mp.Y) / 900f;
                        if (vy > 1f) vy = 1f;
                        else if (vy < -1f) vy = -1f;
                        dy += vy * 4f;
                    }

                    // 闲置闲聊
                    if (t >= _nextChatter && _info.Chatter.Length > 0)
                    {
                        _nextChatter = t + _info.ChatterEvery;
                        Say(_info.Chatter[_rnd.Next(_info.Chatter.Length)]);
                    }
                }

                // 互动动作：弹跳 / 旋转 / 摇摆
                if (_animStart >= 0)
                {
                    double p = (elapsed - _animStart) / Math.Max(120, _animDur);
                    if (p >= 1) _animStart = -1;
                    else
                    {
                        double e = Math.Sin(Math.PI * p);
                        dy -= (float)(_animBounce * e);
                        rot += (float)(_animSpin * e);
                        rot += (float)(_animWobble * Math.Sin(p * Math.PI * 4) * (1 - p));
                    }
                }

                Size s = Size;
                using (Bitmap bmp = new Bitmap(s.Width, s.Height, PixelFormat.Format32bppArgb))
                {
                    using (Graphics g = Graphics.FromImage(bmp))
                    {
                        g.Clear(Color.Transparent);
                        g.SmoothingMode = SmoothingMode.AntiAlias;
                        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                        g.TranslateTransform(s.Width / 2f, s.Height - 4f + dy);
                        g.RotateTransform(rot);
                        g.ScaleTransform(k * (1f - breath * 0.6f), k * (1f + breath));
                        Image frame = (_blink != null && _blinkUntil > 0) ? _blink : _img;
                        g.DrawImage(frame, -_iw / 2f, -_ih, _iw, _ih);
                    }
                    BlendToScreen(bmp, Left, Top);
                }
            }
            catch { }
        }

        private void BlendToScreen(Bitmap bmp, int x, int y)
        {
            IntPtr screenDc = GetDC(IntPtr.Zero);
            IntPtr memDc = CreateCompatibleDC(screenDc);
            IntPtr hBmp = IntPtr.Zero, old = IntPtr.Zero;
            try
            {
                hBmp = bmp.GetHbitmap(Color.FromArgb(0));
                old = SelectObject(memDc, hBmp);
                POINT src = new POINT(0, 0);
                POINT dst = new POINT(x, y);
                SIZE size = new SIZE(bmp.Width, bmp.Height);
                BLENDFUNCTION blend = new BLENDFUNCTION();
                blend.BlendOp = 0;
                blend.BlendFlags = 0;
                blend.SourceConstantAlpha = (byte)(255 * Math.Max(10, Live2D.Opacity) / 100);
                blend.AlphaFormat = 1;
                UpdateLayeredWindow(Handle, screenDc, ref dst, ref size, memDc, ref src, 0, ref blend, 2);
            }
            finally
            {
                if (hBmp != IntPtr.Zero)
                {
                    SelectObject(memDc, old);
                    DeleteObject(hBmp);
                }
                DeleteDC(memDc);
                ReleaseDC(IntPtr.Zero, screenDc);
            }
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_NCHITTEST)
            {
                if (Live2D.ClickThrough) { m.Result = (IntPtr)HTTRANSPARENT; return; }
                int lp = m.LParam.ToInt32();
                Point p = PointToClient(new Point((short)(lp & 0xFFFF), (short)((lp >> 16) & 0xFFFF)));
                m.Result = (IntPtr)(HitTestAlpha(p) ? HTCLIENT : HTTRANSPARENT);
                return;
            }
            if (m.Msg == WM_LBUTTONDOWN)
            {
                Point p = PointToClient(Cursor.Position);
                _down = true; _dragging = false;
                _downTick = Environment.TickCount;
                _downScreen = Cursor.Position;
                _downClient = p;
                try { SetCapture(Handle); } catch { }
                return;
            }
            if (m.Msg == WM_MOUSEMOVE)
            {
                if (_down)
                {
                    Point sp = Cursor.Position;
                    if (!_dragging &&
                        (Math.Abs(sp.X - _downScreen.X) + Math.Abs(sp.Y - _downScreen.Y) > 4))
                        _dragging = true;
                    if (_dragging)
                        Location = new Point(sp.X - _downClient.X, sp.Y - _downClient.Y);
                    return;
                }
                if (Live2D.ClickThrough) return;
                Point p = PointToClient(Cursor.Position);
                Hover(p);
                return;
            }
            if (m.Msg == WM_LBUTTONUP)
            {
                if (!_down) return;
                _down = false;
                try { ReleaseCapture(); } catch { }
                if (_dragging)
                {
                    Live2D.SavePos(Left, Top);
                }
                else if (Environment.TickCount - _downTick < 600)
                {
                    RunClick(PointToClient(Cursor.Position));
                }
                return;
            }
            if (m.Msg == WM_RBUTTONUP)
            {
                ShowMenu();
                return;
            }
            if (m.Msg == WM_EXITSIZEMOVE)
            {
                Live2D.SavePos(Left, Top);
                base.WndProc(ref m);
                return;
            }
            base.WndProc(ref m);
        }

        /// <summary>按预存 alpha 蒙版判断点是否落在角色本体上（忽略旋转误差，足够准）。</summary>
        private bool HitTestAlpha(Point p)
        {
            if (_mask == null || _mw <= 0 || _mh <= 0) return true;
            float k = Live2D.Scale / 100f;
            float lx = (p.X - Width / 2f) / k + _iw / 2f;
            float ly = (p.Y - (Height - 4f)) / k + _ih;
            int x = (int)lx, y = (int)ly;
            if (x < 0 || y < 0 || x >= _mw || y >= _mh) return false;
            return _mask[y * _mw + x] > 16;
        }

        // ============================================================ 互动
        private void RunClick(Point p)
        {
            string[] ids = RegionActions(p);
            if (ids.Length == 0) ids = _info.ClickDefault;
            RunActions(ids);
        }

        private string[] RegionActions(Point p)
        {
            if (_info.Regions.Count == 0 || _iw <= 0 || _ih <= 0) return new string[0];
            float k = Live2D.Scale / 100f;
            double nx = ((p.X - Width / 2f) / k + _iw / 2f) / _iw;
            double ny = ((p.Y - (Height - 4f)) / k + _ih) / _ih;
            List<string> ids = new List<string>();
            foreach (Live2DRegion r in _info.Regions)
            {
                if (nx >= r.X && nx <= r.X + r.W && ny >= r.Y && ny <= r.Y + r.H)
                    ids.AddRange(r.Click);
            }
            return ids.ToArray();
        }

        private void Hover(Point p)
        {
            if (_info.Regions.Count == 0) return;
            float k = Live2D.Scale / 100f;
            double nx = ((p.X - Width / 2f) / k + _iw / 2f) / _iw;
            double ny = ((p.Y - (Height - 4f)) / k + _ih) / _ih;
            foreach (Live2DRegion r in _info.Regions)
            {
                if (r.Hover.Length == 0) continue;
                if (nx >= r.X && nx <= r.X + r.W && ny >= r.Y && ny <= r.Y + r.H)
                {
                    string key = r.X + "," + r.Y;
                    if (key == _lastHover) return;
                    _lastHover = key;
                    RunActions(r.Hover);
                    return;
                }
            }
            _lastHover = "";
        }

        private void RunActions(string[] ids)
        {
            if (ids == null) return;
            int now = Environment.TickCount;
            foreach (string raw in ids)
            {
                string id = (raw ?? "").Trim();
                if (id.Length == 0) continue;
                Live2DAction a;
                if (!_info.Actions.TryGetValue(id, out a)) continue;
                int last;
                if (_cooldown.TryGetValue(id, out last) && now - last < a.Cooldown) continue;
                _cooldown[id] = now;

                if (a.Say.Length > 0)
                    Say(a.Say[_rnd.Next(a.Say.Length)]);

                if (a.Bounce != 0 || a.Spin != 0 || a.Wobble != 0)
                {
                    _animBounce = a.Bounce;
                    _animSpin = a.Spin;
                    _animWobble = a.Wobble;
                    _animDur = Math.Max(120, a.Duration);
                    _animStart = Environment.TickCount - _start;
                }
            }
        }

        private void Say(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            try
            {
                if (_say == null || _say.IsDisposed) _say = new Live2DSayForm();
                _say.ShowText(text, new Point(Left + Width / 2, Top));
            }
            catch { }
        }

        private void ShowMenu()
        {
            ContextMenuStrip menu = new ContextMenuStrip();
            menu.Items.Add("打个招呼", null, delegate { RunActions(new[] { "greet" }); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("隐藏角色", null, delegate
            {
                Live2D.Enabled = false;
                Live2D.Save();
                Live2D.Sync();
                Live2D.Notify();
            });
            ToolStripMenuItem through = new ToolStripMenuItem("鼠标穿透");
            through.Checked = Live2D.ClickThrough;
            through.Click += delegate
            {
                Live2D.ClickThrough = !Live2D.ClickThrough;
                Live2D.Save();
                Live2D.Notify();
            };
            menu.Items.Add(through);
            ToolStripMenuItem top = new ToolStripMenuItem("始终置顶");
            top.Checked = Live2D.Topmost;
            top.Click += delegate
            {
                Live2D.Topmost = !Live2D.Topmost;
                TopMost = Live2D.Topmost;
                Live2D.Save();
                Live2D.Notify();
            };
            menu.Items.Add(top);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("打开 Live2D 设置…", null, delegate
            {
                try { if (Live2D.OpenSettings != null) Live2D.OpenSettings(); } catch { }
            });
            menu.Show(Cursor.Position);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            try { _timer.Stop(); _timer.Dispose(); } catch { }
            try { _img.Dispose(); } catch { }
            try { if (_blink != null) _blink.Dispose(); } catch { }
            try { if (_ms != null) _ms.Dispose(); } catch { }
            try { if (_bms != null) _bms.Dispose(); } catch { }
            try { if (_say != null) { _say.Close(); _say.Dispose(); } } catch { }
            base.OnFormClosed(e);
        }

        // ============================================================ Win32
        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int X, Y; public POINT(int x, int y) { X = x; Y = y; } }

        [StructLayout(LayoutKind.Sequential)]
        private struct SIZE { public int CX, CY; public SIZE(int w, int h) { CX = w; CY = h; } }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        private struct BLENDFUNCTION
        {
            public byte BlendOp;
            public byte BlendFlags;
            public byte SourceConstantAlpha;
            public byte AlphaFormat;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref POINT pptDst,
            ref SIZE psize, IntPtr hdcSrc, ref POINT pptSrc, int crKey, ref BLENDFUNCTION pblend, int dwFlags);

        [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
        [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hDC);
        [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);
        [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr h);
        [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr hObject);
        [DllImport("user32.dll")] private static extern bool ReleaseCapture();
        [DllImport("user32.dll")] private static extern IntPtr SetCapture(IntPtr hWnd);
    }
}
