using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Dispatching;

namespace AI_Tool_Box.Services;

/// <summary>局域网在线设备（字段对齐 LocalSend 协议：alias / fingerprint / port / deviceType）。</summary>
public sealed class LanPeer : IEquatable<LanPeer>
{
    /// <summary>初始化设备信息。</summary>
    public LanPeer(string alias, string ipAddress, int port, string fingerprint, string deviceType,
        string version = "2.1", bool https = false)
    {
        IpAddress = ipAddress ?? throw new ArgumentNullException(nameof(ipAddress));
        Port = port;
        Alias = string.IsNullOrWhiteSpace(alias) ? IpAddress : alias;
        Fingerprint = fingerprint ?? string.Empty;
        DeviceType = string.IsNullOrWhiteSpace(deviceType) ? "desktop" : deviceType;
        Version = string.IsNullOrWhiteSpace(version) ? "1.0" : version;
        Https = https;
    }

    /// <summary>设备别名（LocalSend：alias）。</summary>
    public string Alias { get; }

    /// <summary>IPv4 地址。</summary>
    public string IpAddress { get; }

    /// <summary>HTTP 传输端口。</summary>
    public int Port { get; }

    /// <summary>设备指纹（持久化 GUID，用于去重/识别自己）。</summary>
    public string Fingerprint { get; }

    /// <summary>设备类型（desktop / mobile / tablet…）。</summary>
    public string DeviceType { get; }

    /// <summary>对端协议版本（1.0 使用 v1 路由；2.x 使用 v2 路由）。</summary>
    public string Version { get; internal set; }

    /// <summary>对端是否使用 HTTPS（LocalSend「加密」开关开启时为 true）。</summary>
    public bool Https { get; internal set; }

    /// <summary>传输基地址，如 http://192.168.1.2:53317。</summary>
    public string BaseUrl => $"{(Https ? "https" : "http")}://{IpAddress}:{Port}";

    /// <summary>最后一次收到广播/注册的时间（UTC）。</summary>
    public DateTime LastSeenUtc { get; set; } = DateTime.UtcNow;

    /// <summary>友好显示。</summary>
    public string Display => $"{Alias}（{IpAddress}:{Port}{(Https ? " · HTTPS" : string.Empty)}）";

    /// <summary>设备类型图标字符（Segoe Fluent Icons）。</summary>
    public string Glyph => DeviceType.Equals("mobile", StringComparison.OrdinalIgnoreCase) ||
                           DeviceType.Equals("phone", StringComparison.OrdinalIgnoreCase)
        ? "\uE8EA"   // CellPhone
        : "\uE7F8";  // Devices2 / desktop

    /// <inheritdoc/>
    public bool Equals(LanPeer? other)
    {
        if (other is null) return false;
        if (!string.IsNullOrEmpty(Fingerprint) && !string.IsNullOrEmpty(other.Fingerprint))
            return string.Equals(Fingerprint, other.Fingerprint, StringComparison.OrdinalIgnoreCase);
        return string.Equals(IpAddress, other.IpAddress, StringComparison.OrdinalIgnoreCase) && Port == other.Port;
    }

    /// <inheritdoc/>
    public override bool Equals(object? obj) => Equals(obj as LanPeer);

    /// <inheritdoc/>
    public override int GetHashCode() =>
        string.IsNullOrEmpty(Fingerprint)
            ? HashCode.Combine(IpAddress, Port)
            : Fingerprint.GetHashCode(StringComparison.OrdinalIgnoreCase);
}

/// <summary>传输进度信息（发送与接收共用；后台线程更新属性，UI 绑定刷新）。</summary>
public sealed class TransferProgress : ObservableObject
{
    private long _totalBytes;
    private double _percent;
    private string _status = "等待中…";
    private string _speedText = string.Empty;
    private bool _isCompleted;
    private string? _sha256Result;

    private readonly Queue<(DateTime Utc, long Bytes)> _samples = new();
    private int _lastNotifiedPercent = -1;
    private DateTime _lastSpeedUpdate = DateTime.MinValue;

    /// <summary>
    /// 属性变更通知必须回到 UI 线程：进度回调发生在 HTTP 上传流的后台线程，
    /// 直接触发 PropertyChanged 会让 x:Bind 在非 UI 线程写 ProgressBar，抛 COMException 0x8001010E。
    /// </summary>
    private void RaiseUi(string propertyName)
    {
        var queue = App.MainWindow?.DispatcherQueue;
        if (queue is null || queue.HasThreadAccess)
        {
            OnPropertyChanged(propertyName);
            return;
        }
        queue.TryEnqueue(() => OnPropertyChanged(propertyName));
    }

    /// <summary>任务 ID。</summary>
    public string TaskId { get; init; } = Guid.NewGuid().ToString("N");

    /// <summary>发送或接收。</summary>
    public bool IsSend { get; init; }

    /// <summary>方向显示文本。</summary>
    public string DirectionText => IsSend ? "发→" : "收←";

    /// <summary>对端显示（对方设备别名+IP）。</summary>
    public string PeerDisplay { get; init; } = "未知";

    /// <summary>文件名。</summary>
    public string FileName { get; init; } = string.Empty;

    /// <summary>文件大小（字节）。</summary>
    public long TotalBytes
    {
        get => _totalBytes;
        set { _totalBytes = value; RaiseUi(nameof(TotalBytes)); UpdatePercent(); }
    }

    /// <summary>已传输字节（通过 ReportProgress 更新）。</summary>
    public long BytesTransferred { get; private set; }

    /// <summary>进度百分比 0~100。</summary>
    public double Percent
    {
        get => _percent;
        private set { _percent = value; RaiseUi(nameof(Percent)); }
    }

    /// <summary>状态文本。</summary>
    public string Status
    {
        get => _status;
        set { _status = value; RaiseUi(nameof(Status)); }
    }

    /// <summary>实时速度文本（如 "3.21 MB/s"）。</summary>
    public string SpeedText
    {
        get => _speedText;
        private set { _speedText = value; RaiseUi(nameof(SpeedText)); }
    }

    /// <summary>是否完成（成功/失败/取消）。</summary>
    public bool IsCompleted
    {
        get => _isCompleted;
        set { _isCompleted = value; RaiseUi(nameof(IsCompleted)); }
    }

    /// <summary>SHA256 校验结果描述。</summary>
    public string? Sha256Result
    {
        get => _sha256Result;
        set { _sha256Result = value; RaiseUi(nameof(Sha256Result)); }
    }

    private Action? _cancelHandler;

    /// <summary>注册取消处理器（由服务在发起传输时设置）。</summary>
    public void SetCancelHandler(Action handler) => _cancelHandler = handler;

    /// <summary>尝试取消本条传输（无处理器时忽略）。</summary>
    public void TryCancel()
    {
        try { _cancelHandler?.Invoke(); }
        catch { /* 忽略 */ }
    }

    /// <summary>上报进度：更新字节、百分比与滑动窗口速度。</summary>
    public void ReportProgress(long bytes)
    {
        BytesTransferred = bytes;
        UpdatePercent();

        var now = DateTime.UtcNow;
        _samples.Enqueue((now, bytes));
        while (_samples.Count > 1 && (now - _samples.Peek().Utc).TotalMilliseconds > 2000)
        {
            _samples.Dequeue();
        }
        if (_samples.Count > 1)
        {
            var (t0, b0) = _samples.Peek();
            var dt = (now - t0).TotalSeconds;
            // 速度文本每 250ms 最多刷新一次（同样要回 UI 线程，且不宜跟随每次回调刷屏）
            if (dt > 0.2 && (now - _lastSpeedUpdate).TotalMilliseconds > 250)
            {
                _lastSpeedUpdate = now;
                SpeedText = $"{FormatSize((long)((bytes - b0) / dt))}/s";
            }
        }
    }

    private void UpdatePercent()
    {
        var value = TotalBytes <= 0 ? 0 : Math.Clamp(100.0 * BytesTransferred / TotalBytes, 0, 100);
        // 进度回调极频繁（每个缓冲区一次），按 1% 合并通知，避免刷爆 UI 派发队列
        var whole = (int)value;
        if (whole == _lastNotifiedPercent)
        {
            _percent = value;
            return;
        }
        _lastNotifiedPercent = whole;
        Percent = value;
    }

    /// <summary>字节大小格式化。</summary>
    public static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F2} KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F2} MB";
        return $"{bytes / (1024.0 * 1024 * 1024):F2} GB";
    }
}

/// <summary>待发送文件条目（UI 列表用）。</summary>
public sealed record PendingFile(string Path, string Name, long Size)
{
    /// <summary>大小文本。</summary>
    public string SizeText => TransferProgress.FormatSize(Size);

    /// <summary>显示文本。</summary>
    public string Display => $"{Name}（{SizeText}）";
}

/// <summary>接收方收到的单个文件元数据（弹窗展示用）。</summary>
public sealed record LanIncomingFile(string FileId, string FileName, long Size);

/// <summary>接收方收到的传输请求（借鉴 LocalSend prepare-upload 语义；调用 Respond 决定接受/拒绝）。</summary>
public sealed class LanIncomingRequest
{
    private readonly TaskCompletionSource<bool> _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>会话 ID。</summary>
    public string SessionId { get; init; } = string.Empty;

    /// <summary>请求方显示（别名+IP）。</summary>
    public string RequesterDisplay { get; init; } = "未知设备";

    /// <summary>请求方 IP。</summary>
    public string RequesterIp { get; init; } = string.Empty;

    /// <summary>文件列表。</summary>
    public IReadOnlyList<LanIncomingFile> Files { get; init; } = Array.Empty<LanIncomingFile>();

    /// <summary>响应请求（仅第一次有效）。</summary>
    public void Respond(bool accept) => _tcs.TrySetResult(accept);

    /// <summary>等待响应；超时抛 TimeoutException。</summary>
    internal Task<bool> WaitResponseAsync(TimeSpan timeout) => _tcs.Task.WaitAsync(timeout);
}

/// <summary>局域网传输服务（借鉴 LocalSend：UDP 多播发现 + HTTP REST 会话式传输）。</summary>
public interface ILanTransferService : IDisposable
{
    /// <summary>本机 HTTP 监听端口（默认 53317，冲突时 +1 搜索）。</summary>
    int LocalPort { get; }

    /// <summary>本机 IPv4。</summary>
    string LocalIpAddress { get; }

    /// <summary>本机别名（可编辑并持久化）。</summary>
    string Alias { get; set; }

    /// <summary>本机设备指纹（持久化 GUID）。</summary>
    string Fingerprint { get; }

    /// <summary>是否已启动（UDP 多播 + HTTP 服务）。</summary>
    bool IsListening { get; }

    /// <summary>是否自动接受传入的传输请求（持久化）。</summary>
    bool AutoAccept { get; set; }

    /// <summary>接收保存目录（持久化）。</summary>
    string SaveDirectory { get; set; }

    /// <summary>在线设备列表（UI 直接绑定；Service 保证 UI 线程增删）。</summary>
    ObservableCollection<LanPeer> OnlinePeers { get; }

    /// <summary>传输记录（发送+接收；UI 直接绑定）。</summary>
    ObservableCollection<TransferProgress> Transfers { get; }

    /// <summary>收到传输请求（AutoAccept=false 时触发；订阅者必须调用 Respond）。</summary>
    event Action<LanIncomingRequest>? IncomingTransferRequested;

    /// <summary>启动 UDP 多播发现 + HTTP 传输服务。</summary>
    bool Start();

    /// <summary>停止（正在进行的传输被取消）。</summary>
    void Stop();

    /// <summary>立即发送一次 announce（让局域网设备快速发现本机）。</summary>
    void AnnounceNow();

    /// <summary>向目标设备发送一批文件（征询 → 接受 → 逐个上传）。</summary>
    Task SendFilesAsync(LanPeer target, IReadOnlyList<string> filePaths);
}
