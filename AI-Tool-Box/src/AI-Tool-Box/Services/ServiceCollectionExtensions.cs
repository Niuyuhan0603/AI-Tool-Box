using AI_Tool_Box.Services;
using AI_Tool_Box.ViewModels;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Dispatching;
using Serilog;

namespace AI_Tool_Box.Services;

/// <summary>DI 容器注册扩展方法集合。</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>注册应用核心服务：Serilog 日志器、导航服务、WebShell、AI 服务、消息总线。</summary>
    /// <param name="services">待注册的 <see cref="IServiceCollection"/>。</param>
    /// <returns>返回原 <see cref="IServiceCollection"/> 以便链式调用。</returns>
    public static IServiceCollection AddApplicationServices(this IServiceCollection services)
    {
        if (services is null)
        {
            throw new ArgumentNullException(nameof(services));
        }

        // Serilog 主日志器：单例
        services.AddSingleton(Log.Logger);

        // 导航服务：单例
        services.AddSingleton<INavigationService, NavigationService>();

        // WebShell 服务：单例（持有 WebShell 控件引用，跨页面共享 WebView2 状态）
        services.AddSingleton<IWebShellService, WebShellService>();

        // AI 服务：Ollama 本地 + OpenAI 兼容 API，单例（注入 UsageTracker 以记录 token 消耗）
        services.AddSingleton<IOllamaService, OllamaService>();
        // Ollama 运行时管理：检测 ollama.exe / 一键 serve / pull / rm（单例，跨页复用进程句柄）
        services.AddSingleton<IOllamaRuntimeService, OllamaRuntimeService>();
        services.AddSingleton<IApiAiService>(sp => new ApiAiService(
            sp.GetRequiredService<ILogger>(),
            sp.GetService<IUsageTracker>()));

        // API Key 安全存储：DPAPI 加密，单例
        services.AddSingleton<IApiKeyService, ApiKeyService>();

        // 用量追踪：API token + VPN 流量统计，持久化 usage.json，单例
        services.AddSingleton<IUsageTracker, UsageTracker>();

        // 应用集中配置：各功能线程数 + 通用参数，持久化 app_settings.json（与 FfmpegLocator 共享文件），单例
        services.AddSingleton<IAppSettingsService, AppSettingsService>();

        // VPN：加密代理节点接入（一键连接 + 流量统计），单例
        // GreenHub 节点为 VMess(WS+TLS)：账号由节点 API 下发，经 v2ray 核心的本地 HTTP 入站转发
        services.AddSingleton<IVpnGhAccountClient, VpnGhAccountClient>();
        services.AddSingleton<IV2rayCoreService, V2rayCoreService>();
        services.AddSingleton<VpnService>();

        // 内测人员 + Edge 扩展管理：单例（口令 114514 解锁；扩展复制到 %LOCALAPPDATA%\AI-Tool-Box\edge_extensions）
        services.AddSingleton<EdgeExtensionService>();

        // 右侧栏AI辅助：设置持久化（ai_assist_settings.json）+ 聊天流式服务（OpenAI 兼容/Anthropic），单例
        services.AddSingleton<IAiAssistSettingsService, AiAssistSettingsService>();
        services.AddSingleton<IAiAssistChatService, AiAssistChatService>();
        // 一次性 AI 调用（复用上面的档案与 DPAPI 密钥）：供 AI 总结 / 思维导图 / HTML 编辑等页面使用
        services.AddSingleton<IAiQuickService, AiQuickService>();
        // 音视频 AI 总结：ffmpeg 抽音频 + 分段总结
        services.AddSingleton<IMediaSummaryService, MediaSummaryService>();
        // HTML → DOCX / PPTX / SVG 导出
        services.AddSingleton<IOfficeExportService, OfficeExportService>();
        // HTML → 演示文稿（幻灯片切分 + 自包含全屏播放器 + 媒体搬迁）
        services.AddSingleton<IPresentationService, PresentationService>();
        // 错题本：本地 JSON（%LOCALAPPDATA%\AI-Tool-Box\wrongbook.json）
        services.AddSingleton<IWrongBookService, WrongBookService>();
        // Autoscript：Lua 沙箱（MoonSharp）+ toolbox API + ATS1 加密
        services.AddSingleton<IAutoscriptService, AutoscriptService>();
        // 通讯软件接入：Telegram Bot 长轮询 + Webhook 推送
        services.AddSingleton<IBotBridgeService, BotBridgeService>();
        // 工程/3D 软件自动化：命令行 + 脚本驱动
        services.AddSingleton<ISoftwareAutomationService, SoftwareAutomationService>();

        // 自动答题：离线（手动拾取区域 + 本机 Ollama 生成代码）与联网（AI 驱动浏览器控制器），单例
        services.AddSingleton<IAutoAnswerSettingsService, AutoAnswerSettingsService>();
        services.AddSingleton<IAutoAnswerService, AutoAnswerService>();
        services.AddSingleton<IBrowserControllerService, BrowserControllerService>();

        // 消息总线：弱引用实现
        services.AddSingleton<IMessenger>(_ => WeakReferenceMessenger.Default);

        // 局域网传文件：单例（UDP 多播 53317 发现 + HTTP REST 会话式传输，借鉴 LocalSend 协议）。
        // 注意：必须在 UI 线程调用 AddApplicationServices（DispatcherQueue.GetForCurrentThread 有效）。
        services.AddSingleton<ILanTransferService>(sp =>
        {
            var dispatcher = DispatcherQueue.GetForCurrentThread()
                ?? throw new InvalidOperationException("ILanTransferService 必须在 UI 线程注册（DispatcherQueue 为空）。");
            var logger = sp.GetRequiredService<ILogger>();
            return new LanTransferService(logger, dispatcher);
        });

        // 题库分享：单例（占用 UDP 9529 / TCP 9530，持久化 JSON 到 webdata/answers.json）
        services.AddSingleton<IAnswerShareService>(sp =>
        {
            var dispatcher = DispatcherQueue.GetForCurrentThread()
                ?? throw new InvalidOperationException("IAnswerShareService 必须在 UI 线程注册（DispatcherQueue 为空）。");
            var logger = sp.GetRequiredService<ILogger>();
            return new AnswerShareService(logger, dispatcher);
        });

        // 局域网 AI 服务：单例（占用 UDP 9532 广播 + HTTP 端口 9531+；持久化 JSON webdata/lanai_settings.json）
        services.AddSingleton<ILanAiService>(sp =>
        {
            var dispatcher = DispatcherQueue.GetForCurrentThread()
                ?? throw new InvalidOperationException("ILanAiService 必须在 UI 线程注册（DispatcherQueue 为空）。");
            var logger = sp.GetRequiredService<ILogger>();
            var apiKey = sp.GetService<IApiKeyService>();
            return new LanAiService(logger, dispatcher, apiKey);
        });

        // 媒体嗅探器：单例（共享 WebView2，跨页面保持嗅探状态）
        services.AddSingleton<ISnifferService>(sp =>
        {
            var dispatcher = DispatcherQueue.GetForCurrentThread()
                ?? throw new InvalidOperationException("ISnifferService 必须在 UI 线程注册（DispatcherQueue 为空）。");
            var logger = sp.GetRequiredService<ILogger>();
            var web = sp.GetRequiredService<IWebShellService>();
            return new SnifferService(logger, web, dispatcher);
        });

        // FFmpeg 路径定位器：单例（用户自定义路径 → PATH/常见安装位置 → Assets 手动放置兜底，持久化 %LOCALAPPDATA%\AI-Tool-Box\app_settings.json）
        services.AddSingleton<FfmpegLocator>();

        // 文件转换服务：单例（生命周期全局，保存 HttpClient 与 FFmpeg 定位缓存）
        services.AddSingleton<ITransformService, TransformService>();

        // 本地 OCR（LiteParse2.0 / lit.exe）：单例（引擎与 traineddata 随应用 Assets\LiteParse 分发，完全离线）
        services.AddSingleton<ILiteParseOcrService, LiteParseOcrService>();

        // ViewModel：瞬时实例
        services.AddTransient<OcrViewModel>();
        services.AddTransient<FileTransferViewModel>();
        services.AddTransient<AnswerShareViewModel>();
        services.AddTransient<LanAiViewModel>();
        services.AddTransient<SnifferViewModel>();
        services.AddTransient<TransformViewModel>();
        services.AddTransient<SettingsViewModel>();
        services.AddTransient<VpnViewModel>();
        services.AddTransient<OllamaViewModel>();
        services.AddTransient<MediaSummaryViewModel>();
        services.AddTransient<MindMapViewModel>();
        // 右侧栏AI辅助：单例（跨页面/卫星窗口共享会话与选中档案）
        services.AddSingleton<AiAssistViewModel>();

        return services;
    }
}
