using CommunityToolkit.Mvvm.ComponentModel;
using System.Collections.ObjectModel;

namespace AI_Tool_Box.Services;

/// <summary>嗅探到的媒体资源类型。</summary>
public enum SnifferResourceKind
{
    /// <summary>未知。</summary>
    Unknown = 0,
    /// <summary>视频（mp4/webm/ts/...）。</summary>
    Video = 1,
    /// <summary>m3u8 HLS 播放列表（分片流）。</summary>
    M3u8 = 2,
    /// <summary>音频（mp3/wav/flac/aac/...）。</summary>
    Audio = 3,
    /// <summary>字幕（vtt/srt/ass）。</summary>
    Subtitle = 4
}

/// <summary>嗅探到的媒体资源项。</summary>
public sealed class SnifferResourceItem : ObservableObject
{
    private string _url = string.Empty;
    private SnifferResourceKind _kind;
    private string _title = string.Empty;
    private string _mimeType = string.Empty;
    private long _sizeBytes;
    private string _pageUrl = string.Empty;
    private string _extension = string.Empty;
    private string _pageTitle = string.Empty;
    private string _headersSummary = string.Empty;
    private DateTime _detectedAtUtc = DateTime.UtcNow;

    public string Url { get => _url; set => SetProperty(ref _url, value); }
    public SnifferResourceKind Kind { get => _kind; set => SetProperty(ref _kind, value); }

    /// <summary>显示名称：优先使用嗅探时的网页标题（用户需求 1），必要时追加扩展名/序号避免重名。</summary>
    public string Title { get => _title; set => SetProperty(ref _title, value); }
    public string MimeType { get => _mimeType; set => SetProperty(ref _mimeType, value); }
    public long SizeBytes { get => _sizeBytes; set => SetProperty(ref _sizeBytes, value); }
    public string PageUrl { get => _pageUrl; set => SetProperty(ref _pageUrl, value); }

    /// <summary>媒体地址的真实扩展名（不含点），用于下载时补全文件名。</summary>
    public string Extension { get => _extension; set => SetProperty(ref _extension, value); }

    /// <summary>嗅探到该资源时所在的网页标题（原始），供"查看详情"展示。</summary>
    public string PageTitle { get => _pageTitle; set => SetProperty(ref _pageTitle, value); }

    /// <summary>响应头摘要（Content-Type / Content-Length / Content-Disposition 等），供"查看详情"展示。</summary>
    public string HeadersSummary { get => _headersSummary; set => SetProperty(ref _headersSummary, value); }

    public DateTime DetectedAtUtc { get => _detectedAtUtc; set => SetProperty(ref _detectedAtUtc, value); }

    /// <summary>用于显示的类型文字。</summary>
    public string KindDisplay => Kind switch
    {
        SnifferResourceKind.Video => "视频",
        SnifferResourceKind.M3u8 => "HLS(m3u8)",
        SnifferResourceKind.Audio => "音频",
        SnifferResourceKind.Subtitle => "字幕",
        _ => "未知"
    };

    public string SizeDisplay
    {
        get
        {
            if (SizeBytes <= 0) return "—";
            string[] suf = { "B", "KB", "MB", "GB", "TB" };
            double s = SizeBytes;
            int i = 0;
            while (s >= 1024 && i < suf.Length - 1) { s /= 1024; i++; }
            return $"{s:0.##} {suf[i]}";
        }
    }
}

/// <summary>下载任务（UI 进度绑定）。</summary>
public sealed class SnifferDownloadTask : ObservableObject
{
    private string _title = string.Empty;
    private string _url = string.Empty;
    private string _referer = string.Empty;
    private long _totalBytes;
    private long _receivedBytes;
    private double _progress; // 0..1
    private string _status = "等待中";
    private string _localPath = string.Empty;
    private bool _isCompleted;
    private bool _isFailed;

    public string Title { get => _title; set => SetProperty(ref _title, value); }
    public string Url { get => _url; set => SetProperty(ref _url, value); }

    /// <summary>下载时携带的 Referer（资源来源页面），修复防盗链站点下载失败。</summary>
    public string Referer { get => _referer; set => SetProperty(ref _referer, value); }
    public long TotalBytes { get => _totalBytes; set { SetProperty(ref _totalBytes, value); OnPropertyChanged(nameof(TotalDisplay)); } }
    public long ReceivedBytes { get => _receivedBytes; set { SetProperty(ref _receivedBytes, value); OnPropertyChanged(nameof(ReceivedDisplay)); OnPropertyChanged(nameof(ProgressPercent)); OnPropertyChanged(nameof(ProgressPercentDisplay)); } }
    public double Progress { get => _progress; set { SetProperty(ref _progress, value); OnPropertyChanged(nameof(ProgressPercent)); OnPropertyChanged(nameof(ProgressPercentDisplay)); } }
    public string Status { get => _status; set => SetProperty(ref _status, value); }
    public string LocalPath { get => _localPath; set => SetProperty(ref _localPath, value); }
    public bool IsCompleted { get => _isCompleted; set => SetProperty(ref _isCompleted, value); }
    public bool IsFailed { get => _isFailed; set => SetProperty(ref _isFailed, value); }

    public int ProgressPercent => (int)Math.Round(Progress * 100.0);
    public string ProgressPercentDisplay => $"{ProgressPercent}%";
    public string ReceivedDisplay => BytesDisplay(ReceivedBytes);
    public string TotalDisplay => BytesDisplay(TotalBytes);

    private static string BytesDisplay(long n)
    {
        if (n <= 0) return "—";
        string[] suf = { "B", "KB", "MB", "GB", "TB" };
        double s = n; int i = 0;
        while (s >= 1024 && i < suf.Length - 1) { s /= 1024; i++; }
        return $"{s:0.##} {suf[i]}";
    }
}

/// <summary>媒体嗅探服务契约。</summary>
public interface ISnifferService
{
    /// <summary>嗅探到的资源（UI 绑定）。</summary>
    ObservableCollection<SnifferResourceItem> Resources { get; }

    /// <summary>下载任务列表。</summary>
    ObservableCollection<SnifferDownloadTask> Downloads { get; }

    /// <summary>检测到资源时触发（用于 UI 弹小提示）。</summary>
    event EventHandler<SnifferResourceItem>? ResourceDetected;

    /// <summary>用户点击网页内"已捕捉到视频"悬浮按钮时触发（用于切到资源列表）。</summary>
    event EventHandler? OpenSnifferRequested;

    /// <summary>开始嗅探：WebResourceRequested 拦截 + DOM 扫描。</summary>
    void StartSniffing();

    /// <summary>停止嗅探。</summary>
    void StopSniffing();

    /// <summary>立即对当前活动网页执行一次 DOM 扫描（video/audio/source/src）。</summary>
    Task<int> ScanCurrentPageAsync();

    /// <summary>清空已嗅探列表。</summary>
    void ClearResources();

    /// <summary>下载某个资源（mp4/mp3 单文件直下；m3u8 分片下载合并；其他文件直下）。</summary>
    /// <param name="item">要下载的资源。</param>
    /// <param name="saveDir">保存目录（默认 Downloads\AI-Tool-Box\Sniffer）。</param>
    /// <param name="m3u8Concurrency">m3u8 分片并发数（默认 32）。</param>
    /// <returns>返回任务实例（可用于 UI 进度跟踪）。</returns>
    Task<SnifferDownloadTask> DownloadAsync(SnifferResourceItem item, string? saveDir = null, int m3u8Concurrency = 32);
}
