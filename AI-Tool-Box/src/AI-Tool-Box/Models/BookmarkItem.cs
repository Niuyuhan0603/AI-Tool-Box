namespace AI_Tool_Box.Models;

/// <summary>书签项。</summary>
public sealed class BookmarkItem
{
    /// <summary>标题。</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>URL。</summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>创建时间（ISO 8601）。</summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>历史记录项。</summary>
public sealed class HistoryItem
{
    /// <summary>标题。</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>URL。</summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>访问时间（ISO 8601）。</summary>
    public DateTime VisitedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>下载记录项。</summary>
public sealed class DownloadItem
{
    /// <summary>文件名。</summary>
    public string FileName { get; set; } = string.Empty;

    /// <summary>下载 URL。</summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>本地保存路径。</summary>
    public string LocalPath { get; set; } = string.Empty;

    /// <summary>文件大小（字节）。</summary>
    public long Size { get; set; }

    /// <summary>下载状态。</summary>
    public DownloadState State { get; set; }

    /// <summary>开始时间。</summary>
    public DateTime StartedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>下载状态枚举。</summary>
public enum DownloadState
{
    /// <summary>进行中。</summary>
    InProgress,
    /// <summary>已完成。</summary>
    Completed,
    /// <summary>已取消。</summary>
    Cancelled,
    /// <summary>已失败。</summary>
    Failed
}
