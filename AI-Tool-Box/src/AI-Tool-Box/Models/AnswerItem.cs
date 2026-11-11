namespace AI_Tool_Box.Models;

/// <summary>题库条目（本机存储 + 局域网共享）。</summary>
public sealed class AnswerItem : IEquatable<AnswerItem>
{
    /// <summary>唯一 ID（Guid）。</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>题号（可选，如 "3"、"2024-T2"）。</summary>
    public string No { get; set; } = string.Empty;

    /// <summary>题干（可多行，作为搜索匹配主体）。</summary>
    public string Question { get; set; } = string.Empty;

    /// <summary>列表显示用题干（未填写时给占位提示）。</summary>
    public string DisplayQuestion =>
        string.IsNullOrWhiteSpace(Question) ? "（未填写题干）" : Question;

    /// <summary>带题号的完整题干（局域网共享与搜索展示用）。</summary>
    public string FullQuestion =>
        string.IsNullOrWhiteSpace(No) ? (Question ?? string.Empty) : $"{No}. {Question}";

    /// <summary>参考答案（可多行，注入到网页焦点输入框的内容）。</summary>
    public string Answer { get; set; } = string.Empty;

    /// <summary>标签（逗号分隔，用于快速筛选）。</summary>
    public string Tags { get; set; } = string.Empty;

    /// <summary>创建时间（UTC）。</summary>
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    /// <summary>最近修改时间（UTC）。</summary>
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;

    /// <summary>来源 Owner（本机名，用于识别局域网分享来源）。</summary>
    public string Owner { get; set; } = Environment.MachineName;

    /// <summary>题干归一化（小写+去空格）用于关键词搜索。</summary>
    public string NormalizedQuestion =>
        (Question ?? string.Empty).ToLowerInvariant().Replace(" ", string.Empty);

    /// <inheritdoc/>
    public bool Equals(AnswerItem? other)
        => other is not null && string.Equals(Id, other.Id, StringComparison.Ordinal);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => Equals(obj as AnswerItem);

    /// <inheritdoc/>
    public override int GetHashCode() => Id?.GetHashCode() ?? 0;
}

/// <summary>局域网匹配命中的答案（附加来源 IP/设备名）。</summary>
public sealed class AnswerMatch
{
    /// <summary>原始条目。</summary>
    public AnswerItem Item { get; init; } = new();

    /// <summary>来源设备名。</summary>
    public string SourceName { get; init; } = string.Empty;

    /// <summary>来源 IPv4 地址。</summary>
    public string SourceIp { get; init; } = string.Empty;

    /// <summary>匹配分数（越高越好），用于排序展示。</summary>
    public int Score { get; set; }

    /// <summary>显示：题目前 60 字符。</summary>
    public string QuestionPreview
    {
        get
        {
            var q = Item.Question ?? string.Empty;
            return q.Length <= 80 ? q : q[..80] + "…";
        }
    }
}

/// <summary>黑名单/白名单模式。</summary>
public enum ListMode
{
    /// <summary>不限制，除黑名单 IP 外均可访问。</summary>
    Blacklist = 0,
    /// <summary>只允许白名单 IP 访问。</summary>
    Whitelist = 1
}

/// <summary>题库分享设置。</summary>
public sealed class AnswerShareSettings
{
    /// <summary>IP 访问控制模式。</summary>
    public ListMode Mode { get; set; } = ListMode.Blacklist;

    /// <summary>黑名单（Mode=Blacklist 时禁止访问本机题库）。</summary>
    public List<string> Blacklist { get; set; } = new();

    /// <summary>白名单（Mode=Whitelist 时仅允许访问本机题库）。</summary>
    public List<string> Whitelist { get; set; } = new();

    /// <summary>启动时自动开启共享。</summary>
    public bool AutoStartSharing { get; set; } = false;

    /// <summary>搜索结果每台设备最大返回条数。</summary>
    public int MaxMatchesPerPeer { get; set; } = 5;
}
