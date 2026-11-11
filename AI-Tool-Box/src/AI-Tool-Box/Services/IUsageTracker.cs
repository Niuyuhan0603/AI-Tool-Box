namespace AI_Tool_Box.Services;

/// <summary>用量追踪器契约：统计 API token 消耗与 VPN 流量，持久化到 usage.json。</summary>
public interface IUsageTracker
{
    /// <summary>累计 API token 用量（按模型聚合）。</summary>
    IReadOnlyDictionary<string, TokenUsage> TokenByModel { get; }

    /// <summary>累计 API 调用次数。</summary>
    long ApiCallCount { get; }

    /// <summary>累计 VPN 上行字节。</summary>
    long VpnBytesSent { get; }

    /// <summary>累计 VPN 下行字节。</summary>
    long VpnBytesReceived { get; }

    /// <summary>最近一次刷新时间（UTC）。</summary>
    DateTime LastUpdatedUtc { get; }

    /// <summary>记录一次 API 调用的 token 用量（线程安全）。</summary>
    /// <param name="model">模型标识。</param>
    /// <param name="promptTokens">提示词 token 数。</param>
    /// <param name="completionTokens">补全 token 数。</param>
    void RecordApiCall(string model, int promptTokens, int completionTokens);

    /// <summary>记录 VPN 流量（连接活动期间累计调用）。</summary>
    /// <param name="bytesSent">上行字节。</param>
    /// <param name="bytesReceived">下行字节。</param>
    void RecordVpnTraffic(long bytesSent, long bytesReceived);

    /// <summary>重置全部统计（清空 usage.json）。</summary>
    void Reset();

    /// <summary>强制持久化到磁盘。</summary>
    void Flush();
}

/// <summary>单个模型的 token 用量。</summary>
public sealed record TokenUsage
{
    public long PromptTokens { get; set; }
    public long CompletionTokens { get; set; }
    public long TotalTokens => PromptTokens + CompletionTokens;
    public long Calls { get; set; }
}
