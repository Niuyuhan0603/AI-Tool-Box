# AI-Tool-Box 开发状态与待办（供后续 AI 直接执行）

> 最后更新：2026-10-25
> 本文档用于交接：**已完成 / 部分完成 / 未开始** 的功能状态、关键文件定位、实现方案与项目约束。
> 任何后续 AI 接手时请先读完本文档，再动代码。
> 2026-10-25 更新：M1–M5 里程碑已全部补齐（新增 11 个功能页），VPN 节点改为读取 `Assets/VPN-config.json`。

---

## 0. 项目速览

| 项 | 值 |
|---|---|
| 技术栈 | WinUI 3 + Microsoft.WindowsAppSDK 1.6.250108002 + .NET 8（x64） |
| 项目文件 | `AI-Tool-Box/src/AI-Tool-Box/AI-Tool-Box.csproj` |
| 私有 SDK | `.dotnet/`（仓库根，必须用它构建） |
| 主窗口 | `AI-Tool-Box/src/AI-Tool-Box/MainWindow.xaml(.cs)`：左侧 `NavigationView` + 单 `ContentFrame` |
| 侧栏路由 | `MainWindow.OnNavSelectionChanged`（按 `NavigationViewItem.Tag` 分发到各 Page） |
| 权限 | `app.manifest` = **asInvoker**（普通权限，勿改回 requireAdministrator，否则 UWP Picker 全失效） |
| WebView2 | 1.0.2651.64；**所有 WebView2 必须用 `WebShell.GetSharedEnvironmentAsync()`**，否则用户数据目录冲突报 NRE |
| 设置持久化 | `%LOCALAPPDATA%\AI-Tool-Box\`（app_settings.json / ai_assist_settings.json / music_favorites.json 等） |
| 日志 | Serilog，路径可在设置页修改 |

### 构建 / 发布命令（必须使用）

```powershell
# 编译（Release，会显示真实错误）
powershell -ExecutionPolicy Bypass -File "e:\ITCH\Project\Intergrated AI Codebot\build.ps1" -Configuration Release -Clean

# 发布（默认散装，冷启动快）→ bin\x64\Release\net8.0-windows10.0.19041.0\win-x64\publish\
powershell -ExecutionPolicy Bypass -File "e:\ITCH\Project\Intergrated AI Codebot\publish.ps1"

# 发布单文件 exe（约 200+MB，冷启动较慢）
powershell -ExecutionPolicy Bypass -File "e:\ITCH\Project\Intergrated AI Codebot\publish.ps1" -SingleFile
```

### 关键项目约定（务必遵守）

1. **验证标准**：`build.ps1` 输出 `0 个警告 0 个错误`。`dotnet build` 的 XamlCompiler 崩溃通常掩盖真实错误 → **必须用 build.ps1 看错误**（或读 `build.log`）。
2. **XAML 陷阱**（net472 XamlCompiler）：
   - `SymbolIcon` 不支持部分枚举值（如 `Chat`）→ 改用 `<FontIcon Glyph="&#xE8BD;"/>`
   - `ToggleButton` **没有 `GroupName`**（那是 RadioButton）→ 代码手动互斥
   - `TextBlock` **没有 `Background` / `CornerRadius`**（WinUI3）→ 用 `Border` 包裹
   - `StackPanel` **没有 `Wrap`** → 用 `ScrollViewer` 或 `ItemsWrapGrid`
3. **WebView2 离线页加载方式**：`Source = new Uri(AppContext.BaseDirectory + "Assets/Web/xxx.html")`（file://，unpackaged 应用无 ms-appx）
4. **UWP Picker（FileOpenPicker/FolderPicker）在 asInvoker 下正常**；若将来要提权，需换 Win32 IFileDialog。
5. **页面切换**：同类型页面 `Frame.Navigate` 会静默跳过 → `NavigationService` 已处理（同类型先置 `Content=null` 再导航）。
6. **WebShell 挂载**：仅 `BrowserPage` / `SnifferPage` 可 `AttachWebShellTo`；导航到其它页面前必须 `HideWebShellForNavigation()`（已内置于 MainWindow 路由）。
7. **沙箱限制**：当前开发终端内**不能运行 app**（写 `%LOCALAPPDATA%` 会被拦截）。运行时验证需用户手动执行。
8. 资源目录：`AI-Tool-Box/src/AI-Tool-Box/Assets/`（`Web/**` 与会 `tessdata/**` 通过 csproj 的 `<Content Include="Assets\Web\**\*">` 复制到输出）。

---

## 1. 功能状态总表（34 项）

图例：✅ 已完成 ｜ 🟡 部分完成（有基础，缺子功能）｜ ❌ 未开始

| # | 功能 | 状态 | 关键实现 / 备注 |
|---|---|---|---|
| 1 | 网址导航 | ✅ | `Views/BrowserPage.*` + `WebShell.*`（多标签/地址栏/书签/历史/下载/拖动成独立窗口/新窗口请求转内部标签） |
| 2 | 媒体嗅探 + 智能多媒体下载（含图片） | 🟡 | `Services/SnifferService.cs`（IDM 嗅探 JS 注入 `Assets/Web/sniffer/`）+ `Views/SnifferPage.*` + m3u8 分片解密下载。**待办**：图片资源独立分类/批量下载 UI、下载队列管理可视化 |
| 3 | 右侧栏 AI 辅助 | ✅ | `Views/AiAssistPanel.*` + `ViewModels/AiAssistViewModel.cs` + `Services/AiAssistChatService.cs`（OpenAI/Anthropic/Ollama 多档案，Key 用 DPAPI） |
| 4 | AI 页面内答题 / 自动填充 | ✅ | `Views/AIPage.*`（正文抓取 + 注入）+ `WebShell.GetContentAsync/InjectToActiveElementAsync` |
| 5 | 本地高精度 OCR（常用语言） | ✅ | `Services/TesseractOcrService.cs`（11 语言：简中/繁中/英/法/俄/西/阿/德/日/韩/葡）+ `Views/OcrPage.*`；`Assets/tessdata` 已裁剪 |
| 6 | 同局域网传文件（LocalSend/Zapya 互通） | 🟡 | LocalSend v2 协议已实现（UDP 224.0.0.167:53317 发现 + HTTP API；`Services/LanTransferService.cs`、`MiniHttpServer.cs`、`Views/FileTransferPage.*`）。**待办**：Zapya 私有协议互通（需逆向/抓包，风险高） |
| 7 | 分享题目答案 / 题库管理 | ✅ | `Views/AnswerSharePage.*` + `Services/AnswerShareService.cs`（UDP 9529 发现 + TCP 9530 共享，本地 JSON 题库，收藏/一键插入） |
| 8 | 局域网 AI 调用 | ✅ | `Views/LanAIPage.*`（同局域网互调 AI，服务端 + 客户端） |
| 9 | 常用办公文件格式转换 | ✅ | `Views/TransformPage.*` + `Services/TransformService.cs`（Word↔PDF / 图片→PDF / PDF→PNG / MP4 / 直链下载；依赖**外部 ffmpeg**，路径可自定义） |
| 10 | 多线程下载器 + m3u8 解密合并 | ✅ | `SnifferService`（分片并发、AES-128 CBC/PKCS7、Master Playlist 选最高 BANDWIDTH、key URI 支持 http/file） |
| 11 | 内置 VPN | ✅ | `Views/VpnPage.*` + `Services/VpnService.cs` + `Services/VpnNodeProbe.cs` + `Services/VpnGhAccountClient.cs` + `Services/V2rayCoreService.cs`。**GreenHub 节点 = VMess(WebSocket+TLS)**：`POST https://{domain}/d/v1/account`（HMAC-SHA256 签名，uid/cur-version 取自 `Assets/VPN-config.json`）→ 生成 v2ray 配置（本地 HTTP 入站 10810+）→ 启动 `Assets/v2ray/v2ray.exe` → 系统代理指向本地端口。圆点：青=可连接/白=不可达/灰=未扫描。启动时清理残留本地代理、退出时自动断开 |
| 12 | EPUB 阅读器 | ✅ | `Views/EpubReaderPage.*`（`EpubReaderPage.xaml.cs` 在项目根，非 Views 下）+ `Assets/Web/epub.html`（epub.js + JSZip）。**注意**：`Assets/Web/lib/jszip.min.js` 必须存在 |
| 13 | PDF 阅读 / 批注 / 编辑 | ✅ | `Views/PdfViewerPage.*`（**C# 原生**：`Windows.Data.Pdf` 渲染 + 2× 超采样；文字/画笔批注叠加；裁剪；合并；加密 PDF 密码弹窗；Ctrl+滚轮缩放；导出经隐藏页 `Assets/Web/pdf.html` 的 pdf-lib `exportAnnotated`） |
| 14 | 极域电子教室控制 + UDP 指令 | ✅ | `Views/JiYuTrainerPage.*` + `Services/JiYuCoreService.cs`（破解密码/Kill/Restart/解除控屏/UDP 攻击）；背景代码雨复用 welcome.html?rain=1 |
| 15 | 音乐 / 视频播放器（含在线） | ✅ | 音乐：`Views/MusicPlayerPage.*`（歌词 LRC 滚动、内嵌曲绘解析、收藏、三种模式、响应式右栏）；视频：`Views/VideoPlayerPage.*`（`MediaPlayerElement`，本地文件 + 在线 mp4/m3u8 直链） |
| 16 | ncm 网易云解码 | ✅ | `Services/NcmConverter.cs`（AES-128-ECB + RC4 还原，输出 mp3/flac + ID3v2.3 标签/封面）；入口在 `TransformPage`（TransformService L417） |
| 17 | Edge 扩展引入测试 | ✅ | `Views/EdgeExtensionTestPage.*` + `Services/EdgeExtensionService.cs`（口令 `114514` 解锁；独立 WebView2 用户目录 + `AreBrowserExtensionsEnabled`） |
| 18 | 本地 Ollama LLM 一键运行 + 调用 | ✅ | 调用：`Services/OllamaService.cs`；运行时：`Services/OllamaRuntimeService.cs`（检测 exe → `ollama serve` → `ollama pull`/`rm`）+ `Views/OllamaPage.*`（状态/模型管理/拉取进度） |
| 19 | 科学计算器 + 函数/方程绘图（AI 辅助） | ✅ | `Services/ExpressionEvaluator.cs`（递归下降：+ - * / ^ % !、常量、20+ 函数、变量 x/y、隐式乘法；`NormalizeMathInput` 支持 LaTeX `\\frac/\\sqrt[n]/^{}/\\pi` 与 Unicode；**新增数值微积分** `Derivative/SecondDerivative`（5 点差分）、`Integrate`（自适应 Simpson）、`SampleDerivative`）+ `Views/CalculatorPage.*`（数学键盘模板、历史、**白色**坐标系 + 网格/刻度 + **始终存在**的可拖动红色取点与坐标气泡、导函数虚线叠加、求导/二阶导/偏导 ∂f/∂x·∂f/∂y/梯度/全微分/定积分/变上限积分、**绘图区可缩放/平移**：滚轮绕光标缩放 + 右键拖动平移 + 手动范围保持） |
| 20 | 分子模拟器（有机/无机、晶体、SMILES/IUPAC、ChemDraw 级导出） | 🟡 | `Views/MoleculePage.*` + `Assets/Web/molecule/`（**离线** SmilesDrawer 2.4.1：统一 `new SmilesDrawer.Drawer(opts)` + `draw(tree, canvas, 'dark')` 修复 “Second argument was not a canvas” 报错；SMILES→2D、自写分子式统计、SVG/PNG 导出；「原子拼装」画布支持鼠标拖动/成键/删键 + 键盘 A/B/E、方向键移动、1/2/3 键级、Delete、Enter；实时分子式·分子量·不饱和度·环数·官能团；**「完成」按钮**一键生成当前分子结构图 + HBD/HBA/可旋转键等参数 + 开链同分异构体缩略图（≤7 重原子，WL 哈希去重，简化力场排序，#1 默认最低能量）；**化学反应预测**（`Services/ReactionPredictService.cs`：输入反应物/产物 SMILES（`+` 分隔）→ 自动配平检查 + ΔH/ΔS/ΔG/K 平衡常数/Arrhenius 速率常数/半衰期/速率等级，已知物种用标准生成焓熵表、未知物种按键能与原子贡献近似，页面入口在左栏「化学反应预测」面板）。**待办**：晶体结构、3D 构象 |
| 21 | 音视频 AI 智能总结 | ✅ | `Services/MediaSummaryService.cs`（ffmpeg 抽 16k 单声道 → 分段总结）+ `Services/AiQuickService.cs`（Whisper `/audio/transcriptions` + AI 总结）+ `Views/MediaSummaryPage.*`（支持导入 .srt/.txt 替代转写） |
| 22 | 多媒体文件格式转换 | ✅ | `TransformService.ConvertMediaAsync`（14 种目标格式编码参数）+ `TransformViewModel` 的 `MediaConvert` 模式 + 页面第 5 个模式选项。**未做**：批量队列、码率/分辨率细调 UI |
| 23 | Markdown → 思维导图（AI 辅助，导出 PDF/SVG/HTML/MD） | ✅ | `Views/MindMapPage.*` + `Assets/Web/mindmap/index.html`（**离线**自研树布局：滚轮缩放/拖拽/导出 SVG/PNG/HTML/MD）+ AI 生成大纲（PDF 可经 HTML 编辑器导出） |
| 24 | HTML 编辑器（字号/段落/效果，AI 补写，导出 docx/html/pdf/svg/pptx） | ✅ | `Views/HtmlEditorPage.*` + `Assets/Web/editor/index.html`（contenteditable + 工具栏 + AI 改写/续写/润色）+ `Services/OfficeExportService.cs`（DOCX/PPTX/SVG；PDF 用 `PrintToPdfAsync`） |
| 25 | 物理题模拟器（可搭模型，AI 辅助） | 🟡 | `Views/PhysicsSimPage.*` + `Services/CircuitSolver.cs`（自研 2D：圆-线段碰撞/弹性/摩擦/轨迹/弹簧（k·L₀·阻尼可编辑，锚点与小球可拖动），预设自由落体·抛体·斜面，场景 JSON + AI 生成；**电路模式**：电池·电阻·电容·电感·开关·电流表·电压表，元件可拖动摆放、参数可编辑、端子连线，MNA 直流求解 + RC/RL 后向欧拉暂态；**画布缩放**：右上角 －/100%/＋ 按钮 + 鼠标滚轮，0.5×–4×，渲染/命中测试/拖动边界/网格统一按 `ViewScale` 比例）。**待办**：刚体旋转、二极管非线性、绳索约束 |
| 26 | 生物 / 遗传模拟器（AI 辅助） | 🟡 | `Views/GeneticsPage.*`（单/双基因杂交比例、表现型统计、哈代-温伯格及反推、AI 解释与出题）。**未做**：系谱图绘制、生态/细胞动画 |
| 27 | 计划表格 / 定时器 / 定制桌宠（AI 接入） | ❌ | 见 §2.8 |
| 28 | Canvas 笔记（保存格式） | ❌ | 见 §2.9 |
| 29 | 错题本（本地）+ AI 出题 | ✅ | `Services/WrongBookService.cs`（`%LOCALAPPDATA%\AI-Tool-Box\wrongbook.json`）+ `Views/WrongBookPage.*`（检索/科目筛选/标签/掌握标记/复习计数/统计行/AI 出题与讲解/**图片 OCR 录入**（自动选 chi_sim→eng）/**多格式文件导入**（PDF 经 `Windows.Data.Pdf` 渲染为 PNG 后 OCR、DOCX 解析 `word/document.xml`、图片 OCR、txt/md 直接读入，可多选）/**AI 拆分题干·答案·解析**/**JSON 导入导出**） |
| 30 | 开机动画 + 收费模式 + 设置 + 用量可视化 + API 安全 + SessionID | 🟡 | 已完成：`Controls/SplashOverlay.*`（H+ Logo 淡入→六边形→青色苯环 bond-line）、`Views/SettingsPage.*`（常规/线程/FFmpeg/AI Key/**厂商预填充一键添加**/**已保存 API 列表（厂商·协议·基地址·模型·Key 掩码）+ 连通性测试**：添加时自动 `GET /models`（Anthropic 走 `/v1/models` + `x-api-key`）校验，20 秒超时，区分鉴权失败/超时/HTTP 错误）/内测/日志/数据管理）、`Services/UsageTracker.cs`（Token+VPN 流量可视化）、`Services/ApiKeyService.cs`（DPAPI 加密，不落明文）、**SessionID**（`AiAssistProfile.SessionId`：OpenAI `user` / Anthropic `metadata.user_id` / 请求头 `X-Session-Id`）。**待办**：收费/订阅模式 |
| 31 | Autoscript（Lua 语法 + API + 混淆加密字节码） | ✅ | `Services/AutoscriptService.cs`（MoonSharp 硬沙箱 + `toolbox` API：log/sleep/ai/http_get/file_*/navigate/now；ATS1 = AES-256-CBC 加密脚本）+ `Views/AutoscriptPage.*`（运行/示例/打开/保存/加密保存/输出） |
| 32 | 接入主流通讯软件（自动收发消息） | 🟡 | `Services/BotBridgeService.cs`（Telegram 长轮询自动收发 + AI 回复；企业微信/钉钉/飞书/Slack Webhook 推送）+ `Views/MessagingPage.*`。**未做**：QQ/个人微信（无官方 API） |
| 33 | 接入工程/3D 建模软件（AI 自动化） | 🟡 | `Services/SoftwareAutomationService.cs`（脚本写临时文件 + 命令行驱动）+ `Views/SoftwareAutomationPage.*`（Blender/FreeCAD/AutoCAD/Python/自定义，AI 生成脚本）。**未做**：SolidWorks 等 COM 直连 |
| 34 | AI 小说创作（工作区前情 + 流式续写） | ✅ | `Views/NovelPage.*`（工作区文件夹内 .md/.txt 按章节名排序作为前情，字符预算截取最近部分；自定义“前情提要”；`IApiAiService.GenerateStreamAsync` SSE 流式实时写入正文、可停止；章节载入/保存；配置持久化 `%LOCALAPPDATA%\AI-Tool-Box\novel_settings.json`）。侧栏入口 Tag=`Novel` |

---

## 2. 未开始 / 待完善功能：实现方案

> 说明：§2.1–§2.7、§2.10–§2.13 对应功能已在 2026-10-25 完成（实现见 §1 表格中的关键文件）。
> 本节仅保留仍未开始的 **#27 计划表格/定时器/桌宠**、**#28 Canvas 笔记** 方案，以及各功能"待办"里的增量项（见 §1 备注）。

### 2.1 科学计算器 + 函数/方程绘图（#19）
- 新建 `Views/CalculatorPage.*` + `Services/ExpressionParser.cs`（或引入 `AngouriMath` / `NCalc` NuGet 做符号/数值计算）
- 绘图：`Microsoft.Graphics.Canvas`（Win2D NuGet）或 `Path`/`Polyline` 自绘坐标系；支持 y=f(x)、隐函数、参数方程
- AI 辅助：输入自然语言 → 调 `IAiAssistChatService` 生成表达式（复用现有 AI 服务）
- 侧栏注册：`MainWindow.xaml` 加 `NavigationViewItem Tag="Calculator"`，`OnNavSelectionChanged` 加 case

### 2.2 分子模拟器（#20）
- **契约**：SMILES / IUPAC / 标准名称（in/out）、2D 结构编辑、晶体结构显示、导出 PDF/HTML
- 建议：C# 端用 `RDKit` 的 .NET 绑定（`RDKit.DotNet`，需原生 dll，体积大）或 `CDK`（Java，不适合）；**更实际**：WebView2 + `JSME`/`Ketcher`/`SmilesDrawer`/`3Dmol.js` 做结构与 3D 渲染，C# 负责文件与 AI
- 名称↔结构互转需 OPSIN（IUPAC→SMILES，Java）→ 可改用在线 API 或本地 `opsin-cli`；**需与用户确认可接受的外部依赖**
- 页面：`Views/MoleculePage.*` + `Assets/Web/molecule/index.html`

### 2.3 音视频 AI 智能总结（#21）
- 流程：ffmpeg 抽音频 → 本地 Whisper（`whisper.cpp` 可执行 / `Whisper.net` NuGet）转写 → 现有 AI 服务总结
- TTS 可选：`Windows.Media.SpeechSynthesis`
- 页面：`Views/MediaSummaryPage.*`；转写模型文件体积大，需外部下载到 `%LOCALAPPDATA%`

### 2.4 Markdown → 思维导图（#23）
- 解析 md 标题/列表 → 树 → 渲染（WebView2 + `markmap`/`mermaid`，或 WinUI `TreeView`+自绘）
- 导出：SVG/HTML（前端直接导出）、PDF（`Assets/Web/pdf.html` 的 img2pdf/pdf-lib 或 WebView2 Print）、MD
- AI 辅助：生成/润色 md（复用 AI 服务）

### 2.5 HTML 编辑器（#24）
- WebView2 + `contenteditable` + 自写工具栏（字号/段落/颜色/列表/表格）
- AI：补写/修格式/加效果（选中片段 → AI → 替换 innerHTML）
- 导出：html 直出；pdf（WebView2 Print / pdf-lib）；docx/pptx 建议用 `DocumentFormat.OpenXml` NuGet 生成（或 HTML→docx 用 `HtmlToOpenXml`）

### 2.6 物理题模拟器（#25）
- 物理引擎：`Box2D.Net` / `Jitter2`（刚体）或 WebView2 + `matter.js`（**推荐**，生态好、可视化强）
- 场景搭建：拖拽添加物体/斜面/弹簧，参数面板（质量/摩擦/重力），实时轨迹
- AI 辅助：自然语言 → 场景 JSON（约定 schema，AI 生成后加载）

### 2.7 生物 / 遗传模拟器（#26）
- 遗传：孟德尔/哈代-温伯格计算器 + 系谱图绘制（血统图用 `Canvas`/SVG）
- 生态/细胞：WebView2 + `p5.js`/`d3.js` 动画
- AI：生成参数/解释结果

### 2.8 计划表格 / 定时器 / 桌宠（#27）
- 计划表格：`DataGrid`（CommunityToolkit）或自绘 Grid，本地 JSON 持久化
- 定时器：`DispatcherTimer` + 通知（`AppNotificationManager`，需 Windows App SDK 通知权限）
- 桌宠：**独立无边框透明窗口**（`AppWindow` + `WS_EX_TRANSPARENT|LAYERED`）+ 帧动画（GIF/APNG 或 Win2D）

### 2.9 Canvas 笔记（#28）
- `InkCanvas` + `InkToolbar`（WinUI 3 支持）+ 笔迹保存（`InkStrokeContainer.SaveAsync` → `.isf`/`.gif`）
- 导出 PNG/SVG（自绘）/PDF；多页管理

### 2.10 错题本（#29）
- 本地 JSON/SQLite（`Microsoft.Data.Sqlite`）存题目（文本+图片/PDF 附件）
- AI 出题：把题干/知识点喂给 AI 生成同类型新题（复用 AI 服务）
- 页面：`Views/WrongBookPage.*`（列表/筛选/标签/复习计划）

### 2.11 Autoscript（#31）
- Lua 运行时：`MoonSharp` NuGet（纯托管，支持沙箱）
- API 暴露：把各功能封装为 `IToolboxApi`（如 `toolbox.navigate("Sniffer")`、`toolbox.ai.chat(...)`、`toolbox.download(url)`）
- 混淆/字节码：MoonSharp 支持 `LoadString` + 编译缓存；「混淆加密字节码」需自定义打包格式（AES 加密脚本 + 内置解密加载）
- 安全：限制文件/网络/进程调用白名单，禁止任意 .NET 反射

### 2.12 通讯软件接入（#32）
- 优先级建议：**Telegram Bot API**（最简单）→ 企业微信/钉钉机器人（Webhook）→ QQ（需第三方框架如 NapCat，非官方，风险高）
- 架构：`Services/BotBridgeService` 订阅消息 → 转成内部指令 → 调用各功能 Api → 回复
- **注意**：微信个人号无官方 API，需说明风险或不做

### 2.13 工程/3D 建模软件自动化（#33）
- 通用方案：**COM/脚本接口**——AutoCAD（AutoLISP/COM）、SolidWorks（COM）、Blender（Python 脚本 + `bpy`，通过 `blender --background --python`）、FreeCAD（Python）
- 实现：C# 调外部进程执行脚本；AI 生成脚本内容
- 需用户明确目标软件清单（不同软件差异极大）

---

## 3. 建议优先级（里程碑）

| 里程碑 | 内容 | 状态 | 新增入口（侧栏 Tag） |
|---|---|---|---|
| M1（补齐短板） | #15 视频播放器、#22 音视频格式转换、#18 Ollama 一键运行 | ✅ 全部完成 | `VideoPlayer` / `Convert`（第 5 模式）/ `Ollama` |
| M2（AI 增强） | #21 音视频总结、#23 思维导图、#24 HTML 编辑器 | ✅ 全部完成 | `MediaSummary` / `MindMap` / `HtmlEditor` |
| M3（教学工具） | #19 计算器、#25 物理、#26 生物遗传、#29 错题本 | ✅ 全部完成（物理/遗传为基础版） | `Calculator` / `Physics` / `Genetics` / `WrongBook` |
| M4（重量级） | #20 分子模拟器、#31 Autoscript | ✅ 主体完成（分子缺晶体/3D） | `Molecule` / `Autoscript` |
| M5（外部集成） | #32 通讯软件、#33 工程软件自动化 | ✅ 主体完成（仅官方接口/命令行） | `Messaging` / `Automation` |

> 共通 AI 能力：`Services/AiQuickService.cs`（`IAiQuickService`）直接复用「设置 → 右侧栏AI辅助」的提供商档案与 DPAPI 密钥，新增 AI 功能请注入它，不要重复写 HTTP 调用。

---

## 4. 已知坑与修复记录（避免重复踩坑）

| 问题 | 现象 | 修复 |
|---|---|---|
| `Shell_NotifyIconW` DllImport 错误 | 托盘图标不显示，`EntryPointNotFoundException` | 库名必须是 **shell32.dll**（不是 user32.dll） |
| UWP Picker 在提权进程失效 | 点"添加文件夹"无反应、返回 null | manifest 保持 **asInvoker**；提权场景改用 Win32 IFileDialog |
| WebView2 无参 Initialize | NRE "Object reference not set" | 一律 `EnsureCoreWebView2Async(await WebShell.GetSharedEnvironmentAsync())` |
| file:// 下 pdf.js Worker 挂起 | JS 页面永远不回调 | 已改 C# 原生 `Windows.Data.Pdf`（PDF 查看不再用 JS） |
| `Windows.Data.Pdf` 加密 PDF | 打开无响应 | `LoadFromFileAsync(file)` 失败 → 密码弹窗 → `LoadFromFileAsync(file, password)` |
| `PdfDocument` 无法 Dispose | 编译 CS1061 / CS8121 | 不要调 Dispose，交给 GC |
| MusicPlayerPage 构造期 NRE | 导航失败回退到网址导航 | XAML 中 `ComboBox` 默认 `SelectedIndex` 会提前触发 `SelectionChanged` → 事件里先判空 |
| 导航失败回退逻辑 | 自动跳回"网址导航" | 回退时 `RootNav.SelectedItem = null`（勿设第一个菜单项） |
| 同类型页面导航被跳过 | 网址导航 ↔ AI辅助 切换无效 | `NavigationService` 同类型先 `Content=null` |
| Assets 修改后未复制到输出 | 改了 html/js 但运行时仍旧版 | `PreserveNewest` 依赖 mtime，必要时手动 Copy 到 `bin/.../Assets/` |
| XamlCompiler 静默崩溃（exit 1 无错误） | `dotnet build` 报 MSB3073 | 用 **build.ps1** 构建看真实 WMC/CS 错误 |
| 插值原始字符串里的 CSS 大括号 | `$"""...body{...}..."""` 报 CS8076 | CSS/JSON 片段用普通字符串常量拼接，或在插值原始字符串里写 `{{ }}` |
| `CoreWebView2.PrintToPdfAsync(path)` | CS7036 缺参数 | 本版本必须传 `CoreWebView2PrintSettings`：`Environment.CreatePrintSettings()` |
| `WebShell` 找不到（CS0103） | 页面在 `AI_Tool_Box.Views` 下 | `WebShell` 位于 `AI_Tool_Box.Controls`，需 `using AI_Tool_Box.Controls;` |
| `Color` 未定义/二义 | 自绘 Canvas 用 `Color.FromArgb` | 加别名 `using Color = Windows.UI.Color;`（不要 `using Microsoft.UI;`） |
| `x:Bind` 绑定到字符串项 | DataTemplate 内绑定报错 | 用 `{Binding}`（无 `x:DataType`）或显式 `x:DataType="x:String"`+`{Binding}` |
| VPN 扫描"全部可用"但连接 400 | TCP 443 通 ≠ 可用 | GreenHub 节点是 VMess(WS+TLS)：TCP 443 只是 Cloudflare 前置（CONNECT 返回 400）。VMess 节点必须用账号 API 校验/连接（`VpnService.TestNodeAsync`），只有标准代理节点才用 `VpnNodeProbe` |
| `AppWindow.MoveInZOrderAtTop()` | 窗口长期置顶，遮挡桌面/其它窗口点击 | 恢复窗口只用 `Show()+Activate()`，并用 `SetWindowPos(HWND_NOTOPMOST)` 清除置顶 |
| 残留系统代理 | 上次异常退出后 ProxyEnable=1 指向 127.0.0.1:95xx → 全网请求卡住 | 启动时 `VpnService.CleanupStaleSystemProxy()`；`Dispose` 时自动断开（含 108xx 的 v2ray 入站） |
| v2ray 配置写入带 BOM | 核心 panic：`invalid character 'ï'` | 配置必须用 `new UTF8Encoding(false)` 写入（无 BOM） |
| GreenHub 账号接口方法 | GET 返回 405 | 必须 **POST** `/d/v1/account`；`sign=Base64(HMAC-SHA256(key="<已脱敏>", "timestamp=..&uid=..&version=.."))`，query 还需 `platform=win` |
| v2ray 语法差异 | 4.x 用 `-c config.json`，5.x/xray 用 `run -c` | `V2rayCoreService` 先试 `-c`，端口未就绪则回退 `run -c` |
| SmilesDrawer 把 SVG 传给 `Drawer.draw` | 控制台报 “Second argument was not a canvas or the ID of a canvas.” | SVG 必须用 `new SmilesDrawer.SvgDrawer(opts)` + `draw(tree, svgElement, 'dark', false)`；`SmilesDrawer.Drawer` 只接受 canvas |
| 分子「SMILES 解析失败」后页面空白 | WebView2 里结构不显示 | 确认 `SmilesDrawer.parse` 成功回调里再建 `SvgDrawer`，并显式设置 svg 的 width/height；主题参数用 `'dark'` 与深色容器一致 |

---

## 5. 新增功能的标准流程（后续 AI 照做）

1. `Views/XxxPage.xaml` + `XxxPage.xaml.cs`（`Grid` 根，`{ThemeResource ApplicationPageBackgroundThemeBrush}` 背景，深色科技风配色 `#0A0F14 / #141B24 / #00E5FF`）
2. 业务逻辑放 `Services/XxxService.cs`，通过 `Services/ServiceCollectionExtensions.cs` 注册 DI
3. `MainWindow.xaml` 加 `NavigationViewItem Tag="Xxx"`（图标用 `FontIcon Glyph`，避免 SymbolIcon 枚举坑）
4. `MainWindow.OnNavSelectionChanged` 的 `default` 分支加 `"Xxx" => NavigateWithLog<XxxPage>("...")`
5. 若页面需要网页内容：`Assets/Web/xxx/` + WebView2（共享环境）
6. 运行 `build.ps1 -Configuration Release -Clean` 直到 **0 警告 0 错误**
7. 需要用户手动运行验证（沙箱不可运行）

---

## 6. 外部依赖清单（当前）

| 依赖 | 用途 | 状态 |
|---|---|---|
| ffmpeg.exe | 音视频转换 / 抽帧 | **不内置**，用户自备（路径可自定义；设置页/转换页指定） |
| tessdata | OCR 语言包 | 已内置 11 种常用语言 |
| WebView2 Runtime | 网页渲染 | 系统依赖（Win10/11 一般已装） |
| Ollama（外部） | 本地 LLM | 用户自行安装；`OllamaRuntimeService` 可一键 `serve`/`pull`，路径可指定（`%LOCALAPPDATA%\AI-Tool-Box\ollama_path.txt`） |
| OpenAI 兼容 API-Key | 语音转写 / AI 总结 / 各页 AI 辅助 | 在「设置 → 右侧栏AI辅助」配置；转写需支持 `/audio/transcriptions` 的服务（OpenAI/兼容网关），否则请导入 .srt/.txt |
| MoonSharp 2.0.0 | Autoscript Lua 运行时 | NuGet 随包分发（纯托管） |
| SmilesDrawer 2.4.1 | 分子 2D 结构渲染 | 已内置 `Assets/Web/molecule/smiles-drawer.min.js`（离线） |
| Telegram Bot Token | 通讯软件接入 | 用户自建 @BotFather Bot；DPAPI 保存为 `telegram` |
| 外部工程软件 | 自动化 | Blender / FreeCAD / AutoCAD / Python 等需本机安装，路径在页面指定 |
| VPN 节点配置 | VPN | 已改为 `Assets/VPN-config.json`（GreenHub 2.4.0 配置，含 uid/cur-version/nodeList；可整体替换） |
| v2ray 核心 | VPN（VMess WS+TLS） | 已内置 `Assets/v2ray/v2ray.exe`（V2Ray 4.42.2，MIT，见同目录 NOTICE.txt）；也可在 VPN 页指定其他 v2ray/xray 路径（存 `%LOCALAPPDATA%\AI-Tool-Box\v2ray_path.txt`） |
