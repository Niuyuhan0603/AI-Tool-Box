using System.Text.Json;
using Serilog;

namespace AI_Tool_Box.Services;

/// <summary>应用集中配置契约：各功能线程数 + 通用参数，持久化到 app_settings.json（与 FfmpegLocator 共享同一文件）。</summary>
public interface IAppSettingsService
{
    /// <summary>嗅探器并发过滤线程数（1~处理器数）。</summary>
    int SnifferThreadCount { get; set; }
    /// <summary>文件转换并发任务数（1~处理器数*2）。</summary>
    int TransformThreadCount { get; set; }
    /// <summary>OCR 并发线程数（1~处理器数；LiteParse2.0 的 --num-workers）。</summary>
    int OcrThreadCount { get; set; }
    /// <summary>局域网传输并发上传流数（1~16）。</summary>
    int LanTransferThreadCount { get; set; }
    /// <summary>下载分片并发数（1~32）。</summary>
    int DownloadThreadCount { get; set; }
    /// <summary>VPN 默认服务端节点标识（VPN 模块就绪后生效）。</summary>
    string? VpnDefaultNode { get; set; }
    /// <summary>关闭窗口行为：tray=缩小至托盘（默认，后台进程不停止），exit=直接退出程序。</summary>
    string CloseBehavior { get; set; }
    /// <summary>设置变更时触发（UI 订阅以刷新）。</summary>
    event EventHandler? Changed;
}

/// <summary>应用集中配置实现：线程安全读写，落盘 app_settings.json（合并保留其它键如 ffmpegPath）。</summary>
public sealed class AppSettingsService : IAppSettingsService
{
    private static readonly string StoreDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AI-Tool-Box");
    private static readonly string StorePath = Path.Combine(StoreDir, "app_settings.json");

    private readonly ILogger _logger;
    private readonly object _gate = new();
    private readonly Dictionary<string, JsonElement> _store = new(StringComparer.Ordinal);
    private bool _loaded;

    // 合理默认值：嗅探4 / 转换2 / OCR2 / 传输4 / 下载8
    public AppSettingsService(ILogger logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        EnsureLoaded();
    }

    public event EventHandler? Changed;

    private static int Clamp(int v, int min, int max) => Math.Clamp(v, min, max);
    private static int ProcessorCap => Math.Max(1, Environment.ProcessorCount);

    public int SnifferThreadCount
    {
        get => ReadInt("snifferThreads", 4, 1, ProcessorCap);
        set { WriteInt("snifferThreads", Clamp(value, 1, ProcessorCap)); Raise(); }
    }
    public int TransformThreadCount
    {
        get => ReadInt("transformThreads", 2, 1, ProcessorCap * 2);
        set { WriteInt("transformThreads", Clamp(value, 1, ProcessorCap * 2)); Raise(); }
    }
    public int OcrThreadCount
    {
        get => ReadInt("ocrThreads", 2, 1, ProcessorCap);
        set { WriteInt("ocrThreads", Clamp(value, 1, ProcessorCap)); Raise(); }
    }
    public int LanTransferThreadCount
    {
        get => ReadInt("lanTransferThreads", 4, 1, 16);
        set { WriteInt("lanTransferThreads", Clamp(value, 1, 16)); Raise(); }
    }
    public int DownloadThreadCount
    {
        get => ReadInt("downloadThreads", 8, 1, 32);
        set { WriteInt("downloadThreads", Clamp(value, 1, 32)); Raise(); }
    }
    public string? VpnDefaultNode
    {
        get => ReadString("vpnDefaultNode");
        set { WriteString("vpnDefaultNode", value); Raise(); }
    }

    /// <summary>关闭窗口行为：默认缩小至托盘（tray）；exit 为直接退出。</summary>
    public string CloseBehavior
    {
        get => ReadString("closeBehavior") ?? "tray";
        set { WriteString("closeBehavior", value == "exit" ? "exit" : "tray"); Raise(); }
    }

    private int ReadInt(string key, int def, int min, int max)
    {
        lock (_gate)
        {
            EnsureLoaded();
            if (_store.TryGetValue(key, out var el) && el.ValueKind == JsonValueKind.Number
                && el.TryGetInt32(out var v))
                return Clamp(v, min, max);
            return def;
        }
    }
    private void WriteInt(string key, int value)
    {
        lock (_gate)
        {
            EnsureLoaded();
            _store[key] = JsonSerializer.SerializeToElement(value);
            Save();
        }
    }
    private string? ReadString(string key)
    {
        lock (_gate)
        {
            EnsureLoaded();
            if (_store.TryGetValue(key, out var el) && el.ValueKind == JsonValueKind.String)
                return el.GetString();
            return null;
        }
    }
    private void WriteString(string key, string? value)
    {
        lock (_gate)
        {
            EnsureLoaded();
            if (string.IsNullOrWhiteSpace(value)) _store.Remove(key);
            else _store[key] = JsonSerializer.SerializeToElement(value);
            Save();
        }
    }

    private void Raise() => Changed?.Invoke(this, EventArgs.Empty);

    private void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;
        try
        {
            if (!File.Exists(StorePath)) return;
            var existing = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(StorePath));
            if (existing is not null) _store.EnsureCapacity(existing.Count);
            foreach (var p in existing ?? Enumerable.Empty<KeyValuePair<string, JsonElement>>())
                _store[p.Key] = p.Value;
            _logger.Information("应用配置已从 app_settings.json 加载：{Count} 项", _store.Count);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "app_settings.json 读取失败，按默认配置处理");
        }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(StoreDir);
            File.WriteAllText(StorePath, JsonSerializer.Serialize(_store, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "app_settings.json 写入失败");
        }
    }
}
