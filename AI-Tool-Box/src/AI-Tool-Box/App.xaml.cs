using AI_Tool_Box.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.UI.Xaml;
using Serilog;

namespace AI_Tool_Box;

/// <summary>应用程序入口：负责构建 DI 容器、配置 Serilog 与启动主窗口。</summary>
public partial class App : Application
{
    private readonly IHost _host;
    private Window? _mainWindow;

    /// <summary>初始化 App 实例，构建 DI 容器并配置 Serilog。</summary>
    public App()
    {
        // 第一步：配置 Serilog（必须在 InitializeComponent 之前，以便后续步骤可记录日志）
        Log.Logger = LoggerSetup.Configure();
        Log.Information("=== AI-Tool-Box 启动 ===");

        // 第二步：构建 DI 容器
        _host = Host.CreateDefaultBuilder()
            .UseSerilog()
            .ConfigureServices((_, services) =>
            {
                services.AddApplicationServices();
            })
            .Build();

        // 注册进程退出钩子，确保 DI 容器与日志缓冲被正确释放
        AppDomain.CurrentDomain.ProcessExit += OnProcessExit;

        // 全局异常兜底：XAML UI 线程未处理异常记入日志并标记 Handled，
        // 防止出现 "unhandled win32 exception" JIT 调试器弹窗导致进程直接崩溃（如侧栏打开某页面时偶发异常）。
        this.UnhandledException += OnUiUnhandledException;

        // 非 UI 线程 / 任务异常也统一记录（不终止进程）
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        InitializeComponent();
    }

    private void OnUiUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        Log.Error(e.Exception, "XAML 未处理异常：{Message}", e.Message);
        System.Diagnostics.Trace.WriteLine($"XAML 未处理异常：{e.Exception}");
        // 已记录即可安全继续运行（页面级失败不影响其他功能）
        e.Handled = true;
    }

    private void OnDomainUnhandledException(object? sender, System.UnhandledExceptionEventArgs e)
    {
        var ex = e.ExceptionObject as Exception;
        Log.Fatal(ex, "非 UI 线程未处理异常（IsTerminating={Terminating}）", e.IsTerminating);
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        Log.Error(e.Exception, "未观察的任务异常（已压制，共 {Count} 个）", e.Exception.InnerExceptions.Count);
        e.SetObserved();
    }

    /// <summary>获取全局服务提供者，供非 DI 上下文（如 XAML 资源）取用。</summary>
    public static IServiceProvider Services { get; private set; } = EmptyServiceProvider.Instance;

    /// <summary>获取当前主窗口实例，供 Page/Picker 绑定窗口句柄使用。</summary>
    public static Window MainWindow { get; private set; } = null!;

    /// <summary>应用启动前使用的空服务提供者，所有查询返回 null。</summary>
    private sealed class EmptyServiceProvider : IServiceProvider
    {
        /// <summary>获取空提供者单例。</summary>
        public static EmptyServiceProvider Instance { get; } = new();

        /// <inheritdoc/>
        public object? GetService(Type serviceType) => null;
    }

    /// <summary>应用启动时同步激活 MainWindow；Splash 动画由 MainWindow 内的 SplashOverlay 覆盖层处理。</summary>
    /// <param name="args">启动激活参数。</param>
    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        Services = _host.Services;

        var logger = Services.GetRequiredService<ILogger>();
        logger.Information("启动流程开始");

        try
        {
            // 主窗口：导航栏 + 欢迎页 + 启动覆盖层（SplashLayer 在构造函数内 fire-and-forget 播放淡出）
            _mainWindow = new MainWindow();
            MainWindow = _mainWindow;
            _mainWindow.Activate();
            logger.Information("主窗口已激活");

            // 上次异常退出可能留下指向本地回环的残留系统代理 → 自动恢复直连（避免系统网络请求卡死）
            try
            {
                if (Services.GetService(typeof(VpnService)) is VpnService vpn)
                {
                    vpn.CleanupStaleSystemProxy();
                }
            }
            catch (Exception proxyEx)
            {
                logger.Warning(proxyEx, "启动清理残留代理设置失败");
            }
        }
        catch (Exception ex)
        {
            logger.Fatal(ex, "启动流程异常");
        }
    }

    /// <summary>进程退出回调：刷新日志缓冲并释放 DI 容器。</summary>
    /// <param name="sender">事件发送者。</param>
    /// <param name="e">进程退出事件参数。</param>
    private void OnProcessExit(object? sender, EventArgs e)
    {
        try
        {
            Log.Information("=== AI-Tool-Box 进程退出 ===");
            _host.Dispose();
        }
        catch (Exception ex)
        {
            // 退出阶段日志器可能已失效，写入系统跟踪以保底
            System.Diagnostics.Trace.WriteLine($"进程退出清理异常：{ex}");
        }
        finally
        {
            Log.CloseAndFlush();
        }
    }
}
