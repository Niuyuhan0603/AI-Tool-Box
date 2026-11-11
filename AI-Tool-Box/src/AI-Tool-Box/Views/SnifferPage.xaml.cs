using AI_Tool_Box.Services;
using AI_Tool_Box.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Serilog;
using System.Text;
using Windows.ApplicationModel.DataTransfer;

namespace AI_Tool_Box.Views;

/// <summary>视频/音频嗅探器页：顶部迷你地址栏 + 左侧网页可视区（从 HiddenWebHost Attach WebShell，地址栏隐藏）
/// + 右侧嗅探结果/下载任务面板（原底部横条改为右侧竖栏，网页保留更多纵向空间）。</summary>
public sealed partial class SnifferPage : Page
{
    private readonly ILogger _logger;
    private readonly IWebShellService _webShell;
    private readonly ISnifferService _sniffer;
    private readonly SnifferViewModel _viewModel;
    private readonly DispatcherQueue _dispatcher;
    private DispatcherQueueTimer? _urlSyncTimer;
    private bool _isEditingUrl;

    public SnifferViewModel ViewModel => _viewModel;

    public SnifferPage()
    {
        InitializeComponent();
        _logger = App.Services.GetService(typeof(ILogger)) as ILogger
                  ?? Serilog.Log.Logger;
        _webShell = App.Services.GetRequiredService<IWebShellService>();
        _viewModel = App.Services.GetRequiredService<SnifferViewModel>();
        _sniffer = App.Services.GetRequiredService<ISnifferService>();
        _dispatcher = DispatcherQueue.GetForCurrentThread()
                     ?? DispatcherQueueController.CreateOnDedicatedThread().DispatcherQueue;
        DataContext = _viewModel;

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;

        MiniUrlBox.GotFocus += (_, _) => _isEditingUrl = true;
        MiniUrlBox.LostFocus += (_, _) => _isEditingUrl = false;
        _logger.Information("SnifferPage 构造完成");
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            // 1) Attach WebShell（显示网页，但隐藏 WebShell 原地址栏，因为本页有迷你地址栏）
            if (App.MainWindow is MainWindow mw)
            {
                mw.AttachWebShellTo(WebShellSlot, addressBarVisible: false);
                _logger.Information("SnifferPage Loaded：WebShell 已 Attach（原地址栏隐藏，使用页面迷你地址栏）");

                // 2) 强制布局刷新（同 BrowserPage 修复 WebView2 白屏逻辑）
                _dispatcher.TryEnqueue(DispatcherQueuePriority.Low, () =>
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
                        _logger.Warning(ex, "SnifferPage 强制刷新布局失败");
                    }
                });
            }

            // 3) 启动迷你地址栏 URL 同步
            _urlSyncTimer = _dispatcher.CreateTimer();
            _urlSyncTimer.Interval = TimeSpan.FromSeconds(1);
            _urlSyncTimer.Tick += (_, _) =>
            {
                if (_isEditingUrl) return;
                var url = _webShell.CurrentUrl;
                if (!string.IsNullOrEmpty(url) && MiniUrlBox.Text != url) MiniUrlBox.Text = url;
            };
            _urlSyncTimer.Start();
            var init = _webShell.CurrentUrl;
            if (!string.IsNullOrEmpty(init)) MiniUrlBox.Text = init;

            // 4) 网页内"已捕捉到视频"悬浮按钮点击 → 选中资源 Tab
            _sniffer.OpenSnifferRequested -= OnSnifferBadgeRequested;
            _sniffer.OpenSnifferRequested += OnSnifferBadgeRequested;

            // 5) 如果嗅探器未开启，提示用户自动开启
            if (!ViewModel.IsSniffingOn)
            {
                ViewModel.ToggleSniffingCommand.Execute(null);
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "SnifferPage OnLoaded 异常");
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_urlSyncTimer is not null)
            {
                _urlSyncTimer.Stop();
                _urlSyncTimer = null;
            }
            if (App.MainWindow is MainWindow mw)
            {
                mw.DetachWebShellFrom(WebShellSlot);
                _logger.Information("SnifferPage Unloaded：WebShell 已归还到 HiddenWebHost");
            }
            _sniffer.OpenSnifferRequested -= OnSnifferBadgeRequested;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "SnifferPage Detach 异常");
        }
        Loaded -= OnLoaded;
        Unloaded -= OnUnloaded;
        // 页面卸载不强制关闭嗅探（用户可能只是临时切走还想继续捕获其他页网络）
    }

    /// <summary>网页内悬浮按钮被点击：资源列表已在右侧常驻显示，这里仅记录并提示。</summary>
    private void OnSnifferBadgeRequested(object? sender, EventArgs e)
    {
        ViewModel.Status = $"已捕捉到视频，请在右侧“嗅探到的媒体资源”列表查看（共 {ViewModel.Resources.Count} 条）。";
        _logger.Information("网页内“已捕捉到视频”按钮被点击（右侧结果面板常驻显示）");
    }

    // ===== 右侧列表：下载 / 重试 / 右键菜单 =====

    /// <summary>点击某项的“下载”按钮。</summary>
    private async void OnItemDownloadClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is SnifferResourceItem item)
        {
            await ViewModel.DownloadResourceAsync(item);
        }
    }

    /// <summary>点击某项的“重试”按钮：重新下载该资源（用户需求 4）。</summary>
    private async void OnItemRetryClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is SnifferResourceItem item)
        {
            await ViewModel.RetryResourceAsync(item);
        }
    }

    /// <summary>下载任务列表里“重试”失败任务（用户需求 4）。</summary>
    private async void OnDownloadRetryClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is SnifferDownloadTask task)
        {
            await ViewModel.RetryDownloadAsync(task);
        }
    }

    /// <summary>用户在资源项上右键：弹出“复制真实地址 / 查看详情 / 下载”菜单（用户需求 5）。</summary>
    private void OnResourcesContextRequested(UIElement sender, ContextRequestedEventArgs args)
    {
        try
        {
            if (args.OriginalSource is not FrameworkElement fe) return;
            if (fe.DataContext is not SnifferResourceItem item) return;

            var flyout = new MenuFlyout();

            var miCopy = new MenuFlyoutItem { Text = "复制真实地址", Icon = new FontIcon { Glyph = "\uE8C8" } };
            miCopy.Click += (_, _) =>
            {
                CopyText(item.Url);
                ViewModel.Status = "已复制真实媒体地址。";
            };

            var miDetail = new MenuFlyoutItem { Text = "查看详情（真实地址/类型/大小/请求头）", Icon = new FontIcon { Glyph = "\uE946" } };
            miDetail.Click += async (_, _) => await ShowItemDetailsAsync(item);

            var miDownload = new MenuFlyoutItem { Text = "下载此资源", Icon = new FontIcon { Glyph = "\uE896" } };
            miDownload.Click += async (_, _) => await ViewModel.DownloadResourceAsync(item);

            flyout.Items.Add(miCopy);
            flyout.Items.Add(miDetail);
            flyout.Items.Add(miDownload);

            args.TryGetPosition(fe, out var pos);
            flyout.ShowAt(fe, new FlyoutShowOptions { Position = pos });
            args.Handled = true;
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "显示资源右键菜单失败");
        }
    }

    /// <summary>“查看详情”对话框：展示真实地址、类型/Content-Type、大小、来源页、响应头等。</summary>
    private async Task ShowItemDetailsAsync(SnifferResourceItem item)
    {
        var panel = new StackPanel { Spacing = 10, MinWidth = 360, MaxWidth = 560 };
        panel.Children.Add(InfoRow("名称", item.Title));
        panel.Children.Add(InfoRow("类型", $"{item.KindDisplay}（{(string.IsNullOrWhiteSpace(item.MimeType) ? "未知 Content-Type" : item.MimeType)}）"));
        panel.Children.Add(InfoRow("大小", item.SizeDisplay));
        panel.Children.Add(InfoRow("来源页面", item.PageUrl));
        if (!string.IsNullOrWhiteSpace(item.PageTitle)) panel.Children.Add(InfoRow("网页标题", item.PageTitle));
        panel.Children.Add(InfoRow("嗅探时间", item.DetectedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")));
        if (!string.IsNullOrWhiteSpace(item.HeadersSummary)) panel.Children.Add(InfoRow("响应头", item.HeadersSummary));

        panel.Children.Add(new TextBlock { Text = "真实媒体地址", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        var urlBox = new TextBox
        {
            Text = item.Url,
            IsReadOnly = true,
            TextWrapping = TextWrapping.Wrap,
            AcceptsReturn = true,
            MaxHeight = 160
        };
        panel.Children.Add(urlBox);

        var dialog = new ContentDialog
        {
            Title = "媒体资源详情",
            Content = new ScrollViewer { Content = panel, MaxHeight = 520, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
            PrimaryButtonText = "复制真实地址",
            CloseButtonText = "关闭",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot
        };

        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary)
        {
            CopyText(item.Url);
            ViewModel.Status = "已复制真实媒体地址。";
        }
    }

    /// <summary>构造“标签 + 可选中文本”的一行信息。</summary>
    private static StackPanel InfoRow(string label, string value)
    {
        var sp = new StackPanel { Spacing = 2 };
        sp.Children.Add(new TextBlock { Text = label, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, FontSize = 12, Opacity = 0.85 });
        sp.Children.Add(new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(value) ? "—" : value,
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true
        });
        return sp;
    }

    /// <summary>写入系统剪贴板。</summary>
    private static void CopyText(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        var dp = new DataPackage();
        dp.SetText(text);
        Clipboard.SetContent(dp);
        Clipboard.Flush();
    }

    // ===== 迷你地址栏事件 =====

    private void OnMiniBackClick(object sender, RoutedEventArgs e)
    {
        _webShell.GoBack();
        _logger.Information("Sniffer 迷你地址栏：后退");
    }

    private void OnMiniForwardClick(object sender, RoutedEventArgs e)
    {
        _webShell.GoForward();
        _logger.Information("Sniffer 迷你地址栏：前进");
    }

    private void OnMiniRefreshClick(object sender, RoutedEventArgs e)
    {
        _webShell.Refresh();
        _logger.Information("Sniffer 迷你地址栏：刷新");
    }

    private void OnMiniGoClick(object sender, RoutedEventArgs e) => DoNavigate();

    private void OnMiniUrlKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter) DoNavigate();
    }

    private async void DoNavigate()
    {
        var url = MiniUrlBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(url)) return;
        var ok = await _webShell.NavigateAsync(url);
        if (!ok)
        {
            _logger.Warning("Sniffer 迷你地址栏导航失败：{Url}", url);
        }
        else
        {
            _logger.Information("Sniffer 迷你地址栏导航：{Url}", url);
            // 导航完成后再扫一次 DOM 捕获资源
            _dispatcher.TryEnqueue(DispatcherQueuePriority.Low, async () =>
            {
                await Task.Delay(1500);
                if (ViewModel.ScanCurrentPageCommand.CanExecute(null))
                    await ViewModel.ScanCurrentPageCommand.ExecuteAsync(null);
            });
        }
    }
}
