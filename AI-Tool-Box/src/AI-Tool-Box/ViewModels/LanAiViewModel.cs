using AI_Tool_Box.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;
using System.Collections.ObjectModel;

namespace AI_Tool_Box.ViewModels;

/// <summary>局域网 AI 服务视图模型：
/// 左 = 服务端（开关/端口/后端/黑白名单/待授权队列）；
/// 右 = 客户端（在线服务器/保存的端点/添加/删除）。</summary>
public partial class LanAiViewModel : ObservableObject
{
    private readonly ILogger _logger;
    private readonly ILanAiService _service;

    /// <summary>在线 AI 服务器列表。</summary>
    public ObservableCollection<LanAiPeer> OnlinePeers => _service.OnlinePeers;

    /// <summary>保存的远端端点（Task 4 局域网 AI 模式下拉来源）。</summary>
    public ObservableCollection<LanAiSavedEndpoint> SavedEndpoints => _service.SavedEndpoints;

    /// <summary>待授权请求（服务端）。</summary>
    public ObservableCollection<LanAiAuthRequest> PendingAuth => _service.PendingAuth;

    /// <summary>服务端配置引用。</summary>
    public LanAiSettings Settings => _service.Settings;

    /// <summary>本机信息。</summary>
    public string InfoText => _service.InfoText;

    /// <summary>服务端是否运行。</summary>
    public bool IsServerRunning => _service.IsServerRunning;

    /// <summary>服务端开关按钮文字（随状态切换）。</summary>
    public string ServerButtonText => IsServerRunning ? "停止服务" : "开启服务端";

    // ============ 中间属性（Settings 本身非 ObservableObject，通过 OnPropertyChanged 手动通知） ============

    public int ModeInt
    {
        get => (int)Settings.Mode;
        set
        {
            if (value < 0 || value > 1) return;
            Settings.Mode = (LanAiAuthMode)value;
            OnPropertyChanged();
        }
    }

    public int BackendInt
    {
        get => (int)Settings.Backend;
        set
        {
            if (value < 0 || value > 1) return;
            Settings.Backend = (LanAiBackend)value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(InfoText));
        }
    }

    public bool PromptUnknownIp
    {
        get => Settings.PromptUnknownIp;
        set { Settings.PromptUnknownIp = value; OnPropertyChanged(); }
    }

    public bool AllowApiForward
    {
        get => Settings.AllowApiForward;
        set { Settings.AllowApiForward = value; OnPropertyChanged(); OnPropertyChanged(nameof(InfoText)); }
    }

    public bool AutoStartServer
    {
        get => Settings.AutoStartServer;
        set { Settings.AutoStartServer = value; OnPropertyChanged(); }
    }

    public int ServerPort
    {
        get => Settings.ServerPort;
        set { if (value is > 0 and < 65536) { Settings.ServerPort = value; OnPropertyChanged(); } }
    }

    public string OllamaBaseUrl
    {
        get => Settings.OllamaBaseUrl;
        set { Settings.OllamaBaseUrl = value ?? string.Empty; OnPropertyChanged(); }
    }

    public string ApiForwardBaseUrl
    {
        get => Settings.ApiForwardBaseUrl;
        set { Settings.ApiForwardBaseUrl = value ?? string.Empty; OnPropertyChanged(); }
    }

    public string BlacklistCsv
    {
        get => string.Join(',', Settings.Blacklist);
        set
        {
            Settings.Blacklist = value?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList() ?? new();
            OnPropertyChanged();
        }
    }

    public string WhitelistCsv
    {
        get => string.Join(',', Settings.Whitelist);
        set
        {
            Settings.Whitelist = value?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList() ?? new();
            OnPropertyChanged();
        }
    }

    [ObservableProperty] private bool _isScanningPeers;
    [ObservableProperty] private string _status = "局域网 AI 服务：本机可作为 AI 代理提供给同局域网其他电脑使用；或扫描保存其他电脑的 AI 代理。";
    [ObservableProperty] private LanAiPeer? _selectedOnlinePeer;
    [ObservableProperty] private LanAiSavedEndpoint? _selectedSavedEndpoint;
    [ObservableProperty] private LanAiAuthRequest? _selectedAuthRequest;
    [ObservableProperty] private string _saveEndpointName = string.Empty;

    public LanAiViewModel(ILogger logger, ILanAiService service)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _service = service ?? throw new ArgumentNullException(nameof(service));
    }

    // ============== 服务端 ==============

    [RelayCommand]
    private void ToggleServer()
    {
        if (!IsServerRunning)
        {
            var ok = _service.StartServer();
            if (ok)
            {
                Status = $"服务端已启动：{InfoText}。其他电脑（模式=局域网）可调用本机 AI。";
                OnPropertyChanged(nameof(IsServerRunning));
                OnPropertyChanged(nameof(ServerButtonText));
                OnPropertyChanged(nameof(InfoText));
                _logger.Information("用户启动局域网 AI 服务端");
            }
            else Status = "启动服务端失败（端口绑定失败或权限不足）。";
        }
        else
        {
            _service.StopServer();
            Status = "服务端已停止。";
            OnPropertyChanged(nameof(IsServerRunning));
            OnPropertyChanged(nameof(ServerButtonText));
            OnPropertyChanged(nameof(InfoText));
            _logger.Information("用户停止局域网 AI 服务端");
        }
    }

    [RelayCommand]
    private void AllowAuth()
    {
        if (SelectedAuthRequest is null) return;
        _service.RespondAuth(SelectedAuthRequest.Id, allow: true, rememberToWhitelistOrBlacklist: true);
        Status = $"已允许 {SelectedAuthRequest.FromIp} 调用（自动加入白名单）。";
    }

    [RelayCommand]
    private void AllowAuthOnce()
    {
        if (SelectedAuthRequest is null) return;
        _service.RespondAuth(SelectedAuthRequest.Id, allow: true, rememberToWhitelistOrBlacklist: false);
        Status = $"已允许 {SelectedAuthRequest.FromIp} 调用（仅本次）。";
    }

    [RelayCommand]
    private void DenyAuth()
    {
        if (SelectedAuthRequest is null) return;
        _service.RespondAuth(SelectedAuthRequest.Id, allow: false, rememberToWhitelistOrBlacklist: true);
        Status = $"已拒绝 {SelectedAuthRequest.FromIp}（自动加入黑名单）。";
    }

    [RelayCommand]
    private void DenyAuthOnce()
    {
        if (SelectedAuthRequest is null) return;
        _service.RespondAuth(SelectedAuthRequest.Id, allow: false, rememberToWhitelistOrBlacklist: false);
        Status = $"已拒绝 {SelectedAuthRequest.FromIp}（仅本次）。";
    }

    // ============== 客户端 ==============

    private bool CanScanPeers => !IsScanningPeers;

    [RelayCommand(CanExecute = nameof(CanScanPeers))]
    private async Task ScanPeers()
    {
        IsScanningPeers = true;
        try
        {
            Status = "扫描局域网 AI 服务器…（4 秒）";
            await _service.ScanPeersAsync(4);
            Status = $"扫描完成：共发现 {OnlinePeers.Count} 台在线 AI 服务器。选中后点击“保存端点”可供 AI 页面答题选择。";
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "扫描失败");
            Status = $"扫描失败：{ex.Message}";
        }
        finally
        {
            IsScanningPeers = false;
        }
    }

    private bool CanSaveSelectedPeer => SelectedOnlinePeer is not null;

    [RelayCommand(CanExecute = nameof(CanSaveSelectedPeer))]
    private void SaveSelectedPeer()
    {
        if (SelectedOnlinePeer is null) return;
        var name = string.IsNullOrWhiteSpace(SaveEndpointName)
            ? $"{SelectedOnlinePeer.MachineName}-{SelectedOnlinePeer.Backend}"
            : SaveEndpointName.Trim();
        _service.SaveEndpoint(SelectedOnlinePeer, name);
        SaveEndpointName = string.Empty;
        Status = $"已保存端点 {name}（共 {SavedEndpoints.Count} 个）。在“AI 页面内答题”中切换模式=局域网即可选择该端点。";
    }

    private bool CanRemoveSaved => SelectedSavedEndpoint is not null;

    [RelayCommand(CanExecute = nameof(CanRemoveSaved))]
    private void RemoveSaved()
    {
        if (SelectedSavedEndpoint is null) return;
        _service.RemoveSavedEndpoint(SelectedSavedEndpoint.BaseUrl);
        Status = $"已删除保存端点 {SelectedSavedEndpoint.Name}。";
    }

    // ============== 设置 ==============

    [RelayCommand]
    private void SaveSettings()
    {
        _service.SaveSettings();
        Status = $"设置已保存：模式={Settings.Mode}，后端={Settings.Backend}，共 {SavedEndpoints.Count} 个保存端点。";
    }

    partial void OnSelectedOnlinePeerChanged(LanAiPeer? value)
    {
        SaveSelectedPeerCommand.NotifyCanExecuteChanged();
        if (value is not null && string.IsNullOrWhiteSpace(SaveEndpointName))
        {
            SaveEndpointName = $"{value.MachineName}-{value.Backend}";
        }
    }

    partial void OnSelectedSavedEndpointChanged(LanAiSavedEndpoint? value)
        => RemoveSavedCommand.NotifyCanExecuteChanged();

    partial void OnSelectedAuthRequestChanged(LanAiAuthRequest? value)
    {
        AllowAuthCommand.NotifyCanExecuteChanged();
        AllowAuthOnceCommand.NotifyCanExecuteChanged();
        DenyAuthCommand.NotifyCanExecuteChanged();
        DenyAuthOnceCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsScanningPeersChanged(bool value)
        => ScanPeersCommand.NotifyCanExecuteChanged();
}
