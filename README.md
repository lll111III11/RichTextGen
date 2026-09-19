# 彩色文本生成器 · Rich text & multifunctional tool

给 SCP:SL（Unity 6 / TextMeshPro）生成富文本的桌面工具。

## 功能

- **颜色系统**：三种模式（`#十六进制` / `RGB 十进制` / `英文色名`），默认白色，选色后生成时用 `<color=…>` 括住文本
- **标签系统**：Unity/TMP 全标签（样式、大小写、颜色、大小与字体、字距偏移、对齐位置、换行、超链接、资源类、旧版兼容），按注册表驱动，规范嵌套
- **渐变**：逐字符渐变 / 循环渐变（轴对称渐变规划中）
- **预览**：黑底实时预览（浏览器渲染，与游戏内观感一致）
- **发送到游戏**：进程锁定、剪贴板/键入两种方式、发送记录
- **在线能力**：在线取色（jsdelivr color-name 148 色 → color.pizza → 内置 140 色回退）、网络状态监测
- **UI**：Win11/Fluent 风格（DWM 圆角窗口、Segoe UI Variable、系统图标字体、高 DPI 清晰渲染）、亮/深主题、缩放 70%~200%、逐部件自定义配色、背景图（对称平铺 / 自适配）

## 安装

下载 Releases 里的 `RichTextGen-Setup.exe`，双击按向导安装（可自定义安装路径、自动创建桌面快捷方式）。
便携版：直接运行 `RichTextGen.exe`（需与 `RichTextGen.exe.config` 同目录，DPI 配置在里面）。

## 自动更新

程序启动后读取本仓库的更新清单（优先 jsdelivr，回退 raw）：

```
https://cdn.jsdelivr.net/gh/lll111III11/RichTextGen@main/version.json
https://raw.githubusercontent.com/lll111III11/RichTextGen/main/version.json
```

- 本地版本 < 清单 `version` → 状态栏提示并亮起「更新」按钮
- 点击后从 GitHub Releases 下载 `installer`（或 `portable`）并自动安装/替换
- 清单里 `mandatory: true` 时强制更新

## 构建

```
dotnet build RichTextGen.csproj -c Release
```

产物：`bin/Release/net48/RichTextGen.exe`（.NET Framework 4.8，Win10/11 自带运行时）。

## 版本记录

- **5.2.0.0** 颜色系统默认白色并默认生效；Win11 风格 UI；高 DPI；预览黑底；分组筛选分页；修复启动空引用崩溃
