# AI-Tool-Box

一款集 **网址导航 · AI 辅助 · 本地工具** 于一体的 Windows 桌面工具箱，基于 **WinUI 3 + .NET 8** 构建。

**34 项功能全部内置**：从浏览器、AI 助手、OCR、PDF/EPUB 阅读，到视频嗅探下载、VPN、局域网传文件、音乐播放器，开箱即用，无需安装一堆零散软件。

> 当前版本：**1.0.0** ｜ 运行环境：Windows 10 / 11（x64）

> **下载发布版**：[Releases](https://github.com/Niuyuhan0603/AI-Tool-Box/releases) 页面提供的发行文件**全部是自解压文件**（`AI-Tool-Box-<版本>-Setup.exe`）。双击它会弹出解压对话框，选择目录后自动解出完整程序——**没有安装向导、不做注册表写入、不装依赖**（已内置 .NET 8 运行时）。解压完成后运行目录下的 `AI-Tool-Box.exe` 即可（首次启动会请求管理员权限）。不需要时直接删掉整个解压目录即可卸载。

---

## 它不是一个「网页套壳」

这是需要先说清楚的一点：**AI-Tool-Box 的原生界面、业务逻辑、文件处理全部由 C# / WinUI 3 实现**，不是把网页塞进一个窗口的外壳应用。

| 能力 | 实现方式 |
|---|---|
| 主界面、导航、侧栏、对话框、动画 | **原生 WinUI 3 / XAML**，跟随系统主题与 DPI |
| 各功能页的交互逻辑 | **原生 C#**（MVVM） |
| PDF 渲染与批注 | **原生 C#**（`Windows.Data.Pdf` + 2× 超采样），批注层为 XAML `Canvas` |
| 文件读写、OCR、下载、VPN、局域网传输 | **原生 .NET**（Tesseract、HttpClient、Socket、进程管理） |
| API Key 存储 | **原生 DPAPI 加密** |

**那 WebView2 用在哪？** 只用在「交给浏览器内核反而更快、更稳」的具体环节，属于技术选型而非主架构：

- **网页导航**——它本身就要显示真实网页，必须用内核渲染；
- **PDF / EPUB 渲染**——复用 PDF.js / epub.js 成熟的排版与字体处理，避免自研排版引擎；而 PDF 的**页面渲染与批注走原生**，只有导出、文字提取这类重活交给 pdf-lib / PDF.js；
- **HTML 编辑器、思维导图、分子结构图**——这类内容本身就是 HTML/Canvas/SVG 表达，直接用内核渲染比自绘更快，也更贴近所见即所得；
- **网页嗅探**——需要在内核网络层拦截真实的媒体请求。

一句话：**原生负责「逻辑与界面」，WebView2 只负责「渲染那些本来就是网页的东西」**。因此启动速度、内存占用、系统集成度都接近于纯原生应用，而不是 Electron 式的整体套壳。

---

## 安全：你的 API Key 不会以明文存在

这是很多人关心的问题，这里明确说明：

- **AI API Key 使用 Windows DPAPI（`DataProtectionScope.CurrentUser`）加密后落盘**，磁盘上只有密文；即使把配置文件拷到别人电脑上也无法解密。
- 解密后的 Key **只存在于进程内存**，仅在发起请求时使用，不会被写入日志。
- 配置文件保存在 `%LOCALAPPDATA%\AI-Tool-Box\`（当前用户目录），**不在程序安装目录内**，升级/重装不丢失，也不会随发布包分发。
- 源码仓库中**不含任何真实密钥**：默认配置为占位符，需你在「设置」页自行填写。
- 本项目的公开代码中不包含开发者本人的账号、密码、VPN 凭据等私密信息。

> 建议：为 AI 平台的 Key 设置用量上限；共用电脑时请使用自己的 Windows 账户登录，DPAPI 的加密密钥与当前用户绑定。

---

## 功能一览

| 分类 | 功能 |
|---|---|
| 网页 | 网址导航（多标签 / 地址栏 / 书签 / 历史 / 下载）、网页内链接自动新标签打开、拖动标签成独立窗口 |
| AI | 右侧栏 AI 辅助（OpenAI / Anthropic / Ollama 多档案）、AI 页面内答题与自动填充、局域网 AI 服务、AI 小说创作、AI 音视频总结 |
| 阅读 | PDF 查看器（翻页 / 缩放 / 文字与画笔批注 / 裁剪 / 合并 / 加密解锁）、EPUB 阅读器、Markdown → 思维导图 |
| 办公 | 本地 OCR（Tesseract，11 种语言）、Word ↔ PDF、图片 → PDF、PDF → PNG、音视频格式转换、HTML 编辑器（导出 docx / pptx / pdf） |
| 下载 | 视频 / 音频嗅探（IDM 同款算法）、M3U8 下载与 AES-128 解密合并、多线程下载 |
| 网络 | VPN（内置节点 / v2ray 内核）、同局域网传文件（LocalSend 协议，可与手机互通）、题库共享 |
| 学习 | 错题本（多格式导入 / 框选录入 / AI 出题讲解）、科学计算器与函数绘图、物理模拟、分子模拟、遗传模拟 |
| 娱乐 | 音乐播放器（歌词 / 曲绘 / 收藏 / 三种播放模式）、视频播放器、极域教室控制 |
| 自动化 | Autoscript（Lua 语法 + 沙箱脚本引擎）、通讯软件机器人、Blender / FreeCAD / AutoCAD 脚本驱动 |
| 系统 | 缩小至托盘、开机动画、用量统计、设置中心、日志、数据管理 |

完整的模块清单与关键实现见文末「目录结构」与各功能页面源码。

---

## 快速开始

### 环境要求

- Windows 10 2004（Build 19041）或更高版本
- Visual Studio 2022（含 MSIX Packaging Tools / Windows App SDK 组件）
- Windows SDK `10.0.19041.0`
- WebView2 Runtime

可运行环境自检脚本确认依赖是否齐全：

```powershell
powershell -ExecutionPolicy Bypass -File .\check-env.ps1
```

### 构建

项目自带私有 .NET SDK（`.dotnet/`），请使用构建脚本编译：

```powershell
# 编译（Release）
powershell -ExecutionPolicy Bypass -File .\build.ps1 -Configuration Release -Clean

# 发布（散装，冷启动快）
powershell -ExecutionPolicy Bypass -File .\publish.ps1

# 发布单文件 exe
powershell -ExecutionPolicy Bypass -File .\publish.ps1 -SingleFile
```

### 打包发布

`release.ps1` 会发布 → 剔除本机私有文件 → 用 Bandizip 生成压缩包与自解压安装程序：

```powershell
powershell -ExecutionPolicy Bypass -File .\release.ps1
```

- 输出到 `Publish/`：`AI-Tool-Box-<版本>.zip` 与 `AI-Tool-Box-<版本>-Setup.exe`（**Release 发布用的就是这个自解压 exe**；只要它时加 `-NoZip`）
- 压缩级别默认 **9（最大压缩率）**，可用 `-CompressLevel 0..9` 调整
- **自动剔除**：`logs/`、`*.WebView2/`（浏览器缓存与 Cookie）、`*.pdb`、`*.log`、运行期用户配置
- **默认剔除** `Assets/VPN-config.json`（含本机 VPN 凭据）；确需随包分发时加 `-IncludeVpnConfig`

> 注意：请使用 `build.ps1` 而非 `dotnet build`，前者可显示 XAML 编译器的真实错误。

---

## 使用说明

- 首次使用请到「设置」页配置需要的 **API Key**（AI 功能）与 **FFmpeg 路径**（文件转换）。
- 关闭主窗口默认 **缩小至托盘**（后台任务不中断），可在「设置 → 常规」修改为直接退出。

---

## 技术栈

- **UI**：WinUI 3 + Microsoft.WindowsAppSDK 1.6（原生 XAML，非网页套壳）
- **运行时**：.NET 8（可自包含发布，无需安装运行时）
- **渲染内核（按需使用）**：WebView2 1.0.2651.64
- **本地离线库**：Tesseract OCR · PDF.js · pdf-lib · epub.js · JSZip · SmilesDrawer

---

## 目录结构

```
AI-Tool-Box/
├─ AI-Tool-Box.sln
├─ src/AI-Tool-Box/            # 主工程
│  ├─ Views/                   # 各功能页面
│  ├─ Services/                # 业务服务（OCR / 嗅探 / VPN / 转换 …）
│  ├─ ViewModels/              # MVVM
│  └─ Assets/                  # Web 离线页、tessdata、v2ray 等资源
├─ LiteParse2.0/               # OCR 语言训练数据
├─ build.ps1                   # 编译
├─ publish.ps1                 # 发布（散装 / 单文件）
├─ release.ps1                 # 打包发布（Zip + 自解压 exe）
└─ check-env.ps1               # 开发环境依赖自检
```

---

## 赞助支持

如果这款工具箱对你有帮助，欢迎扫码赞助，支持持续更新（微信 / 支付宝均可）。

> 赞助二维码见应用内「关于软件」页。

---

## 许可

本项目仅供学习与个人使用。内置的第三方组件（v2ray、Tesseract、PDF.js、pdf-lib、epub.js 等）遵循其各自的开源许可。

