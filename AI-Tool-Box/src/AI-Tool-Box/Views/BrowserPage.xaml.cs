using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using AI_Tool_Box.Services;
using Serilog;

namespace AI_Tool_Box.Views;

/// <summary>BrowserPage 运行模式：通过导航参数传递，控制 AI 侧栏开合。</summary>
public enum BrowserMode
{
    /// <summary>网址导航模式（关闭 AI 辅助侧栏）。</summary>
    Browser = 0,
    /// <summary>右侧栏 AI 辅助模式（打开 AI 辅助侧栏）。</summary>
    AiAssist = 1,
}

/// <summary>网址导航页：从主窗口 HiddenWebHost 装载 WebShell 可视层，实现多标签、地址栏、悬浮球。
/// 页面卸载时把 WebShell 送回 HiddenWebHost，保证 WebView2 状态不丢失。
/// 支持两种模式（通过 NavigationParameter=BrowserMode 控制 AI 侧栏开合）。</summary>
public sealed partial class BrowserPage : Page
{
    private readonly ILogger _logger;
    private readonly IWebShellService _webShell;
    private BrowserMode _mode = BrowserMode.Browser;

    /// <summary>初始化 BrowserPage。</summary>
    public BrowserPage()
    {
        InitializeComponent();
        _logger = App.Services.GetService(typeof(ILogger)) as ILogger
                  ?? Serilog.Log.Logger;
        _webShell = App.Services.GetRequiredService<IWebShellService>();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    /// <summary>接收导航参数（BrowserMode），决定 AI 侧栏开合。</summary>
    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is BrowserMode mode)
        {
            _mode = mode;
        }
        else if (e.Parameter is string s && Enum.TryParse<BrowserMode>(s, out var parsed))
        {
            _mode = parsed;
        }
        _logger.Information("BrowserPage.OnNavigatedTo：Mode={Mode}（Param={Param}）", _mode, e.Parameter ?? "null");
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            if (App.MainWindow is MainWindow mw)
            {
                mw.AttachWebShellTo(WebShellSlot, addressBarVisible: true);
                // 按 BrowserMode 同步 AI 侧栏开合（同页重建后 WebShell.IsAiAssistOpen 取决于 WebShell 自身状态，
                // 这里必须强制覆盖；否则 Browser→AiAssist 同页重建后侧栏仍保持旧状态）。
                _webShell.IsAiAssistOpen = (_mode == BrowserMode.AiAssist);
                _logger.Information("BrowserPage Loaded：已挂载 WebShell（地址栏可见，AI 侧栏={Open}）",
                    _webShell.IsAiAssistOpen ? "开" : "关");
                // 关键修复：转移容器后强制重新布局，激活 WebView2 像素渲染
                var dq = DispatcherQueue.GetForCurrentThread();
                if (dq is not null)
                {
                    dq.TryEnqueue(DispatcherQueuePriority.Low, () =>
                    {
                        try
                        {
                            WebShellSlot.InvalidateMeasure();
                            WebShellSlot.InvalidateArrange();
                            if (WebShellSlot.Child is UIElement child)
                            {
                                child.InvalidateMeasure();
                                child.InvalidateArrange();
                                child.UpdateLayout();
                            }
                            WebShellSlot.UpdateLayout();
                        }
                        catch (Exception ex)
                        {
                            _logger.Warning(ex, "BrowserPage 强制刷新布局失败");
                        }
                    });
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "BrowserPage 挂载 WebShell 失败");
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        try
        {
            if (App.MainWindow is MainWindow mw)
            {
                mw.DetachWebShellFrom(WebShellSlot);
                _logger.Information("BrowserPage Unloaded：已归还 WebShell 到 HiddenWebHost");
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "BrowserPage 归还 WebShell 失败");
        }
        Loaded -= OnLoaded;
        Unloaded -= OnUnloaded;
    }
}
