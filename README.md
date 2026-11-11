# AI-Tool-Box

一款集 **网址导航 · AI 辅助 · 本地工具** 于一体的 Windows 桌面工具箱，基于 **WinUI 3 + .NET 8** 构建，全部功能本地运行、离线可用。

> 当前版本：**1.0.0** ｜ 运行环境：Windows 10 / 11（x64）

---

## 功能一览

| 分类 | 功能 |
|---|---|
| 网页 | 网址导航（多标签 / 地址栏 / 书签 / 历史 / 下载）、网页内链接自动新标签打开 |
| AI | 右侧栏 AI 辅助（OpenAI / Anthropic / Ollama）、AI 页面内答题、局域网 AI 服务 |
| 工具 | 本地 OCR（Tesseract，11 种常用语言）、PDF 查看器（翻页 / 缩放 / 批注 / 裁剪 / 合并）、EPUB 阅读器 |
| 下载 | 视频 / 音频嗅探器（IDM 同款算法）、M3U8 下载与解密合并、文件转换（Word ↔ PDF / 音视频 / 直链下载） |
| 网络 | VPN（内置免费节点）、同局域网传文件（LocalSend 协议）、题库共享 |
| 娱乐 | 音乐播放器（歌词 / 曲绘 / 收藏 / 三种播放模式）、极域教室控制 |
| 系统 | 缩小至托盘、Edge 扩展引入测试、内测功能 |

更完整的功能清单与实现状态见 [DEVELOPMENT_STATUS.md](DEVELOPMENT_STATUS.md)。

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

> 注意：请使用 `build.ps1` 而非 `dotnet build`，前者可显示 XAML 编译器的真实错误。

---

## 使用说明

- 首次使用请到「设置」页配置需要的 **API Key**（AI 功能）与 **FFmpeg 路径**（文件转换）。
- 关闭主窗口默认 **缩小至托盘**（后台任务不中断），可在「设置 → 常规」修改为直接退出。

---

## 技术栈

- **UI**：WinUI 3 + Microsoft.WindowsAppSDK 1.6
- **运行时**：.NET 8（可自包含发布，无需安装运行时）
- **网页渲染 / PDF / EPUB / 嗅探**：WebView2 1.0.2651.64
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
├─ build.ps1 / publish.ps1     # 构建与发布脚本
└─ check-env.ps1               # 开发环境依赖自检
```

---

## 赞助支持

如果这款工具箱对你有帮助，欢迎扫码赞助，支持持续更新（微信 / 支付宝均可）。

> 赞助二维码见应用内「关于软件」页。

---

## 许可

本项目仅供学习与个人使用。内置的第三方组件（v2ray、Tesseract、PDF.js、pdf-lib、epub.js 等）遵循其各自的开源许可。
