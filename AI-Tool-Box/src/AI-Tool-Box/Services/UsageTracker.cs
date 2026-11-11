using System.Collections.Concurrent;
using System.Text.Json;
using Serilog;

namespace AI_Tool_Box.Services;

/// <summary>用量追踪器实现：线程安全记录 API token 与 VPN 流量，持久化到 %LOCALAPPDATA%\AI-Tool-Box\usage.json。
/// 内存统计 + 延迟落盘（写入时合并落盘，避免高频 IO）。重启后从 usage.json 恢复累计值。</summary>
public sealed class UsageTracker : IUsageTracker
{
    private static readonly string StoreDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AI-Tool-Box");
    private static readonly string StorePath = Path.Combine(StoreDir, "usage.json");

    private readonly ILogger _logger;
    private readonly object _gate = new();
    private readonly ConcurrentDictionary<string, TokenUsage> _tokenByModel = new(StringComparer.Ordinal);
    private long _apiCallCount;
    private long _vpnSent;
    private long _vpnRecv;
    private DateTime _lastUpdatedUtc;
    private bool _loaded;

    public UsageTracker(ILogger logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        EnsureLoaded();
    }

    public IReadOnlyDictionary<string, TokenUsage> TokenByModel => _tokenByModel;
    public long ApiCallCount => Interlocked.Read(ref _apiCallCount);
    public long VpnBytesSent => Interlocked.Read(ref _vpnSent);
    public long VpnBytesReceived => Interlocked.Read(ref _vpnRecv);
    public DateTime LastUpdatedUtc => _lastUpdatedUtc;

    public void RecordApiCall(string model, int promptTokens, int completionTokens)
    {
        if (promptTokens < 0) promptTokens = 0;
        if (completionTokens < 0) completionTokens = 0;
        var key = string.IsNullOrWhiteSpace(model) ? "unknown" : model;
        var usage = _tokenByModel.GetOrAdd(key, _ => new TokenUsage());
        lock (usage)
        {
            usage.PromptTokens += promptTokens;
            usage.CompletionTokens += completionTokens;
            usage.Calls++;
        }
        Interlocked.Increment(ref _apiCallCount);
        _lastUpdatedUtc = DateTime.UtcNow;
        // 每次记录都落盘：API 调用频率不高，保证统计不丢
        Flush();
    }

    public void RecordVpnTraffic(long bytesSent, long bytesReceived)
    {
        if (bytesSent > 0) Interlocked.Add(ref _vpnSent, bytesSent);
        if (bytesReceived > 0) Interlocked.Add(ref _vpnRecv, bytesReceived);
        _lastUpdatedUtc = DateTime.UtcNow;
        Flush();
    }

    public void Reset()
    {
        lock (_gate)
        {
            _tokenByModel.Clear();
            Interlocked.Exchange(ref _apiCallCount, 0);
            Interlocked.Exchange(ref _vpnSent, 0);
            Interlocked.Exchange(ref _vpnRecv, 0);
            _lastUpdatedUtc = DateTime.UtcNow;
            try { if (File.Exists(StorePath)) File.Delete(StorePath); } catch { /* 忽略 */ }
            _logger.Information("用量统计已重置");
        }
    }

    public void Flush()
    {
        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(StoreDir);
                var dto = new
                {
                    apiCallCount = ApiCallCount,
                    vpnBytesSent = VpnBytesSent,
                    vpnBytesReceived = VpnBytesReceived,
                    lastUpdatedUtc = _lastUpdatedUtc,
                    tokenByModel = _tokenByModel.ToDictionary(p => p.Key, p => new
                    {
                        promptTokens = p.Value.PromptTokens,
                        completionTokens = p.Value.CompletionTokens,
                        calls = p.Value.Calls
                    })
                };
                File.WriteAllText(StorePath, JsonSerializer.Serialize(dto, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "usage.json 持久化失败");
            }
        }
    }

    private void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;
        try
        {
            if (!File.Exists(StorePath)) return;
            using var doc = JsonDocument.Parse(File.ReadAllText(StorePath));
            var root = doc.RootElement;
            if (root.TryGetProperty("apiCallCount", out var ac) && ac.TryGetInt64(out var c))
                _apiCallCount = c;
            if (root.TryGetProperty("vpnBytesSent", out var s) && s.TryGetInt64(out var sv))
                _vpnSent = sv;
            if (root.TryGetProperty("vpnBytesReceived", out var r) && r.TryGetInt64(out var rv))
                _vpnRecv = rv;
            if (root.TryGetProperty("lastUpdatedUtc", out var d) && d.ValueKind == JsonValueKind.String
                && DateTime.TryParse(d.GetString(), out var dt))
                _lastUpdatedUtc = dt;
            if (root.TryGetProperty("tokenByModel", out var tbm) && tbm.ValueKind == JsonValueKind.Object)
            {
                foreach (var p in tbm.EnumerateObject())
                {
                    var u = new TokenUsage();
                    if (p.Value.TryGetProperty("promptTokens", out var pt) && pt.TryGetInt64(out var ptv))
                        u.PromptTokens = ptv;
                    if (p.Value.TryGetProperty("completionTokens", out var ct) && ct.TryGetInt64(out var ctv))
                        u.CompletionTokens = ctv;
                    if (p.Value.TryGetProperty("calls", out var cl) && cl.TryGetInt64(out var clv))
                        u.Calls = clv;
                    _tokenByModel[p.Name] = u;
                }
            }
            _logger.Information("用量统计已从 usage.json 恢复：API 调用 {Calls} 次，VPN ↑{Up} ↓{Down}",
                _apiCallCount, _vpnSent, _vpnRecv);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "usage.json 读取失败，按空统计处理");
        }
    }
}
