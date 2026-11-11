using CommunityToolkit.Mvvm.ComponentModel;
using System.Collections.ObjectModel;
using System.Net;

namespace AI_Tool_Box.Services;

/// <summary>局域网 AI 授权模式。</summary>
public enum LanAiAuthMode
{
    /// <summary>黑名单模式：黑名单 IP 拒绝，其余自动通过或弹确认。</summary>
    Blacklist = 0,
    /// <summary>白名单模式：白名单 IP 自动通过，其余弹确认。</summary>
    Whitelist = 1
}

/// <summary>转发到本机的后端 AI 类型。</summary>
public enum LanAiBackend
{
    /// <summary>本机 Ollama（http://localhost:11434）。</summary>
    Ollama = 0,
    /// <summary>本机 API（使用 Task 4 保存的 API Key + BaseUrl）。</summary>
    Api = 1
}

/// <summary>局域网 AI 服务器配置（持久化）。</summary>
public sealed class LanAiSettings
{
    /// <summary>授权模式。</summary>
    public LanAiAuthMode Mode { get; set; } = LanAiAuthMode.Blacklist;

    /// <summary>黑名单（Mode = Blacklist 时生效）。</summary>
    public List<string> Blacklist { get; set; } = new();

    /// <summary>白名单（Mode = Whitelist 时生效）。</summary>
    public List<string> Whitelist { get; set; } = new();

    /// <summary>非黑白名单 IP 是否弹出授权询问（true = 弹；false = 静默拒绝）。</summary>
    public bool PromptUnknownIp { get; set; } = true;

    /// <summary>后端转发类型。</summary>
    public LanAiBackend Backend { get; set; } = LanAiBackend.Ollama;

    /// <summary>当 Backend = Ollama 时，Ollama 基地址。</summary>
    public string OllamaBaseUrl { get; set; } = "http://localhost:11434";

    /// <summary>当 Backend = Api 时，是否允许转发（默认 false，避免意外泄露 API Key 额度）。</summary>
    public bool AllowApiForward { get; set; } = false;

    /// <summary>当 Backend = Api 时，显式配置的转发基地址（如 https://api.openai.com/v1 ）。
    /// 留空则使用 AIViewModel 写入的上一次已知值（通过保存的 api_baseurl 键，不存在时转发失败）。</summary>
    public string ApiForwardBaseUrl { get; set; } = string.Empty;

    /// <summary>服务端 TCP HTTP 端口（0=自动 9531 开始碰撞自增）。</summary>
    public int ServerPort { get; set; } = 9531;

    /// <summary>UDP 广播端口。</summary>
    public int UdpPort { get; set; } = 9532;

    /// <summary>是否随程序启动自动开启服务端。</summary>
    public bool AutoStartServer { get; set; } = false;

    /// <summary>保存的远端 AI 端点（Task 4 的局域网模式下拉源）。</summary>
    public List<LanAiSavedEndpoint> SavedEndpoints { get; set; } = new();
}

/// <summary>用户保存的远端 AI 端点（局域网模式选择项）。</summary>
public sealed class LanAiSavedEndpoint : ObservableObject
{
    private string _name = string.Empty;
    private string _baseUrl = string.Empty;
    private string _ownerMachine = string.Empty;
    private string _ownerIp = string.Empty;
    private DateTime _addedAtUtc = DateTime.UtcNow;

    /// <summary>显示名称（如"办公机 3080"）。</summary>
    public string Name { get => _name; set => SetProperty(ref _name, value); }

    /// <summary>基础 URL，如 http://192.168.1.10:9531/ </summary>
    public string BaseUrl { get => _baseUrl; set => SetProperty(ref _baseUrl, value); }

    /// <summary>对方机器名。</summary>
    public string OwnerMachine { get => _ownerMachine; set => SetProperty(ref _ownerMachine, value); }

    /// <summary>对方 IP。</summary>
    public string OwnerIp { get => _ownerIp; set => SetProperty(ref _ownerIp, value); }

    /// <summary>添加时间。</summary>
    public DateTime AddedAtUtc { get => _addedAtUtc; set => SetProperty(ref _addedAtUtc, value); }
}

/// <summary>局域网 AI 在线服务器（广播发现运行态集合项）。</summary>
public sealed class LanAiPeer : ObservableObject
{
    private string _machineName = string.Empty;
    private string _ip = string.Empty;
    private int _port;
    private string _backend = string.Empty;
    private DateTime _lastSeenUtc = DateTime.UtcNow;

    public string MachineName { get => _machineName; set => SetProperty(ref _machineName, value); }
    public string Ip { get => _ip; set => SetProperty(ref _ip, value); }
    public int Port { get => _port; set => SetProperty(ref _port, value); }
    public string Backend { get => _backend; set => SetProperty(ref _backend, value); }
    public DateTime LastSeenUtc { get => _lastSeenUtc; set => SetProperty(ref _lastSeenUtc, value); }

    /// <summary>显示文本。</summary>
    public string Display => $"{MachineName} @ {Ip}:{Port} ({Backend})";
}

/// <summary>待授权请求（服务端弹确认）。</summary>
public sealed class LanAiAuthRequest : ObservableObject
{
    private string _id = Guid.NewGuid().ToString("N");
    private string _fromIp = string.Empty;
    private string _machineName = string.Empty;
    private string _endpoint = string.Empty;
    private DateTime _requestedAtUtc = DateTime.UtcNow;
    private bool _handled;

    public string Id { get => _id; set => SetProperty(ref _id, value); }
    public string FromIp { get => _fromIp; set => SetProperty(ref _fromIp, value); }
    public string MachineName { get => _machineName; set => SetProperty(ref _machineName, value); }
    public string Endpoint { get => _endpoint; set => SetProperty(ref _endpoint, value); }
    public DateTime RequestedAtUtc { get => _requestedAtUtc; set => SetProperty(ref _requestedAtUtc, value); }
    public bool Handled { get => _handled; set => SetProperty(ref _handled, value); }

    public string Display => $"[{FromIp}] {MachineName} → {Endpoint}";
}

/// <summary>局域网 AI 服务接口（服务端 + 客户端发现 + 持久化）。</summary>
public interface ILanAiService
{
    /// <summary>当前配置。</summary>
    LanAiSettings Settings { get; }

    /// <summary>局域网发现的在线 AI 服务器列表。</summary>
    ObservableCollection<LanAiPeer> OnlinePeers { get; }

    /// <summary>保存的远端 AI 端点（Task 4 局域网模式使用）。</summary>
    ObservableCollection<LanAiSavedEndpoint> SavedEndpoints { get; }

    /// <summary>待授权请求队列（UI 绑定用于服务端弹确认）。</summary>
    ObservableCollection<LanAiAuthRequest> PendingAuth { get; }

    /// <summary>本机服务端是否运行中。</summary>
    bool IsServerRunning { get; }

    /// <summary>本机 IP（用于显示）。</summary>
    string LocalIp { get; }

    /// <summary>服务端实际 HTTP 端口（碰撞自增之后）。</summary>
    int ActualServerPort { get; }

    /// <summary>本机信息字符串。</summary>
    string InfoText { get; }

    /// <summary>启动服务端（HTTP 代理 + UDP 广播心跳）。</summary>
    bool StartServer();

    /// <summary>停止服务端。</summary>
    void StopServer();

    /// <summary>立即触发一次 UDP 广播发现（客户端扫描局域网 AI 服务器）。</summary>
    Task ScanPeersAsync(int waitSeconds = 4);

    /// <summary>授权/拒绝某个待授权请求。</summary>
    void RespondAuth(string requestId, bool allow, bool rememberToWhitelistOrBlacklist);

    /// <summary>把在线服务器保存为可调用端点（Task 4.4 来源）。</summary>
    void SaveEndpoint(LanAiPeer peer, string name);

    /// <summary>移除已保存端点。</summary>
    void RemoveSavedEndpoint(string baseUrl);

    /// <summary>立即保存配置到 JSON。</summary>
    void SaveSettings();
}
