using AI_Tool_Box.Controls;
using Microsoft.UI.Xaml;
using Serilog;
using Windows.Graphics;

namespace AI_Tool_Box.Views;

/// <summary>独立浏览器窗口（标签拖出 tear-out 的卫星窗口）：
/// 内部承载一个新的 WebShell 卫星实例并直接打开被拖标签的 URL（WebView2 无法跨窗口复用，故传 URL 重建；
/// 登录态通过全进程共享的 CoreWebView2Environment / 同一用户数据目录保持）。
/// 窗口标题跟随网页 DocumentTitle；关闭时统一清理事件订阅并释放 WebView2 资源。</summary>
public sealed partial class BrowserTearoutWindow : Window
{
    private readonly ILogger _logger;
    private readonly WebShell _shell;
    private readonly string? _pendingUrl;
    private bool _initialOpened;

    /// <summary>卫星窗口唯一 Id（用于拖回时按源窗口定位）。</summary>
    public Guid Id { get; } = Guid.NewGuid();

    /// <summary>本窗口承载的卫星 WebShell。</summary>
    public WebShell Shell => _shell;

    /// <summary>创建独立浏览器窗口并打开指定 URL。</summary>
    /// <param name="url">被拖标签的 URL（null/about:blank 则打开新建标签页）。</param>
    /// <param name="title">初始标题（被拖标签标题；后续由 DocumentTitle 覆盖）。</param>
    public BrowserTearoutWindow(string url, string? title = null)
    {
        InitializeComponent();
        _logger = App.Services.GetService(typeof(ILogger)) as ILogger
                  ?? Serilog.Log.Logger;

        Title = string.IsNullOrWhiteSpace(title) ? "新标签页" : title;
        _pendingUrl = string.IsNullOrWhiteSpace(url) || url == "about:blank" ? null : url;

        _shell = new WebShell(isSatellite: true)
        {
            OwningWindow = this
        };
        ShellSlot.Child = _shell;
        _shell.ActiveTitleChanged += OnShellTitleChanged;

        WebShell.RegisterSatellite(this);
        // 注：WindowClosedEventArgs 在当前投影下不可按名引用，改用 lambda 由编译器推断类型
        Closed += (_, _) => OnClosed();

        // 初始尺寸（默认窗口过小）
        try
        {
            AppWindow.Resize(new SizeInt32(1280, 860));
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "设置独立窗口初始尺寸失败");
        }

        // HWND 就绪（窗口激活）后再初始化标签，避免 WebView2 控件在无窗口句柄时初始化
        Activated += OnFirstActivated;
    }

    private void OnFirstActivated(object sender, WindowActivatedEventArgs args)
    {
        if (_initialOpened || args.WindowActivationState == WindowActivationState.Deactivated)
        {
            return;
        }
        _initialOpened = true;
        Activated -= OnFirstActivated;
        _ = OpenInitialTabAsync();
    }

    private async Task OpenInitialTabAsync()
    {
        try
        {
            if (_pendingUrl is null)
            {
                _shell.NewTab();
            }
            else
            {
                await _shell.OpenUrlInNewTabAsync(_pendingUrl);
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "独立窗口打开初始标签失败：{Url}", _pendingUrl);
        }
    }

    private void OnShellTitleChanged(object? sender, string title)
    {
        try
        {
            Title = string.IsNullOrWhiteSpace(title) ? "浏览器" : title;
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "更新独立窗口标题失败");
        }
    }

    private void OnClosed()
    {
        try
        {
            // 清理事件订阅，避免泄漏；并关闭全部 WebView2 释放内核资源
            _shell.ActiveTitleChanged -= OnShellTitleChanged;
            WebShell.UnregisterSatellite(Id);
            _shell.Shutdown();
            _logger.Information("独立浏览器窗口已关闭并完成资源清理");
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "独立窗口关闭清理失败");
        }
    }
}
