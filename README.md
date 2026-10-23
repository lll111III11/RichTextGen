# 彩色文本生成器（RichTextGen）

Windows 桌面端的彩色文本工具：多标签编辑、富文本上色、快捷组件全局热键、Live2D 角色模型，另有 Android 精简版。

- 官网下载页：<https://richtextgen-dl.pages.dev/>
- 安装统计：<https://richtextgen-dl.pages.dev/stats>
- 网页工作区（试验）：<https://richtextgen-dl.pages.dev/studio>
- GitHub 发布页：<https://github.com/lll111III11/RichTextGen/releases>

## 下载

| 版本 | 说明 |
| --- | --- |
| `RichTextGen-Setup.exe` | Windows 安装版（推荐），安装成功后自动计入安装量 |
| `RichTextGen-Portable.zip` | Windows 免安装版，解压即用，不写注册表 |
| `RichTextGen.apk` | Android 版（1.1.0） |

两个 Windows 包都可以从下载页或 GitHub Releases 获取，内容完全一致，均可对照发布页上的 SHA-256 校验。

## 更新机制

程序内置双通道更新：

1. 检查更新时同时探测 **Cloudflare 下载站** 与 **GitHub**，哪条通道响应快就用哪条；
2. 手动点「立即检测更新」时，默认直接走 Cloudflare；
3. 任一通道下载安装包失败，会自动改用另一条通道。

## 下载或安装时出现安全提示怎么办

安装包使用项目自有的代码签名证书签名，**不是**受信任 CA 签发的商业证书，因此：

- 浏览器（Chrome / Edge）可能提示「不常见的下载内容」或「可能有害」。点击下载项的「保留」/「仍要下载」即可继续；
- Windows SmartScreen 可能提示「Windows 已保护你的电脑」。点「更多信息」→「仍要运行」；
- 提示的原因只是**证书未受信任**，并不代表文件被篡改。

如果不放心，可以：

1. 在下载页展开「SHA-256 校验值」，用 `certutil -hashfile 文件名 SHA256` 比对；
2. 改从 [GitHub Releases](https://github.com/lll111III11/RichTextGen/releases) 下载同一文件，两条来源交叉核对；
3. 需要源码级确认时，本仓库包含全部客户端源码，可自行构建比对。

## 目录结构

```
工作区/
├─ WebUi/                Windows 客户端（C# WinForms + WebView2）
│  ├─ src/               宿主逻辑：文件、AI、Live2D、更新、热键
│  └─ ui/                HTML 界面
├─ android/              Android 版（Java + WebView）
├─ installer/            原生 C++ 安装器与彻底卸载工具
├─ download-site/        静态下载站（Cloudflare Pages + Functions + KV）
├─ signing/              代码签名证书（本地保留，不入库）
└─ version.json          更新清单
```

## 构建（Windows）

```bat
rem 完整构建：程序 -> 签名 -> 刷载荷 -> 安装器 -> 彻底卸载 -> 签名
installer\build.bat
```

需要 .NET SDK、MSVC（VS2019 及以上）与 Windows SDK 的 `signtool`。缺少 `signing/rtg-codesign.pfx` 时会跳过签名，仍可构建出未签名版本。

## 许可

本项目采用 [MIT License](LICENSE)。

## 反馈

问题与建议请提到 <https://github.com/lll111III11/RichTextGen/issues>。
