using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using AI_Tool_Box.Models;
using AI_Tool_Box.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Serilog;

namespace AI_Tool_Box.ViewModels;

/// <summary>用量条目（按模型聚合）。</summary>
public sealed partial class TokenUsageRow : ObservableObject
{
    [ObservableProperty] private string _model = string.Empty;
    [ObservableProperty] private long _promptTokens;
    [ObservableProperty] private long _completionTokens;
    [ObservableProperty] private long _totalTokens;
    [ObservableProperty] private long _calls;
}

/// <summary>内测人员解锁成功消息：MainWindow 收到后显示"Edge扩展引入测试"侧栏项。</summary>
public sealed record TesterUnlockedMessage();

/// <summary>Edge 扩展列表行：包装服务条目以支持 UI 开关的属性变更通知。</summary>
public sealed partial class EdgeExtensionRow : ObservableObject
{
    public EdgeExtensionItem Item { get; }

    public string Name => Item.Name;
    public string Version => Item.Version;
    public string InstallPath => Item.InstallPath;

    [ObservableProperty] private bool _isEnabled;

    public EdgeExtensionRow(EdgeExtensionItem item)
    {
        Item = item;
        _isEnabled = item.Enabled;
    }
}

/// <summary>已保存的 API 档案展示行：厂商/模型/基地址/Key 掩码 + 连通性测试结果。</summary>
public sealed partial class ApiProfileRow : ObservableObject
{
    public ApiProfileRow(AiAssistProfile profile, string keyMask)
    {
        Profile = profile;
        _keyMask = keyMask;
    }

    public AiAssistProfile Profile { get; }

    public string DisplayName => Profile.DisplayName;

    public string KindLabel => Profile.Kind == "anthropic" ? "Anthropic 协议" : "OpenAI 兼容";

    public string BaseUrl => Profile.BaseUrl;

    public string Model => Profile.DefaultModel;

    /// <summary>API-Key 掩码（不显示明文）。</summary>
    [ObservableProperty] private string _keyMask;

    /// <summary>连通性测试结果。</summary>
    [ObservableProperty] private string _testResult = "尚未测试";

    [ObservableProperty] private bool _isTesting;
}

/// <summary>设置页视图模型：集中控制面板——用量统计、各功能线程数、FFmpeg 路径、API Key 管理、右侧栏AI辅助、内测人员（Edge 扩展）、数据管理。</summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly ILogger _logger;
    private readonly IUsageTracker _usage;
    private readonly IAppSettingsService _settings;
    private readonly FfmpegLocator _ffmpeg;
    private readonly IApiAiService _api;
    private readonly IApiKeyService _keyStore;
    private readonly IAiAssistSettingsService _assist;
    private readonly AiAssistViewModel _assistVm;
    private readonly EdgeExtensionService _edge;

    public SettingsViewModel(ILogger logger, IUsageTracker usage, IAppSettingsService settings,
        FfmpegLocator ffmpeg, IApiAiService api, IApiKeyService keyStore, IAiAssistSettingsService assist,
        AiAssistViewModel assistVm, EdgeExtensionService edge)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _usage = usage ?? throw new ArgumentNullException(nameof(usage));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _ffmpeg = ffmpeg ?? throw new ArgumentNullException(nameof(ffmpeg));
        _api = api ?? throw new ArgumentNullException(nameof(api));
        _keyStore = keyStore ?? throw new ArgumentNullException(nameof(keyStore));
        _assist = assist ?? throw new ArgumentNullException(nameof(assist));
        _assistVm = assistVm ?? throw new ArgumentNullException(nameof(assistVm));
        _edge = edge ?? throw new ArgumentNullException(nameof(edge));

        // 初始快照
        RefreshUsage();
        _snifferThreadCount = _settings.SnifferThreadCount;
        _transformThreadCount = _settings.TransformThreadCount;
        _ocrThreadCount = _settings.OcrThreadCount;
        _lanTransferThreadCount = _settings.LanTransferThreadCount;
        _downloadThreadCount = _settings.DownloadThreadCount;
        _vpnDefaultNode = _settings.VpnDefaultNode ?? string.Empty;
        _ffmpegPath = _ffmpeg.GetCustomPath() ?? _ffmpeg.Resolve() ?? string.Empty;
        _apiBaseUrl = _api.BaseUrl;
        _logDirectory = LoggerSetup.CurrentLogRoot;
        // 右侧栏AI辅助：档案快照与默认选中
        foreach (var p in _assist.Settings.Profiles)
        {
            AssistProfiles.Add(p);
        }
        SelectedAssistProfile = AssistProfiles.FirstOrDefault(p => p.Id == _assist.Settings.SelectedProfileId)
                                ?? AssistProfiles.FirstOrDefault();
        // 厂商预填充默认选中：优先匹配当前档案（便于看到已保存的模型），否则取第一个内置厂商
        var activeProfile = SelectedAssistProfile;
        SelectedAssistPreset = AssistPresets.FirstOrDefault(p =>
                                  activeProfile is not null
                                  && (string.Equals(p.BaseUrl, activeProfile.BaseUrl, StringComparison.OrdinalIgnoreCase)
                                      || string.Equals(p.DisplayName, activeProfile.DisplayName, StringComparison.Ordinal)))
                              ?? AssistPresets.FirstOrDefault();
        if (activeProfile is not null && !string.IsNullOrWhiteSpace(activeProfile.DefaultModel))
        {
            PresetModel = activeProfile.DefaultModel;
        }
        // 从 DPAPI 密钥库读回保存的 OpenAI key（不直接显示明文，仅回填掩码占位）
        var saved = _keyStore.Load("openai");
        _apiKey = string.IsNullOrEmpty(saved) ? string.Empty : new string('*', saved.Length);
        _settings.Changed += (_, _) => RefreshUsage();
        // 内测人员：解锁状态与扩展列表快照（已解锁则直接展示扩展管理区）
        _isTesterUnlocked = _edge.IsTesterUnlocked;
        if (_isTesterUnlocked) RefreshEdgeExtensions();
        // 已保存 API 信息展示（基地址/模型/Key 掩码）
        RefreshApiProfileRows();
    }

    // ===== 用量统计 =====
    public ObservableCollection<TokenUsageRow> TokenRows { get; } = new();

    /// <summary>最近一次操作结果反馈（按钮按下后有可见信息提示）。</summary>
    [ObservableProperty] private string _status = "就绪。";

    [ObservableProperty] private long _totalApiCalls;
    [ObservableProperty] private long _totalPromptTokens;
    [ObservableProperty] private long _totalCompletionTokens;
    [ObservableProperty] private long _totalAllTokens;
    [ObservableProperty] private long _vpnBytesSent;
    [ObservableProperty] private long _vpnBytesReceived;
    [ObservableProperty] private DateTime _lastUpdatedUtc;

    /// <summary>刷新用量快照（线程安全拷贝，UI 线程调用）。</summary>
    [RelayCommand]
    public void RefreshUsage()
    {
        TokenRows.Clear();
        long pSum = 0, cSum = 0, callSum = 0;
        foreach (var kv in _usage.TokenByModel)
        {
            var u = kv.Value;
            pSum += u.PromptTokens;
            cSum += u.CompletionTokens;
            callSum += u.Calls;
            TokenRows.Add(new TokenUsageRow
            {
                Model = kv.Key,
                PromptTokens = u.PromptTokens,
                CompletionTokens = u.CompletionTokens,
                TotalTokens = u.TotalTokens,
                Calls = u.Calls
            });
        }
        TotalApiCalls = _usage.ApiCallCount;
        TotalPromptTokens = pSum;
        TotalCompletionTokens = cSum;
        TotalAllTokens = pSum + cSum;
        VpnBytesSent = _usage.VpnBytesSent;
        VpnBytesReceived = _usage.VpnBytesReceived;
        LastUpdatedUtc = _usage.LastUpdatedUtc;
    }

    /// <summary>重置全部用量统计（清空 usage.json）。</summary>
    [RelayCommand]
    public void ResetUsage()
    {
        _usage.Reset();
        RefreshUsage();
        Status = "用量统计已重置（usage.json 已清空）。";
        _logger.Information("用量统计已由用户重置");
    }

    // ===== 线程数 =====
    [ObservableProperty] private int _snifferThreadCount;
    [ObservableProperty] private int _transformThreadCount;
    [ObservableProperty] private int _ocrThreadCount;
    [ObservableProperty] private int _lanTransferThreadCount;
    [ObservableProperty] private int _downloadThreadCount;
    [ObservableProperty] private string _vpnDefaultNode;

    partial void OnSnifferThreadCountChanged(int value) => _settings.SnifferThreadCount = value;
    partial void OnTransformThreadCountChanged(int value) => _settings.TransformThreadCount = value;
    partial void OnOcrThreadCountChanged(int value) => _settings.OcrThreadCount = value;
    partial void OnLanTransferThreadCountChanged(int value) => _settings.LanTransferThreadCount = value;
    partial void OnDownloadThreadCountChanged(int value) => _settings.DownloadThreadCount = value;
    partial void OnVpnDefaultNodeChanged(string value) => _settings.VpnDefaultNode = string.IsNullOrWhiteSpace(value) ? null : value;

    // 处理器上限（供 UI Slider Maximum 绑定）
    public int ProcessorCount => Environment.ProcessorCount;
    public int MaxDownloadThreads => 32;
    public int MaxLanThreads => 16;

    // ===== FFmpeg 路径 =====
    [ObservableProperty] private string _ffmpegPath;

    /// <summary>设置 FFmpeg 自定义路径（外部经文件选择器获得后传入）。</summary>
    public void SetFfmpegPath(string path)
    {
        _ffmpeg.SetCustomPath(string.IsNullOrWhiteSpace(path) ? null : path);
        FfmpegPath = _ffmpeg.GetCustomPath() ?? _ffmpeg.Resolve() ?? string.Empty;
        Status = $"FFmpeg 路径已更新：{FfmpegPath}";
        _logger.Information("FFmpeg 路径已更新：{Path}", FfmpegPath);
    }

    /// <summary>清除自定义 FFmpeg 路径，回退到自动查找。</summary>
    [RelayCommand]
    public void ClearFfmpegPath()
    {
        _ffmpeg.SetCustomPath(null);
        FfmpegPath = _ffmpeg.Resolve() ?? string.Empty;
        Status = $"已清除自定义 FFmpeg 路径，当前使用：{FfmpegPath}";
        _logger.Information("已清除 FFmpeg 自定义路径，回退自动查找：{Path}", FfmpegPath);
    }

    // ===== API Key 管理 =====
    [ObservableProperty] private string _apiBaseUrl;
    [ObservableProperty] private string _apiKey;

    partial void OnApiBaseUrlChanged(string value)
    {
        if (!string.IsNullOrWhiteSpace(value)) _api.BaseUrl = value;
    }

    /// <summary>保存 API Key（非掩码输入时才写库，避免把掩码当真实 key 存）。</summary>
    public void SaveApiKey(string input)
    {
        if (string.IsNullOrWhiteSpace(input) || input.Contains('*')) return;
        _keyStore.Save("openai", input.Trim());
        _api.ApiKey = input.Trim();
        ApiKey = new string('*', input.Trim().Length);
        Status = "API Key 已保存到 DPAPI 密钥库。";
        _logger.Information("API Key 已保存到 DPAPI 密钥库");
    }

    /// <summary>清除 API Key。</summary>
    [RelayCommand]
    public void ClearApiKey()
    {
        _keyStore.Delete("openai");
        _api.ApiKey = null;
        ApiKey = string.Empty;
        Status = "API Key 已清除。";
        _logger.Information("API Key 已清除");
    }

    // ===== 右侧栏AI辅助（提供商档案 + API-Key） =====

    /// <summary>提供商档案集合（与侧栏共享同一服务实例）。</summary>
    public System.Collections.ObjectModel.ObservableCollection<AiAssistProfile> AssistProfiles { get; }
        = new();

    /// <summary>当前编辑中的档案。</summary>
    [ObservableProperty] private AiAssistProfile? _selectedAssistProfile;

    /// <summary>API 基地址（编辑中）。</summary>
    [ObservableProperty] private string _assistBaseUrl = string.Empty;

    /// <summary>默认模型（编辑中）。</summary>
    [ObservableProperty] private string _assistModel = string.Empty;

    /// <summary>是否免鉴权（本地服务如 Ollama）。</summary>
    [ObservableProperty] private bool _assistNoKeyRequired;

    /// <summary>Key 输入框（PasswordBox 双向绑定；保存后自动回填为掩码）。</summary>
    [ObservableProperty] private string _assistApiKeyInput = string.Empty;

    /// <summary>会话 ID（编辑中）。</summary>
    [ObservableProperty] private string _assistSessionId = string.Empty;

    // ===== 厂商预填充（用户只需填 API-Key） =====

    /// <summary>内置的主流厂商预填充列表。</summary>
    public ObservableCollection<AiProviderPreset> AssistPresets { get; } = new(AiProviderPresets.All);

    /// <summary>当前选中的预置厂商。</summary>
    [ObservableProperty] private AiProviderPreset? _selectedAssistPreset;

    /// <summary>所选厂商的模型候选（ComboBox 可编辑）。</summary>
    public ObservableCollection<string> PresetModels { get; } = new();

    /// <summary>用户选中的模型名（可手改）。</summary>
    [ObservableProperty] private string _presetModel = string.Empty;

    /// <summary>预填充 API-Key（一键添加时写入 DPAPI）。</summary>
    [ObservableProperty] private string _presetKeyInput = string.Empty;

    /// <summary>预填充提示（基地址 / 申请地址 / 说明）。</summary>
    [ObservableProperty] private string _presetHint = "先选厂商，再粘贴 API-Key，最后点「一键添加/更新」。";

    partial void OnSelectedAssistPresetChanged(AiProviderPreset? value)
    {
        PresetModels.Clear();
        if (value is null)
        {
            PresetModel = string.Empty;
            return;
        }
        foreach (var model in value.Models) PresetModels.Add(model);
        PresetModel = value.Models.FirstOrDefault() ?? string.Empty;
        PresetKeyInput = string.Empty;
        PresetHint = $"基地址：{value.BaseUrl}　Key 申请：{value.ApplyUrl}"
                     + (string.IsNullOrEmpty(value.Note) ? string.Empty : $"　{value.Note}");
        // 本地免鉴权服务：模型候选以本机实际安装为准（预置列表里的 qwen3:8b 等只是示例）
        if (value.NoKeyRequired) _ = LoadLocalPresetModelsAsync(value);
    }

    /// <summary>读取本机 Ollama 实际已安装模型，填充预置厂商的模型下拉。</summary>
    private async Task LoadLocalPresetModelsAsync(AiProviderPreset preset)
    {
        var models = await OllamaService.ListModelsAtAsync(preset.BaseUrl);
        if (SelectedAssistPreset is null || SelectedAssistPreset.Id != preset.Id) return;
        if (models.Count == 0)
        {
            PresetHint = $"未连接到 {preset.BaseUrl}：请先启动 ollama（ollama serve），确认端口后重试。";
            return;
        }
        PresetModels.Clear();
        foreach (var m in models) PresetModels.Add(m);
        PresetModel = models[0];
        PresetHint = $"已读取本机 {models.Count} 个已安装模型：{string.Join("、", models)}";
    }

    /// <summary>按预置厂商一键添加或更新档案（含 API-Key 写入），保存后自动做连通性测试。</summary>
    [RelayCommand]
    public async Task AddFromPresetAsync()
    {
        var preset = SelectedAssistPreset;
        if (preset is null)
        {
            Status = "请先选择厂商。";
            return;
        }

        var id = $"vendor-{preset.Id}";
        var existing = _assist.Settings.Profiles.FirstOrDefault(p => p.Id == id)
                       ?? _assist.Settings.Profiles.FirstOrDefault(p => p.DisplayName == preset.DisplayName);
        var isNew = existing is null;
        var profile = existing ?? new AiAssistProfile { Id = id };

        profile.DisplayName = preset.DisplayName;
        profile.Kind = preset.Kind;
        profile.BaseUrl = preset.BaseUrl;
        var model = string.IsNullOrWhiteSpace(PresetModel)
            ? preset.Models.FirstOrDefault() ?? string.Empty
            : PresetModel.Trim();
        if (string.IsNullOrWhiteSpace(model) && preset.NoKeyRequired)
        {
            // 本地免鉴权服务（Ollama）：不使用预填的假模型名，改为本机实际已安装的模型
            var local = await OllamaService.ListModelsAtAsync(preset.BaseUrl);
            model = local.FirstOrDefault() ?? string.Empty;
            if (local.Count > 0)
            {
                PresetModels.Clear();
                foreach (var m in local) PresetModels.Add(m);
                PresetModel = model;
            }
        }
        profile.DefaultModel = model;
        profile.NoKeyRequired = preset.NoKeyRequired;
        if (string.IsNullOrWhiteSpace(profile.SessionId))
        {
            profile.SessionId = Guid.NewGuid().ToString("N")[..12];
        }
        if (isNew) _assist.Settings.Profiles.Add(profile);

        if (!preset.NoKeyRequired && !string.IsNullOrWhiteSpace(PresetKeyInput) && !PresetKeyInput.Contains('*'))
        {
            _keyStore.Save(AssistKeyName(profile), PresetKeyInput.Trim());
            // 同步到"AI API 配置"共用密钥（AI 问答页 / 语音转写等走 IApiAiService 的功能一起生效）
            _keyStore.Save("api", PresetKeyInput.Trim());
            _api.ApiKey = PresetKeyInput.Trim();
            ApiKey = new string('*', PresetKeyInput.Trim().Length);
        }
        else if (!preset.NoKeyRequired)
        {
            // 未重新输入 Key（只切换厂商/模型）时，沿用该档案已保存的 Key，并同步到共用密钥
            var savedKey = _keyStore.Load(AssistKeyName(profile));
            if (!string.IsNullOrWhiteSpace(savedKey))
            {
                _keyStore.Save("api", savedKey);
                _api.ApiKey = savedKey;
                ApiKey = new string('*', savedKey.Length);
            }
        }
        _api.BaseUrl = preset.BaseUrl;
        ApiBaseUrl = preset.BaseUrl;
        // 持久化到 AI 问答页读取的键，保证重启后仍是同一厂商/模型
        _keyStore.Save("api_baseurl", preset.BaseUrl);
        if (!string.IsNullOrWhiteSpace(profile.DefaultModel))
        {
            _keyStore.Save("api_model", profile.DefaultModel);
        }

        _assist.Settings.SelectedProfileId = profile.Id;
        _assist.Save();

        AssistProfiles.Clear();
        foreach (var item in _assist.Settings.Profiles) AssistProfiles.Add(item);
        SelectedAssistProfile = AssistProfiles.FirstOrDefault(x => x.Id == profile.Id);
        _assistVm.RefreshProfiles();
        _assistVm.RefreshHasSavedKey();
        PresetKeyInput = string.Empty;
        Status = $"{(isNew ? "已添加" : "已更新")}厂商“{preset.DisplayName}”，模型 {profile.DefaultModel}；全应用的 AI 功能已使用该配置。";
        _logger.Information("AI 厂商预填充应用：{Name} {Base} {Model}", profile.DisplayName, profile.BaseUrl, profile.DefaultModel);

        // 列表刷新 + 添加后自动连通性测试（免鉴权本地服务也测，验证是否在线）
        RefreshApiProfileRows();
        var row = ApiProfileRows.FirstOrDefault(r => r.Profile.Id == profile.Id);
        if (row is not null)
        {
            await TestConnectivityAsync(row);
            PresetHint = $"配置已保存。连通性：{row.TestResult}";
        }
    }

    partial void OnSelectedAssistProfileChanged(AiAssistProfile? value)
    {
        if (value is null)
        {
            AssistBaseUrl = AssistModel = string.Empty;
            AssistNoKeyRequired = false;
            AssistApiKeyInput = string.Empty;
            AssistSessionId = string.Empty;
            return;
        }
        AssistBaseUrl = value.BaseUrl;
        AssistModel = value.DefaultModel;
        AssistNoKeyRequired = value.NoKeyRequired;
        AssistSessionId = string.IsNullOrWhiteSpace(value.SessionId)
            ? Guid.NewGuid().ToString("N")[..12]
            : value.SessionId;
        // 已保存的 Key 不回显明文，只给掩码占位
        var saved = _keyStore.Load(AssistKeyName(value));
        AssistApiKeyInput = string.IsNullOrEmpty(saved) ? string.Empty : new string('*', saved.Length);
    }

    private static string AssistKeyName(AiAssistProfile p) => $"assist:{p.Id}";

    // ===== 已保存的 API 信息 / 连通性测试 =====

    /// <summary>已保存的全部 API 档案（展示用）。</summary>
    public ObservableCollection<ApiProfileRow> ApiProfileRows { get; } = new();

    /// <summary>从档案与 DPAPI 密钥库刷新展示列表。</summary>
    public void RefreshApiProfileRows()
    {
        ApiProfileRows.Clear();
        foreach (var profile in _assist.Settings.Profiles)
        {
            var saved = _keyStore.Load(AssistKeyName(profile));
            var mask = profile.NoKeyRequired
                ? "免鉴权（本地服务）"
                : string.IsNullOrWhiteSpace(saved)
                    ? "未保存"
                    : $"{new string('*', Math.Min(saved.Length, 24))}（{saved.Length} 位）";
            ApiProfileRows.Add(new ApiProfileRow(profile, mask));
        }
    }

    /// <summary>测试指定档案的连通性（GET /models，20 秒超时）。添加 API 时会自动调用。</summary>
    public async Task TestConnectivityAsync(ApiProfileRow row)
    {
        if (row.IsTesting) return;
        row.IsTesting = true;
        row.TestResult = "测试中…";
        Status = $"正在测试「{row.DisplayName}」连通性…";
        var watch = Stopwatch.StartNew();
        try
        {
            var key = row.Profile.NoKeyRequired ? null : _keyStore.Load(AssistKeyName(row.Profile));
            if (!row.Profile.NoKeyRequired && string.IsNullOrWhiteSpace(key))
            {
                row.TestResult = "未保存 API-Key，无法测试";
                Status = $"「{row.DisplayName}」尚未保存 API-Key。";
                return;
            }
            if (string.IsNullOrWhiteSpace(row.Profile.BaseUrl))
            {
                row.TestResult = "基地址为空";
                Status = $"「{row.DisplayName}」基地址为空。";
                return;
            }

            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            var baseUrl = row.Profile.BaseUrl.TrimEnd('/');
            using var request = row.Profile.Kind == "anthropic"
                ? new HttpRequestMessage(HttpMethod.Get, baseUrl + "/v1/models")
                : new HttpRequestMessage(HttpMethod.Get, baseUrl + "/models");
            if (!row.Profile.NoKeyRequired && !string.IsNullOrWhiteSpace(key))
            {
                if (row.Profile.Kind == "anthropic")
                {
                    request.Headers.Add("x-api-key", key);
                    request.Headers.Add("anthropic-version", "2023-06-01");
                }
                else
                {
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
                }
            }

            using var response = await http.SendAsync(request);
            watch.Stop();
            var body = await response.Content.ReadAsStringAsync();
            if (response.IsSuccessStatusCode)
            {
                var count = CountModels(body);
                row.TestResult = $"✓ 连通（HTTP {(int)response.StatusCode}，{watch.ElapsedMilliseconds} ms"
                                 + (count > 0 ? $" ，{count} 个模型" : string.Empty) + "）";
                Status = $"「{row.DisplayName}」连通性正常：{row.TestResult}";
                _logger.Information("API 连通性测试成功：{Name} {Status} {Elapsed}ms", row.DisplayName, response.StatusCode, watch.ElapsedMilliseconds);
            }
            else if (response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
            {
                row.TestResult = $"✗ 鉴权失败（HTTP {(int)response.StatusCode}），请检查 API-Key 是否正确/有权限";
                Status = $"「{row.DisplayName}」鉴权失败：Key 无效或无权限。";
            }
            else
            {
                var snippet = body.Length > 120 ? body[..120] + "…" : body;
                row.TestResult = $"✗ 服务返回 HTTP {(int)response.StatusCode}：{snippet}";
                Status = $"「{row.DisplayName}」连通性测试失败。";
            }
        }
        catch (TaskCanceledException)
        {
            row.TestResult = "✗ 连接超时（20 秒）";
            Status = $"「{row.DisplayName}」连接超时。";
        }
        catch (Exception ex)
        {
            row.TestResult = $"✗ {ex.Message}";
            Status = $"「{row.DisplayName}」连通性测试失败：{ex.Message}";
            _logger.Warning(ex, "API 连通性测试失败：{Name}", row.DisplayName);
        }
        finally
        {
            row.IsTesting = false;
        }
    }

    private static int CountModels(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
            {
                return data.GetArrayLength();
            }
            if (doc.RootElement.TryGetProperty("models", out var models) && models.ValueKind == JsonValueKind.Array)
            {
                return models.GetArrayLength();
            }
        }
        catch (JsonException)
        {
            // 非 JSON 成功响应：不显示模型数量
        }
        return 0;
    }

    /// <summary>把当前编辑字段写回档案并持久化。</summary>
    [RelayCommand]
    public void SaveAssistProfile()
    {
        if (SelectedAssistProfile is null)
        {
            Status = "请先选择一个提供商档案。";
            return;
        }
        var p = SelectedAssistProfile;
        p.BaseUrl = AssistBaseUrl.Trim();
        p.DefaultModel = AssistModel.Trim();
        p.NoKeyRequired = AssistNoKeyRequired;
        p.SessionId = string.IsNullOrWhiteSpace(AssistSessionId)
            ? Guid.NewGuid().ToString("N")[..12]
            : AssistSessionId.Trim();
        _assist.Save();
        // 重置集合以刷新 ComboBox 中显示名称等 PONO 属性的渲染
        SelectedAssistProfile = null;
        AssistProfiles.Clear();
        foreach (var profile in _assist.Settings.Profiles)
        {
            AssistProfiles.Add(profile);
        }
        SelectedAssistProfile = AssistProfiles.FirstOrDefault(x => x.Id == p.Id);
        _assistVm.RefreshProfiles();
        _assistVm.RefreshHasSavedKey();
        RefreshApiProfileRows();
        Status = $"提供商“{p.DisplayName}”档案已保存（名称/基地址/模型/免鉴权）。";
        _logger.Information("右侧栏AI辅助档案已保存：{Name} {Base} {Model}", p.DisplayName, p.BaseUrl, p.DefaultModel);
    }

    /// <summary>保存当前档案的 API-Key（DPAPI 加密落盘）。</summary>
    public void SaveAssistKey(string input)
    {
        if (SelectedAssistProfile is null)
        {
            Status = "请先选择一个提供商档案。";
            return;
        }
        if (string.IsNullOrWhiteSpace(input) || input.Contains('*'))
        {
            Status = "API-Key 未变化（掩码或空值不写入）。";
            return;
        }
        var p = SelectedAssistProfile;
        _keyStore.Save(AssistKeyName(p), input.Trim());
        AssistApiKeyInput = new string('*', input.Trim().Length);
        _assistVm.RefreshHasSavedKey();
        RefreshApiProfileRows();
        Status = $"“{p.DisplayName}”的 API-Key 已保存到 DPAPI 密钥库。";
        _logger.Information("右侧栏AI辅助 API-Key 已保存：{Name}", p.DisplayName);
    }

    /// <summary>清除当前档案的 API-Key。</summary>
    [RelayCommand]
    public void ClearAssistKey()
    {
        if (SelectedAssistProfile is null) return;
        var p = SelectedAssistProfile;
        _keyStore.Delete(AssistKeyName(p));
        AssistApiKeyInput = string.Empty;
        _assistVm.RefreshHasSavedKey();
        RefreshApiProfileRows();
        Status = $"“{p.DisplayName}”的 API-Key 已清除。";
        _logger.Information("右侧栏AI辅助 API-Key 已清除：{Name}", p.DisplayName);
    }

    /// <summary>新增自定义 OpenAI 兼容提供商档案。</summary>
    [RelayCommand]
    public void AddAssistProfile()
    {
        var p = new AiAssistProfile
        {
            DisplayName = "自定义 OpenAI 兼容",
            Kind = "openai",
            BaseUrl = "https://api.openai.com/v1",
            DefaultModel = "gpt-4o-mini"
        };
        _assist.Settings.Profiles.Add(p);
        _assist.Save();
        AssistProfiles.Add(p);
        SelectedAssistProfile = p;
        _assistVm.RefreshProfiles();
        RefreshApiProfileRows();
        Status = "已新增提供商档案，请修改名称/基地址后点击“保存档案修改”。";
        _logger.Information("右侧栏AI辅助新增提供商档案：{Name}", p.DisplayName);
    }

    /// <summary>删除当前选中的提供商档案（连同其 Key）。</summary>
    [RelayCommand(CanExecute = nameof(CanRemoveAssistProfile))]
    public void RemoveAssistProfile()
    {
        if (SelectedAssistProfile is null || AssistProfiles.Count <= 1) return;
        var p = SelectedAssistProfile;
        _keyStore.Delete(AssistKeyName(p));
        _assist.Settings.Profiles.Remove(p);
        AssistProfiles.Remove(p);
        _assist.Settings.SelectedProfileId = AssistProfiles[0].Id;
        _assist.Save();
        SelectedAssistProfile = AssistProfiles[0];
        _assistVm.RefreshProfiles();
        _assistVm.RefreshHasSavedKey();
        RefreshApiProfileRows();
        Status = $"提供商“{p.DisplayName}”及其 API-Key 已删除。";
        _logger.Information("右侧栏AI辅助删除提供商档案：{Name}", p.DisplayName);
    }

    private bool CanRemoveAssistProfile() => SelectedAssistProfile is not null && AssistProfiles.Count > 1;

    // ===== 内测人员（Edge 扩展导入与管理） =====

    /// <summary>内测功能是否已解锁（口令 114514）。</summary>
    [ObservableProperty] private bool _isTesterUnlocked;

    /// <summary>口令输入框内容。</summary>
    [ObservableProperty] private string _testerCodeInput = string.Empty;

    /// <summary>已导入扩展列表（UI 绑定行）。</summary>
    public ObservableCollection<EdgeExtensionRow> EdgeExtensions { get; } = new();

    /// <summary>校验口令解锁内测功能。</summary>
    [RelayCommand]
    public void UnlockTester()
    {
        if (_edge.UnlockTester(TesterCodeInput))
        {
            IsTesterUnlocked = true;
            TesterCodeInput = string.Empty;
            RefreshEdgeExtensions();
            Status = "内测功能已解锁：侧栏已出现「Edge扩展引入测试」，可在其中使用已启用的扩展。";
            // 通知主窗口显示"Edge扩展引入测试"侧栏项（经 IMessenger 接口调用以正确解析扩展重载）
            IMessenger bus = WeakReferenceMessenger.Default;
            bus.Send(new TesterUnlockedMessage());
            _logger.Information("设置页：内测功能已解锁");
        }
        else
        {
            Status = "口令错误，内测功能未解锁。";
            _logger.Warning("设置页：内测口令校验失败");
        }
    }

    /// <summary>从服务存储刷新扩展列表。</summary>
    public void RefreshEdgeExtensions()
    {
        EdgeExtensions.Clear();
        foreach (var e in _edge.Store.Extensions)
        {
            EdgeExtensions.Add(new EdgeExtensionRow(e));
        }
    }

    /// <summary>导入扩展文件夹（外部经文件夹选择器获得路径后传入）。</summary>
    public void ImportExtension(string folder)
    {
        var item = _edge.Import(folder);
        RefreshEdgeExtensions();
        Status = $"已导入扩展：{item.Name}{(string.IsNullOrEmpty(item.Version) ? string.Empty : " " + item.Version)}";
    }

    /// <summary>切换扩展启用状态。</summary>
    public void SetExtensionEnabled(EdgeExtensionRow row, bool enabled)
    {
        _edge.SetEnabled(row.Item, enabled);
        row.IsEnabled = enabled;
    }

    /// <summary>删除扩展。</summary>
    public void RemoveExtension(EdgeExtensionRow row)
    {
        _edge.Remove(row.Item);
        EdgeExtensions.Remove(row);
        Status = $"已删除扩展：{row.Name}（如该扩展已装入测试浏览器，点击其页面「重载扩展」生效）";
    }

    // ===== 日志 =====
    [ObservableProperty] private string _logDirectory;

    /// <summary>设置自定义日志目录并热重载日志管道（外部经文件夹选择器获得后传入；null 恢复默认）。</summary>
    public void SetLogDirectory(string? dir)
    {
        LoggerSetup.SetCustomDirectory(dir);
        LogDirectory = LoggerSetup.CurrentLogRoot;
        Status = $"日志输出目录已更新并立即生效：{LogDirectory}";
        _logger.Information("日志输出目录已更新：{Dir}", LogDirectory);
    }

    /// <summary>恢复默认日志目录（安装目录\logs）。</summary>
    [RelayCommand]
    public void ClearLogDirectory()
    {
        LoggerSetup.SetCustomDirectory(null);
        LogDirectory = LoggerSetup.CurrentLogRoot;
        Status = $"已恢复默认日志目录：{LogDirectory}";
        _logger.Information("已恢复默认日志目录：{Dir}", LogDirectory);
    }
}
