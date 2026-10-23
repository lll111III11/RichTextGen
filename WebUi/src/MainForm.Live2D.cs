using System;
using System.Drawing;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

namespace RichTextGen
{
    /// <summary>Live2D / 3D 角色：运行时下载 + AI 生成互动配置。</summary>
    public partial class MainForm
    {
        // ============================================================ 3D 运行时（three.js）
        /// <summary>按需下载 three.js + GLTFLoader 到 %APPDATA%\RichTextGen\live2d\_runtime。</summary>
        private void StartLive2dRuntimeDownload()
        {
            if (Live2D.ThreeReady)
            {
                CallStr("status", "3D 组件已就绪，可直接载入 .glb / .vrm 模型");
                CallStr("live2dState", Live2D.StateJson());
                return;
            }
            CallStr("status", "正在下载 3D 组件（three.js，约 700KB）…");
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    Directory.CreateDirectory(Live2D.RuntimeDir);
                    string three = DownloadFirst(new[]
                    {
                        "https://unpkg.com/three@0.128.0/build/three.min.js",
                        "https://cdnjs.cloudflare.com/ajax/libs/three.js/r128/three.min.js",
                        "https://cdn.jsdelivr.net/npm/three@0.128.0/build/three.min.js"
                    }, 180000);
                    if (three.Length < 10000)
                    {
                        CallStr("status", "3D 组件下载失败：three.js 内容不完整，请检查网络后重试");
                        return;
                    }
                    string loader = DownloadFirst(new[]
                    {
                        "https://unpkg.com/three@0.128.0/examples/js/loaders/GLTFLoader.js",
                        "https://cdn.jsdelivr.net/npm/three@0.128.0/examples/js/loaders/GLTFLoader.js"
                    }, 120000);
                    if (loader.Length < 2000)
                    {
                        CallStr("status", "3D 组件下载失败：GLTFLoader 内容不完整，请检查网络后重试");
                        return;
                    }
                    File.WriteAllText(Path.Combine(Live2D.RuntimeDir, "three.min.js"), three, new UTF8Encoding(false));
                    File.WriteAllText(Path.Combine(Live2D.RuntimeDir, "GLTFLoader.js"), loader, new UTF8Encoding(false));
                    CallStr("status", "3D 组件已安装，可以载入 .glb / .vrm 模型了");
                    CallStr("live2dState", Live2D.StateJson());
                }
                catch (Exception ex)
                {
                    CallStr("status", "3D 组件安装失败：" + ex.Message);
                }
            });
        }

        // ============================================================ Cubism（.moc3）运行时
        /// <summary>按需下载 Cubism Core + PIXI + pixi-live2d-display 到 _runtime。</summary>
        private void StartCubismRuntimeDownload()
        {
            if (Live2D.CubismReady)
            {
                CallStr("status", "Cubism 组件已就绪，可直接导入 .model3.json / .moc3 模型");
                CallStr("live2dState", Live2D.StateJson());
                return;
            }
            CallStr("status", "正在下载 Cubism 组件（Core + PIXI，约 1.5MB）…");
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    Directory.CreateDirectory(Live2D.RuntimeDir);

                    string core = DownloadFirst(new[]
                    {
                        "https://cubism.live2d.com/sdk-web/cubismcore/live2dcubismcore.min.js"
                    }, 120000);
                    if (core.Length < 50000)
                    {
                        CallStr("status", "Cubism Core 下载失败（需要访问 live2d.com），请检查网络后重试");
                        return;
                    }

                    string pixi = DownloadFirst(new[]
                    {
                        "https://unpkg.com/pixi.js@6.5.10/dist/browser/pixi.min.js",
                        "https://cdn.jsdelivr.net/npm/pixi.js@6.5.10/dist/browser/pixi.min.js"
                    }, 180000);
                    if (pixi.Length < 100000)
                    {
                        CallStr("status", "PIXI 下载失败，请检查网络后重试");
                        return;
                    }

                    string cubism = DownloadFirst(new[]
                    {
                        "https://unpkg.com/pixi-live2d-display@0.4.0/dist/cubism4.min.js",
                        "https://cdn.jsdelivr.net/npm/pixi-live2d-display@0.4.0/dist/cubism4.min.js"
                    }, 120000);
                    if (cubism.Length < 50000)
                    {
                        CallStr("status", "pixi-live2d-display 下载失败，请检查网络后重试");
                        return;
                    }

                    File.WriteAllText(Path.Combine(Live2D.RuntimeDir, "live2dcubismcore.min.js"), core, new UTF8Encoding(false));
                    File.WriteAllText(Path.Combine(Live2D.RuntimeDir, "pixi.min.js"), pixi, new UTF8Encoding(false));
                    File.WriteAllText(Path.Combine(Live2D.RuntimeDir, "cubism4.min.js"), cubism, new UTF8Encoding(false));
                    CallStr("status", "Cubism 组件已安装，可以导入 .moc3 模型了");
                    CallStr("live2dState", Live2D.StateJson());
                }
                catch (Exception ex)
                {
                    CallStr("status", "Cubism 组件安装失败：" + ex.Message);
                }
            });
        }

        private static string DownloadFirst(string[] urls, int ms)
        {
            foreach (string u in urls)
            {
                string s = Http(u, ms);
                if (!string.IsNullOrEmpty(s)) return s;
            }
            return "";
        }

        // ============================================================ AI 角色工坊
        /// <summary>选图片或 3D 模型 → 导入成模型包 → 让 AI 生成 regions / actions / idle。</summary>
        private void Live2dAiWorkshop()
        {
            string src = "";
            using (OpenFileDialog d = new OpenFileDialog())
            {
                d.Filter = "角色图片或 3D 模型|*.png;*.gif;*.jpg;*.jpeg;*.bmp;*.glb;*.vrm|所有文件|*.*";
                d.Title = "选择角色图片或 3D 模型（AI 会生成互动配置）";
                if (d.ShowDialog(this) != DialogResult.OK) return;
                src = d.FileName;
            }

            string id = Live2D.ImportFile(src, false);
            if (id.Length == 0) { CallStr("status", "导入失败：文件无法读取"); return; }
            string dir = Path.Combine(Live2D.Root, id);
            Live2DModelInfo m = Live2D.ReadModel(dir);
            if (m == null) { CallStr("status", "模型读取失败"); return; }

            // 用抠图后的成品给视觉模型看，保证 regions 的坐标和实际显示图一致
            string visionSrc = src;
            try
            {
                string cut = Path.Combine(dir, "model.png");
                if (m.IsSprite && File.Exists(cut)) visionSrc = cut;
            }
            catch { }

            CallStr("live2dModels", Live2D.ModelsJson());
            CallStr("live2dState", Live2D.StateJson());
            CallStr("status", "AI 正在为「" + m.Name + "」生成互动配置…（不影响继续使用）" +
                (Live2D.LastImportNote.Length > 0 ? " · " + Live2D.LastImportNote : ""));

            ThreadPool.QueueUserWorkItem(delegate
            {
                string note;
                try { note = GenerateConfigWithAi(m, visionSrc); }
                catch (Exception ex) { note = "AI 生成失败：" + ex.Message + "；已改用默认配置"; }
                try
                {
                    if (!File.Exists(m.ConfigPath)) Live2D.WriteDefaultConfig(m, true);
                }
                catch { }

                try
                {
                    BeginInvoke((Action)delegate
                    {
                        Live2D.Enabled = true;
                        Live2D.Save();
                        CallStr("live2dModels", Live2D.ModelsJson());
                        CallStr("live2dState", Live2D.Apply(Live2D.StateJson()));
                        CallStr("status", note);
                    });
                }
                catch { }
            });
        }

        private static string GenerateConfigWithAi(Live2DModelInfo m, string src)
        {
            if (string.IsNullOrEmpty((AiConfig.Key ?? "").Trim()))
                return "未配置 API Key：已写入默认互动配置，可在「设置 → AI 接入」填好 Key 后再点一次 AI 生成";

            string schema =
                "{\"name\":\"角色名\",\"regions\":[{\"x\":0.3,\"y\":0.03,\"w\":0.4,\"h\":0.3,\"click\":[\"pat\"],\"hover\":[\"notice\"]}]," +
                "\"actions\":{\"pat\":{\"say\":[\"台词1\",\"台词2\"],\"anim\":{\"bounce\":8,\"wobble\":4},\"cooldown\":1200}}," +
                "\"idle\":{\"chatter\":[\"闲聊1\"],\"every\":150}}";

            string kind = m.IsSprite ? "2D 立绘" : "3D 模型";
            string prompt =
                "你是「彩色文本生成器」的角色互动配置生成器。请为下面的" + kind + "生成一份 model.json 互动配置。\n" +
                "只输出 JSON 本体，不要解释、不要 Markdown 代码块。字段必须符合这个结构：\n" + schema + "\n" +
                "要求：\n" +
                "1) regions 是 0~1 的相对矩形，按人物各部位实际比例给出 2~4 块（头、身体、手等），click 填动作 id。\n" +
                "2) actions 给 4~6 个，每个 2~3 句中文台词；语气自然、不低俗、不重复；anim.bounce 0~20（像素），spin / wobble 0~10（度）。\n" +
                "3) 角色性格根据" + (m.IsSprite ? "图片里的人物外观与画风" : "模型文件名") + "来猜。\n" +
                "4) idle.chatter 给 3~5 句，every 取 60~240 秒。\n" +
                "5) 不要输出结构以外的字段，不要注释。";

            string images = "";
            if (m.IsSprite)
            {
                try
                {
                    FileInfo fi = new FileInfo(src);
                    if (fi.Length <= 8 * 1024 * 1024 && ModelSupportsVision(AiConfig.Provider, AiConfig.Model))
                    {
                        byte[] b = File.ReadAllBytes(src);
                        images = "[\"data:" + MimeOf(src) + ";base64," + Convert.ToBase64String(b) + "\"]";
                    }
                }
                catch { }
            }

            string body = "{\"model\":" + Quote(AiConfig.Model) + ",\"messages\":[" +
                "{\"role\":\"system\",\"content\":" + Quote("你只输出严格合法的 JSON，不要任何额外文字。") + "}," +
                "{\"role\":\"user\",\"content\":" + BuildUserContent(prompt, images) + "}]," +
                "\"max_tokens\":2000,\"temperature\":0.8}";

            string err;
            string resp = HttpPostEx(NormalizeEndpoint(AiConfig.Endpoint), (AiConfig.Key ?? "").Trim(), body, 120000, out err);

            // 接口可能不认图片（模型名判断不准）→ 去掉图片重试一次
            if (resp.Length == 0 && images.Length > 0)
            {
                body = "{\"model\":" + Quote(AiConfig.Model) + ",\"messages\":[" +
                    "{\"role\":\"system\",\"content\":" + Quote("你只输出严格合法的 JSON，不要任何额外文字。") + "}," +
                    "{\"role\":\"user\",\"content\":" + BuildUserContent(prompt, "") + "}]," +
                    "\"max_tokens\":2000,\"temperature\":0.8}";
                resp = HttpPostEx(NormalizeEndpoint(AiConfig.Endpoint), (AiConfig.Key ?? "").Trim(), body, 120000, out err);
            }

            string content = ExtractContent(resp);
            string json = ExtractJsonObject(content);
            if (json.Length == 0)
                return "AI 没有返回可用的 JSON" + (err.Length > 0 ? "：" + err : "") + "；已改用默认配置";
            if (Live2D.ParseObj(json) == null)
                return "AI 返回的 JSON 无法解析；已改用默认配置";

            File.WriteAllText(m.ConfigPath, json, new UTF8Encoding(false));
            return "AI 已完成角色配置（" + m.Name + "）：点击角色试试互动效果";
        }

        /// <summary>从模型回复里抠出第一个完整 JSON 对象（去掉代码块和前后解释）。</summary>
        private static string ExtractJsonObject(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            int a = s.IndexOf('{');
            int b = s.LastIndexOf('}');
            if (a < 0 || b <= a) return "";
            string json = s.Substring(a, b - a + 1);
            return Live2D.ParseObj(json) != null ? json : "";
        }
    }
}
