using AI_Tool_Box.Controls;
using AI_Tool_Box.Services;
using AI_Tool_Box.Views;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Serilog;

namespace AI_Tool_Box;

/// <summary>主窗口：侧栏导航 + 单 Frame 承载各功能页；WebShell 常驻 HiddenWebHost，按需由 BrowserPage/SnifferPage 插拔可视层。</summary>
public sealed partial class MainWindow : Window,
    CommunityToolkit.Mvvm.Messaging.IRecipient<ViewModels.NavigateToTagMessage>,
    CommunityToolkit.Mvvm.Messaging.IRecipient<ViewModels.TesterUnlockedMessage>
{
    private readonly ILogger _logger;
    private readonly INavigationService _navigation;
    private readonly IWebShellService _webShell;
    private string? _lastSelectedTag;
    /// <summary>WebShell 当前挂载的可视槽（null = 已归还 HiddenWebHost）。</summary>
    private Border? _currentWebShellSlot;
    /// <summary>系统托盘服务（缩小至托盘，关闭拦截 + 托盘菜单）。</summary>
    private TrayIconService? _tray;

    /// <summary>初始化主窗口。</summary>
    public MainWindow()
    {
        InitializeComponent();
        Title = "AI-Tool-Box";
        ExtendsContentIntoTitleBar = false;

        _logger = App.Services.GetService(typeof(ILogger)) as ILogger
                  ?? Serilog.Log.Logger;
        _navigation = App.Services.GetRequiredService<INavigationService>();
        _webShell = App.Services.GetRequiredService<IWebShellService>();

        // 绑定单个 WebShell 实例到服务
        if (_webShell is WebShellService wss)
        {
            wss.Attach(WebShellLayer);
        }

        _navigation.Initialize(ContentFrame);

        // 默认：欢迎页
        _navigation.Navigate<WelcomePage>();
        _lastSelectedTag = null;

        // 题库分享：勾选"启动软件时自动开启共享"则随主窗口启动（UDP 9529 + TCP 9530）
        try
        {
            var answerShare = App.Services.GetService<IAnswerShareService>();
            if (answerShare is { Settings.AutoStartSharing: true } && answerShare.StartSharing())
            {
                _logger.Information("题库共享已随启动自动开启（TCP {Port}）", answerShare.LocalTcpPort);
            }
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "题库共享自动开启失败");
        }

        // 调试口：主界面任意位置连续键入 114514 解锁"直接添加 Edge 扩展"
        if (Content is Microsoft.UI.Xaml.Controls.Grid rootGrid)
        {
            rootGrid.KeyDown += OnDebugKeyDown;
        }

        // 跨层导航消息 + 内测解锁消息：本窗口实现两个 IRecipient<T> 接收。
        // 注意：实现多个 IRecipient<T> 时泛型推断失效，必须经 IMessenger 接口显式指定 TMessage 注册
        //（WeakReferenceMessenger 具体类型的实例方法均为带 token 的三泛型重载）。
        CommunityToolkit.Mvvm.Messaging.IMessenger bus = WeakReferenceMessenger.Default;
        bus.Register<ViewModels.NavigateToTagMessage>(this);
        bus.Register<ViewModels.TesterUnlockedMessage>(this);

        _logger.Information("主窗口已加载，Frame + HiddenWebHost 架构初始化完成");

        // 缩小至托盘：关闭窗口按设置拦截（默认缩小至托盘，进程与后台任务不停止）；托盘菜单"退出"才真正结束进程
        try
        {
            if (App.Services.GetService(typeof(IAppSettingsService)) is IAppSettingsService appSettings)
            {
                _tray = new TrayIconService(_logger, appSettings, this);
                _tray.Initialize();
            }
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "托盘服务启动失败");
        }

        // 应用退出时终止内嵌外部程序（JiYuTrainer / MusicPlayer2），防止窗口随父销毁后进程滞留
        this.Closed += (_, _) =>
        {
            Views.JiYuTrainerPage.ShutdownForAppExit();
            Views.MusicPlayerPage.ShutdownForAppExit();
        };

        // "Edge扩展引入测试"侧栏项：仅在内测人员已解锁（设置页输入口令 114514）时可见
        try
        {
            var edgeSvc = App.Services.GetRequiredService<AI_Tool_Box.Services.EdgeExtensionService>();
            EdgeExtensionTestItem.Visibility = edgeSvc.IsTesterUnlocked
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "初始化 Edge扩展引入测试侧栏项可见性失败");
        }

        _ = PlaySplashAsync();
    }

    // ==================== 调试口（输入 114514 解锁） ====================

    private string _debugBuffer = string.Empty;
    private bool _debugUnlocked;

    private static string? DebugDigitOf(Windows.System.VirtualKey k) => k switch
    {
        Windows.System.VirtualKey.Number0 or Windows.System.VirtualKey.NumberPad0 => "0",
        Windows.System.VirtualKey.Number1 or Windows.System.VirtualKey.NumberPad1 => "1",
        Windows.System.VirtualKey.Number2 or Windows.System.VirtualKey.NumberPad2 => "2",
        Windows.System.VirtualKey.Number3 or Windows.System.VirtualKey.NumberPad3 => "3",
        Windows.System.VirtualKey.Number4 or Windows.System.VirtualKey.NumberPad4 => "4",
        Windows.System.VirtualKey.Number5 or Windows.System.VirtualKey.NumberPad5 => "5",
        Windows.System.VirtualKey.Number6 or Windows.System.VirtualKey.NumberPad6 => "6",
        Windows.System.VirtualKey.Number7 or Windows.System.VirtualKey.NumberPad7 => "7",
        Windows.System.VirtualKey.Number8 or Windows.System.VirtualKey.NumberPad8 => "8",
        Windows.System.VirtualKey.Number9 or Windows.System.VirtualKey.NumberPad9 => "9",
        _ => null
    };

    private void OnDebugKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        // 拾取进行中按 Esc 取消：焦点停在应用内控件（如“拾取”按钮）时，键盘事件不会进入网页，
        // 由页面脚本的 Esc 处理不到；这里在应用层兜底取消（焦点在网页内时仍由页面脚本处理）。
        if (e.Key == Windows.System.VirtualKey.Escape)
        {
            _webShell.CancelActivePick();
            return;
        }

        var d = DebugDigitOf(e.Key);
        if (d is null) return;
        _debugBuffer = (_debugBuffer + d) switch { var s when s.Length > 6 => s[1..], var s => s };
        if (_debugBuffer.Length == 6 && _debugBuffer == "114514")
        {
            _debugBuffer = string.Empty;
            _ = ShowDebugDialogAsync();
        }
    }

    /// <summary>调试解锁对话框：直接以 --load-extension 方式把本地扩展目录加载进 Edge。</summary>
    private async Task ShowDebugDialogAsync()
    {
        try
        {
            var dialog = new ContentDialog
            {
                Title = "调试模式",
                Content = _debugUnlocked
                    ? "调试功能已解锁。可再次选择扩展目录以 --load-extension 方式加载进 Edge。"
                    : "已输入调试口令，解锁「直接添加 Edge 扩展」功能。选择一个包含 manifest.json 的扩展文件夹，将以 --load-extension 方式启动 Edge 并加载。",
                PrimaryButtonText = "选择扩展目录并启动 Edge",
                CloseButtonText = "关闭",
                XamlRoot = Content.XamlRoot
            };
            var result = await dialog.ShowAsync();
            if (result != ContentDialogResult.Primary) return;

            var folder = await PickFolderAsync();
            if (string.IsNullOrEmpty(folder))
            {
                _logger.Information("调试加载扩展：用户取消选择目录");
                return;
            }
            LaunchEdgeWithExtension(folder);
            _debugUnlocked = true;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "调试口功能异常");
        }
    }

    private async Task<string?> PickFolderAsync()
    {
        var picker = new Windows.Storage.Pickers.FolderPicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        picker.FileTypeFilter.Add("*");
        var folder = await picker.PickSingleFolderAsync();
        return folder?.Path;
    }

    private void LaunchEdgeWithExtension(string extensionDir)
    {
        try
        {
            var candidates = new[]
            {
                Environment.ExpandEnvironmentVariables("%ProgramFiles(x86)%\\Microsoft\\Edge\\Application\\msedge.exe"),
                Environment.ExpandEnvironmentVariables("%ProgramFiles%\\Microsoft\\Edge\\Application\\msedge.exe"),
                Environment.ExpandEnvironmentVariables("%LocalAppData%\\Microsoft\\Edge\\Application\\msedge.exe")
            };
            var exe = candidates.FirstOrDefault(File.Exists);
            if (exe is null)
            {
                _logger.Warning("未找到 msedge.exe，无法加载扩展：{Dir}", extensionDir);
                return;
            }
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = exe,
                Arguments = $"--load-extension=\"{extensionDir}\"",
                UseShellExecute = false
            };
            System.Diagnostics.Process.Start(psi);
            _logger.Information("已启动 Edge 并加载扩展：{Dir}", extensionDir);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "启动 Edge 加载扩展失败：{Dir}", extensionDir);
        }
    }

    /// <summary>把 WebShell 控件从后台 HiddenWebHost 拔出，装载到目标容器（BrowserPage/SnifferPage 的可视槽）。</summary>
    /// <param name="targetSlot">目标容器 Border，其 Child 将被设置为 WebShell 控件。</param>
    /// <param name="addressBarVisible">是否显示 WebShell 的地址栏。</param>
    internal void AttachWebShellTo(Border targetSlot, bool addressBarVisible)
    {
        if (targetSlot is null) return;
        // 若 WebShell 仍挂在旧页面槽位，先归还，避免视觉残留叠加
        if (!ReferenceEquals(_currentWebShellSlot, targetSlot))
        {
            DetachWebShellFrom(_currentWebShellSlot);
        }
        if (HiddenWebHost.Child is WebShell ws)
        {
            HiddenWebHost.Child = null;
            targetSlot.Child = ws;
            ws.Visibility = Visibility.Visible;
            ws.SetAddressBarVisible(addressBarVisible);
            _currentWebShellSlot = targetSlot;
        }
        else if (targetSlot.Child is not WebShell)
        {
            // 已经在别处，尝试从当前可视树根找 WebShell
            var root = this.Content as DependencyObject;
            if (root is not null)
            {
                var ws2 = FindWebShellInVisualTree(root);
                if (ws2 is not null && ws2.Parent is Border parentBorder)
                {
                    parentBorder.Child = null;
                    targetSlot.Child = ws2;
                    ws2.Visibility = Visibility.Visible;
                    ws2.SetAddressBarVisible(addressBarVisible);
                    _currentWebShellSlot = targetSlot;
                }
            }
        }
        _webShell.SetVisibility(true, addressBarVisible);
    }

    /// <summary>把 WebShell 控件从源容器还回到 HiddenWebHost（离开可视层时调用）。
    /// 归还后置 Collapsed：WebView2 composition 视觉可能不受宿主 Opacity 影响，必须显式从视觉树摘除以避免残影。</summary>
    internal void DetachWebShellFrom(Border? sourceSlot)
    {
        if (sourceSlot?.Child is WebShell ws)
        {
            sourceSlot.Child = null;
            HiddenWebHost.Child = ws;
            // 关键：Collapsed 才能彻底把 composition visual 从渲染树移除（Opacity=0 对 WebView2 island 不可靠）
            ws.Visibility = Visibility.Collapsed;
            _currentWebShellSlot = null;
            // 仅在真正归还时才同步隐藏状态。关键修复：WinUI 导航时"新页 Loaded 早于旧页 Unloaded"，
            // 旧 BrowserPage 的 Unloaded 晚于新页挂载触发；若无条件 SetVisibility(false)，
            // 会把新页面刚挂载好的 WebShell 重新隐藏（"先开AI辅助再进网址导航后网页区空白"的根因）。
            _webShell.SetVisibility(false, false);
        }
    }

    /// <summary>导航到任意页面之前强制隐藏 WebShell（无论其挂在哪个槽位/可视树节点），归还 HiddenWebHost 并复位 AI 侧栏。
    /// 与 <see cref="DetachWebShellFrom"/> 相比增加可视树兜底查找，覆盖"槽位记录与真实挂载点不一致"导致
    /// WebShell 残留在即将卸载页面中、网页 UI 覆盖新功能页（如音乐播放器右侧）的情况。</summary>
    internal void HideWebShellForNavigation()
    {
        try
        {
            WebShell? found = null;
            // 1) 常规路径：从记录的槽位摘除
            if (_currentWebShellSlot?.Child is WebShell ws1)
            {
                found = ws1;
                _currentWebShellSlot.Child = null;
                _currentWebShellSlot = null;
            }
            // 2) 兜底：WebShell 仍挂在可视树其它节点（ContentFrame 内旧页面槽位等）
            if (found is null && this.Content is DependencyObject root)
            {
                var ws2 = FindWebShellInVisualTree(root);
                if (ws2 is not null && !ReferenceEquals(ws2.Parent, HiddenWebHost))
                {
                    found = ws2;
                    if (ws2.Parent is Border pb)
                    {
                        pb.Child = null;
                    }
                }
            }
            if (found is not null)
            {
                HiddenWebHost.Child = found;
                found.Visibility = Visibility.Collapsed;
            }
            // 3) 复位地址栏与 AI 侧栏，避免残留叠加到新页面
            _webShell.SetVisibility(false, false);
            WebShellLayer.IsAiAssistOpen = false;
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "导航前隐藏 WebShell 失败");
        }
    }

    private static WebShell? FindWebShellInVisualTree(DependencyObject root)
    {
        if (root is WebShell found) return found;
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            var r = FindWebShellInVisualTree(child);
            if (r is not null) return r;
        }
        return null;
    }

    /// <summary>异步播放 SplashOverlay。</summary>
    private async Task PlaySplashAsync()
    {
        try
        {
            await SplashLayer.PlayStartupAsync();
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "SplashOverlay 动画异常");
            SplashLayer.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>侧栏选中项变更路由。相同 Tag 二次点击 → 关闭功能（淡出后回到欢迎页）。</summary>
    private void OnNavSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is not NavigationViewItem item || item.Tag is not string tag)
        {
            return;
        }

        // 如果已经在该功能页，二次点击 → 关闭功能（淡出回欢迎页）
        if (tag == _lastSelectedTag)
        {
            // 右侧栏AI辅助开合约定：二次点击时先收起侧栏
            if (tag == "AiAssist" && WebShellLayer.IsAiAssistOpen)
            {
                WebShellLayer.IsAiAssistOpen = false;
                _logger.Information("右侧栏AI辅助：二次点击收起侧栏");
            }
            _ = CloseCurrentFeatureFadeAndGoHomeAsync();
            _logger.Information("侧栏二次点击：触发淡出关闭并回欢迎页");
            return;
        }

        _lastSelectedTag = tag;

        try
        {
            // 关键修复：导航前若 WebShell 仍挂在旧页面槽位（Browser/Sniffer 页 Unloaded 可能不触发或时序晚于新页渲染），
            // 立即强制摘除并归还 HiddenWebHost，避免网页/地址栏视觉残影叠加到新功能页（用户反馈的"UI 重叠"根因）。
            HideWebShellForNavigation();

            bool navigated;
            switch (tag)
            {
                case "Browser":
                    // 模式：纯导航（AI 辅助侧栏关闭）
                    navigated = _navigation.Navigate<BrowserPage>(BrowserMode.Browser);
                    _logger.Information("侧栏选中：网址导航（BrowserPage Mode=Browser）");
                    // 同页重建可能导致 WebShell 状态残留，显式关闭侧栏
                    if (navigated && WebShellLayer.IsAiAssistOpen) WebShellLayer.IsAiAssistOpen = false;
                    break;
                case "AiAssist":
                    // 模式：AI 辅助（侧栏展开）
                    navigated = _navigation.Navigate<BrowserPage>(BrowserMode.AiAssist);
                    _logger.Information("侧栏选中：右侧栏AI辅助（BrowserPage Mode=AiAssist）");
                    if (navigated && !WebShellLayer.IsAiAssistOpen) WebShellLayer.IsAiAssistOpen = true;
                    break;
                default:
                    navigated = tag switch
                    {
                        "OCR" => NavigateWithLog<OcrPage>("本地 OCR（OcrPage）"),
                        "Sniffer" => NavigateWithLog<SnifferPage>("视频/音频嗅探器（SnifferPage，带迷你地址栏 + WebShell 资源嗅探）"),
                        "FileTransfer" => NavigateWithLog<FileTransferPage>("同局域网传文件（FileTransferPage）"),
                        "AnswerShare" => NavigateWithLog<AnswerSharePage>("分享题目答案（AnswerSharePage）"),
                        "AutoAnswer" => NavigateWithLog<AutoAnswerPage>("自动答题（AutoAnswerPage：区域拾取 + 本机模型生成代码 + 自动提交）"),
                        "LanAI" => NavigateWithLog<LanAIPage>("局域网 AI 服务（LanAIPage）"),
                        "Ollama" => NavigateWithLog<OllamaPage>("本地 Ollama（OllamaPage：一键启动 / 模型管理）"),
                        "MediaSummary" => NavigateWithLog<MediaSummaryPage>("音视频 AI 总结（MediaSummaryPage：抽取音频/转写/总结）"),
                        "MindMap" => NavigateWithLog<MindMapPage>("思维导图（MindMapPage：Markdown → SVG/PNG/HTML/MD）"),
                        "HtmlEditor" => NavigateWithLog<HtmlEditorPage>("HTML 编辑器（HtmlEditorPage：富文本 + AI + 多格式导出）"),
                        "Calculator" => NavigateWithLog<CalculatorPage>("科学计算器（CalculatorPage：表达式 + 函数绘图）"),
                        "Physics" => NavigateWithLog<PhysicsSimPage>("物理模拟器（PhysicsSimPage：可搭场景 + AI 生成）"),
                        "Genetics" => NavigateWithLog<GeneticsPage>("生物遗传模拟器（GeneticsPage：杂交 / 哈代-温伯格）"),
                        "WrongBook" => NavigateWithLog<WrongBookPage>("错题本（WrongBookPage：本地题库 + AI 出题）"),
                        "Molecule" => NavigateWithLog<MoleculePage>("分子模拟器（MoleculePage：SMILES 结构 + AI）"),
                        "Novel" => NavigateWithLog<NovelPage>("AI 小说创作（NovelPage：工作区前情 + 流式续写）"),
                        "Autoscript" => NavigateWithLog<AutoscriptPage>("Autoscript（AutoscriptPage：Lua 沙箱脚本）"),
                        "Messaging" => NavigateWithLog<MessagingPage>("通讯软件接入（MessagingPage：Telegram Bot + Webhook）"),
                        "Automation" => NavigateWithLog<SoftwareAutomationPage>("工程软件自动化（SoftwareAutomationPage：Blender/FreeCAD/AutoCAD）"),
                        "Convert" => NavigateWithLog<TransformPage>("文件转换（TransformPage：Word↔PDF / MP4 / 图片）"),
                        "StreamDownload" => NavigateWithLog<StreamDownloadPage>("流媒体下载（StreamDownloadPage：直链 / m3u8 下载）"),
                        "EpubReader" => NavigateWithLog<EpubReaderPage>("EPUB 阅读器（EpubReaderPage）"),
                        "Vpn" => NavigateWithLog<VpnPage>("VPN（VpnPage：节点/连接/流量统计）"),
                        "JiYuTrainer" => NavigateWithLog<JiYuTrainerPage>("极域教室控制（JiYuTrainerPage：C# 功能面板 + 代码雨）"),
                        "MusicPlayer" => NavigateWithLog<MusicPlayerPage>("音乐播放器（MusicPlayerPage：原生播放器 + 歌词 + 曲绘）"),
                        "VideoPlayer" => NavigateWithLog<VideoPlayerPage>("视频播放器（VideoPlayerPage：本地/在线视频）"),
                        "PdfViewer" => NavigateWithLog<PdfViewerPage>("PDF 查看器（PdfViewerPage：翻页/缩放/批注/裁剪/合并）"),
                        "EdgeExtensionTest" => NavigateWithLog<EdgeExtensionTestPage>("Edge扩展引入测试（带扩展的 WebView2 测试浏览器）"),
                        "Settings" => NavigateWithLog<SettingsPage>("设置（SettingsPage：集中控制面板）"),
                        "About" => NavigateWithLog<AboutPage>("关于软件（AboutPage：版本 / 功能 / 赞助码）"),
                        _ => NavigateWithLog<WelcomePage>($"未知 Tag：{tag}（回退欢迎页）")
                    };
                    break;
            }

            // 关键修复：导航失败（目标页 XAML 构造异常等）时 Frame 仍显示旧页，但侧栏选中项已切换，
            // 视觉上表现为"旧功能页 UI 残留在新功能里"。回退到欢迎页并清除选中项（置 null 不触发导航，
            // 若设为第一个菜单项"网址导航"会再次触发 SelectionChanged 自动跳回网址导航）。
            if (!navigated)
            {
                _logger.Warning("导航失败，回退欢迎页：{Tag}", tag);
                _navigation.Navigate<WelcomePage>();
                _lastSelectedTag = null;
                RootNav.SelectedItem = null;
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "侧栏路由异常：{Tag}", tag);
        }
    }

    /// <summary>关闭当前功能（ContentFrame 淡出 250ms）后切到欢迎页，避免"咔嚓一下突然消失"的突兀感。</summary>
    private async System.Threading.Tasks.Task CloseCurrentFeatureFadeAndGoHomeAsync()
    {
        try
        {
            var sb = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation
            {
                From = 1.0,
                To = 0.0,
                Duration = new Microsoft.UI.Xaml.Duration(TimeSpan.FromMilliseconds(250)),
                FillBehavior = Microsoft.UI.Xaml.Media.Animation.FillBehavior.HoldEnd
            };
            ContentFrame.OpacityTransition = null;
            ContentFrame.Opacity = 1.0;
            var storyboard = new Microsoft.UI.Xaml.Media.Animation.Storyboard();
            Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(sb, ContentFrame);
            Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(sb, nameof(ContentFrame.Opacity));
            storyboard.Children.Add(sb);
            var tcs = new TaskCompletionSource<bool>();
            storyboard.Completed += (_, _) => tcs.TrySetResult(true);
            storyboard.Begin();
            await tcs.Task;
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "淡出动画失败，直接回欢迎页");
        }

        try
        {
            HideWebShellForNavigation();
            _navigation.Navigate<WelcomePage>();
            ContentFrame.Opacity = 1.0;
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "淡出后回欢迎页失败");
            ContentFrame.Opacity = 1.0;
        }
        _lastSelectedTag = null;
        _logger.Information("功能已关闭（淡出+回欢迎页）");
    }

    /// <summary>导航到目标页并记录日志，返回是否成功。</summary>
    private bool NavigateWithLog<TPage>(string logMessage) where TPage : Microsoft.UI.Xaml.Controls.Page
    {
        var ok = _navigation.Navigate<TPage>();
        _logger.Information("侧栏选中：{Message}", logMessage);
        return ok;
    }

    /// <summary>接收跨层导航消息（右侧栏AI辅助 → 设置页等）：按 Tag 选中导航项。</summary>
    public void Receive(ViewModels.NavigateToTagMessage message) => SelectNavTag(message.Tag);

    /// <summary>接收内测解锁消息（设置页口令校验通过）：显示"Edge扩展引入测试"侧栏项。</summary>
    public void Receive(ViewModels.TesterUnlockedMessage message)
    {
        EdgeExtensionTestItem.Visibility = Visibility.Visible;
        _logger.Information("内测人员已解锁：Edge扩展引入测试侧栏项已显示");
    }

    /// <summary>按 Tag 选中导航项（跨层跳转入口；已在目标页时忽略，避免误触发"二次点击回欢迎页"）。</summary>
    private void SelectNavTag(string tag)
    {
        if (_lastSelectedTag == tag)
        {
            return;
        }

        NavigationViewItem? found = RootNav.MenuItems.OfType<NavigationViewItem>()
            .Concat(RootNav.FooterMenuItems.OfType<NavigationViewItem>())
            .FirstOrDefault(it => it.Tag as string == tag);

        if (found is not null)
        {
            RootNav.SelectedItem = found;
        }
    }
}
