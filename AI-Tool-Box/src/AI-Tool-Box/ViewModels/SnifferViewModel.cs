using AI_Tool_Box.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;
using System.Collections.ObjectModel;

namespace AI_Tool_Box.ViewModels;

/// <summary>嗅探器视图模型：开始/停止嗅探、DOM 扫描、下载选中。</summary>
public partial class SnifferViewModel : ObservableObject
{
    private readonly ILogger _logger;
    private readonly ISnifferService _sniffer;
    private readonly IWebShellService _web;

    public ObservableCollection<SnifferResourceItem> Resources => _sniffer.Resources;
    public ObservableCollection<SnifferDownloadTask> Downloads => _sniffer.Downloads;

    [ObservableProperty] private bool _isSniffingOn;
    [ObservableProperty] private bool _isScanningPage;
    [ObservableProperty] private string _status = "嗅探器：打开视频/音频页面后，点击“开始嗅探”即可自动捕获资源；或手动点“扫描当前页”。";
    [ObservableProperty] private SnifferResourceItem? _selectedResource;
    [ObservableProperty] private int _m3u8Concurrency = 32;

    public SnifferViewModel(ILogger logger, ISnifferService sniffer, IWebShellService web)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _sniffer = sniffer ?? throw new ArgumentNullException(nameof(sniffer));
        _web = web ?? throw new ArgumentNullException(nameof(web));
        _sniffer.ResourceDetected += OnResourceDetected;
    }

    private void OnResourceDetected(object? sender, SnifferResourceItem e)
    {
        _ = e;
        // 简单更新提示（不阻塞嗅探线程）
        _ = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread()?.TryEnqueue(() =>
        {
            var v = Resources.Count;
            Status = $"已嗅探 {v} 条资源。选中后点击下载；m3u8 自动分片下载合并为 .ts。";
        });
    }

    [RelayCommand]
    private void ToggleSniffing()
    {
        if (!IsSniffingOn)
        {
            _sniffer.StartSniffing();
            IsSniffingOn = true;
            Status = "嗅探已开启：切换/加载网页时自动捕获 mp4/m3u8/mp3 等资源。点击“扫描当前页”立即分析 DOM 中的 video/audio src。";
            _logger.Information("用户开启嗅探");
        }
        else
        {
            _sniffer.StopSniffing();
            IsSniffingOn = false;
            Status = "嗅探已关闭。";
            _logger.Information("用户关闭嗅探");
        }
    }

    private bool CanScanPage => !IsScanningPage && _web.IsAvailable;

    [RelayCommand(CanExecute = nameof(CanScanPage))]
    private async Task ScanCurrentPage()
    {
        IsScanningPage = true;
        try
        {
            Status = "扫描当前页 DOM 中的媒体元素…";
            int n = await _sniffer.ScanCurrentPageAsync();
            Status = n == 0
                ? "扫描完成：当前页 DOM 未找到新的媒体资源。试试点击“开始嗅探”再刷新页面（网络层拦截更全）。"
                : $"扫描完成：新增 {n} 条媒体资源。";
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "扫描失败");
            Status = $"扫描失败：{ex.Message}";
        }
        finally
        {
            IsScanningPage = false;
        }
    }

    [RelayCommand]
    private void ClearAll()
    {
        _sniffer.ClearResources();
        Status = "已清空嗅探资源列表。";
    }

    private bool CanDownload => SelectedResource is not null;

    [RelayCommand(CanExecute = nameof(CanDownload))]
    private async Task DownloadSelected()
    {
        if (SelectedResource is null) return;
        await DownloadResourceAsync(SelectedResource);
    }

    /// <summary>下载指定资源（供右侧列表每项的"下载"按钮 / 右键菜单调用）。</summary>
    public async Task DownloadResourceAsync(SnifferResourceItem item)
    {
        if (item is null) return;
        try
        {
            Status = $"开始下载：{item.Title}";
            var task = await _sniffer.DownloadAsync(item, m3u8Concurrency: Math.Max(1, M3u8Concurrency));
            Status = task.IsCompleted
                ? $"下载完成：{Path.GetFileName(task.LocalPath)}"
                : $"已加入下载任务：{item.Title}。请在右侧“下载任务”查看进度。";
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "下载失败");
            Status = $"下载失败：{ex.Message}";
        }
    }

    /// <summary>重试：重新下载该资源（用户需求 4，用于失败的下载）。</summary>
    public Task RetryResourceAsync(SnifferResourceItem item)
    {
        if (item is null) return Task.CompletedTask;
        Status = $"重试下载：{item.Title}";
        return DownloadResourceAsync(item);
    }

    /// <summary>重试失败的下载任务（用户需求 4）：由任务信息还原资源再次下载。</summary>
    public Task RetryDownloadAsync(SnifferDownloadTask task)
    {
        if (task is null) return Task.CompletedTask;
        var kind = SnifferService.ClassifyByUrl(task.Url, out var ext);
        var item = new SnifferResourceItem
        {
            Url = task.Url,
            Title = task.Title,
            Kind = kind,
            Extension = ext,
            PageUrl = task.Referer
        };
        Status = $"重试下载：{task.Title}";
        return DownloadResourceAsync(item);
    }

    partial void OnSelectedResourceChanged(SnifferResourceItem? value)
        => DownloadSelectedCommand.NotifyCanExecuteChanged();

    partial void OnIsScanningPageChanged(bool value)
        => ScanCurrentPageCommand.NotifyCanExecuteChanged();
}
