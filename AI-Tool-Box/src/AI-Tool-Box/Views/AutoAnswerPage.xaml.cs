using AI_Tool_Box.Models;
using AI_Tool_Box.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Serilog;
using System.Net.Http.Headers;

namespace AI_Tool_Box.Views;

/// <summary>自动答题页：离线（手动拾取区域 + 本机 Ollama 生成代码 + 自动提交）与联网（AI 驱动浏览器控制器）两种模式。
/// 右侧内嵌 WebShell，用于页面拾取与自动操作。</summary>
public sealed partial class AutoAnswerPage : Page
{
    private readonly ILogger _logger;
    private readonly IWebShellService _web;
    private readonly IAutoAnswerService _autoAnswer;
    private readonly IBrowserControllerService _browser;
    private readonly IAutoAnswerSettingsService _settings;
    private readonly IOllamaService _ollama;
    private readonly IAiAssistSettingsService _assist;
    private readonly IApiKeyService _keys;

    private CancellationTokenSource? _pickCts;
    private CancellationTokenSource? _runCts;

    /// <summary>联网答题可选 API 档案项（连通性测试通过的）。</summary>
    private sealed class ApiChoice
    {
        public AiAssistProfile Profile { get; init; } = null!;
        public string Display { get; init; } = string.Empty;
        public override string ToString() => Display;
    }

    private bool _apisLoaded;
    private bool _apisTesting;

    /// <summary>初始化自动答题页。</summary>
    public AutoAnswerPage()
    {
        InitializeComponent();

        _logger = App.Services.GetService(typeof(ILogger)) as ILogger ?? Serilog.Log.Logger;
        _web = App.Services.GetRequiredService<IWebShellService>();
        _autoAnswer = App.Services.GetRequiredService<IAutoAnswerService>();
        _browser = App.Services.GetRequiredService<IBrowserControllerService>();
        _settings = App.Services.GetRequiredService<IAutoAnswerSettingsService>();
        _ollama = App.Services.GetRequiredService<IOllamaService>();
        _assist = App.Services.GetRequiredService<IAiAssistSettingsService>();
        _keys = App.Services.GetRequiredService<IApiKeyService>();

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;

        LoadSettingsToUi();
        _ = RefreshModelsAsync();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            if (App.MainWindow is MainWindow mw)
            {
                mw.AttachWebShellTo(WebShellSlot, addressBarVisible: true);
                _logger.Information("AutoAnswerPage Loaded：已挂载 WebShell");
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "AutoAnswerPage 挂载 WebShell 失败");
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        try
        {
            if (App.MainWindow is MainWindow mw)
            {
                mw.DetachWebShellFrom(WebShellSlot);
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "AutoAnswerPage 归还 WebShell 失败");
        }

        Loaded -= OnLoaded;
        Unloaded -= OnUnloaded;
        _pickCts?.Cancel();
        _runCts?.Cancel();
    }

    // ===== 模式切换 =====
    private void OnModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (OfflinePanel is null || OnlinePanel is null) return; // 构造期保护
        var online = ModeSelector.SelectedIndex == 1;
        OfflinePanel.Visibility = online ? Visibility.Collapsed : Visibility.Visible;
        OnlinePanel.Visibility = online ? Visibility.Visible : Visibility.Collapsed;

        // 首次进入联网模式：后台逐个测试 API 档案连通性，仅把通过的填入选择框
        if (online && !_apisLoaded)
        {
            _ = RefreshOnlineApisAsync();
        }
    }

    // ===== 联网答题 API 选择（仅显示连通性测试通过的档案） =====
    private async void OnRetestApisClick(object sender, RoutedEventArgs e) => await RefreshOnlineApisAsync();

    private async Task RefreshOnlineApisAsync()
    {
        if (_apisTesting) return;
        _apisTesting = true;
        RetestApisButton.IsEnabled = false;
        ApiTestStatusText.Text = "正在测试各 API 档案连通性…";

        try
        {
            var profiles = _assist.Settings.Profiles;
            var passed = new List<ApiChoice>();
            foreach (var p in profiles)
            {
                if (await TestProfileAsync(p))
                {
                    passed.Add(new ApiChoice
                    {
                        Profile = p,
                        Display = string.IsNullOrWhiteSpace(p.DefaultModel)
                            ? p.DisplayName
                            : $"{p.DisplayName}（{p.DefaultModel}）"
                    });
                }
            }

            _apisLoaded = true;
            var prevId = _assist.Settings.SelectedProfileId;
            OnlineApiSelector.ItemsSource = passed;
            ApiTestStatusText.Text = passed.Count == 0
                ? "没有连通性测试通过的档案：请到「设置 → 右侧栏AI辅助」配置 API-Key 后重试。"
                : $"通过 {passed.Count} 个档案（共 {profiles.Count} 个）。";

            var pick = passed.FirstOrDefault(c => c.Profile.Id == prevId) ?? passed.FirstOrDefault();
            OnlineApiSelector.SelectedItem = pick; // null 时清空选择
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "联网答题 API 连通性测试失败");
            ApiTestStatusText.Text = $"测试失败：{ex.Message}";
        }
        finally
        {
            _apisTesting = false;
            RetestApisButton.IsEnabled = true;
        }
    }

    /// <summary>测试单个档案连通性（GET /models，20 秒超时，与设置页逻辑一致）。</summary>
    private async Task<bool> TestProfileAsync(AiAssistProfile profile)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(profile.BaseUrl)) return false;
            var key = profile.NoKeyRequired ? null : _keys.Load($"assist:{profile.Id}");
            if (!profile.NoKeyRequired && string.IsNullOrWhiteSpace(key)) return false;

            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            var baseUrl = profile.BaseUrl.TrimEnd('/');
            using var request = profile.Kind == "anthropic"
                ? new HttpRequestMessage(HttpMethod.Get, baseUrl + "/v1/models")
                : new HttpRequestMessage(HttpMethod.Get, baseUrl + "/models");
            if (!profile.NoKeyRequired && !string.IsNullOrWhiteSpace(key))
            {
                if (profile.Kind == "anthropic")
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
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private void OnOnlineApiChanged(object sender, SelectionChangedEventArgs e)
    {
        if (OnlineApiSelector.SelectedItem is not ApiChoice choice) return;
        if (_assist.Settings.SelectedProfileId != choice.Profile.Id)
        {
            _assist.Settings.SelectedProfileId = choice.Profile.Id;
            _assist.Save();
            _logger.Information("联网自动答题改用 API 档案：{Name}", choice.Profile.DisplayName);
        }
        Log($"联网答题 API：{choice.Display}");
    }

    // ===== 设置读写 =====
    private void LoadSettingsToUi()
    {
        var s = _settings.Settings;
        var site = _settings.SelectedSite;

        UrlTemplateBox.Text = site.UrlTemplate;
        StartIndexBox.Value = site.StartIndex;
        StepBox.Value = site.Step;
        LanguageBox.Text = site.Language;
        ProblemSelectorBox.Text = site.ProblemSelector;
        TypeSelectorBox.Text = site.TypeSelector;
        SubmitSelectorBox.Text = site.SubmitSelector;
        ErrorSelectorBox.Text = site.ErrorRegionSelector;

        BaseUrlBox.Text = s.OllamaBaseUrl;
        RunCountBox.Value = s.RunCount;
        MaxStepsBox.Value = s.MaxSteps;
        ModelBox.Text = s.OllamaModel;
    }

    private void SaveUiToSettings()
    {
        var site = _settings.SelectedSite;
        site.UrlTemplate = UrlTemplateBox.Text?.Trim() ?? string.Empty;
        site.StartIndex = ToInt(StartIndexBox.Value, 1001);
        site.Step = Math.Max(1, ToInt(StepBox.Value, 1));
        site.Language = string.IsNullOrWhiteSpace(LanguageBox.Text) ? "C++" : LanguageBox.Text.Trim();
        site.ProblemSelector = ProblemSelectorBox.Text?.Trim() ?? string.Empty;
        site.TypeSelector = TypeSelectorBox.Text?.Trim() ?? string.Empty;
        site.SubmitSelector = SubmitSelectorBox.Text?.Trim() ?? string.Empty;
        site.ErrorRegionSelector = ErrorSelectorBox.Text?.Trim() ?? string.Empty;

        var s = _settings.Settings;
        s.OllamaBaseUrl = string.IsNullOrWhiteSpace(BaseUrlBox.Text) ? "http://localhost:11434" : BaseUrlBox.Text.Trim();
        s.RunCount = Math.Max(1, ToInt(RunCountBox.Value, 1));
        s.MaxSteps = Math.Max(1, ToInt(MaxStepsBox.Value, 30));
        s.OllamaModel = ModelBox.Text?.Trim() ?? string.Empty;

        _settings.Save();
    }

    private static int ToInt(double v, int fallback) => double.IsNaN(v) ? fallback : (int)v;

    // ===== Ollama 模型列表 =====
    private async void OnRefreshModelsClick(object sender, RoutedEventArgs e) => await RefreshModelsAsync();

    private async Task RefreshModelsAsync()
    {
        ModelStatusText.Text = "正在读取本机 Ollama 模型…";
        try
        {
            _ollama.BaseUrl = string.IsNullOrWhiteSpace(BaseUrlBox.Text) ? "http://localhost:11434" : BaseUrlBox.Text.Trim();
            var models = await _ollama.ListModelsAsync();
            var current = ModelBox.Text;
            ModelBox.ItemsSource = models;
            if (!string.IsNullOrWhiteSpace(current)) ModelBox.Text = current;
            else if (models.Count > 0) ModelBox.Text = models[0];

            ModelStatusText.Text = models.Count == 0
                ? "未读取到模型：请确认本机已运行 ollama serve 且已 pull 模型。"
                : $"已读取 {models.Count} 个模型。";
        }
        catch (Exception ex)
        {
            ModelStatusText.Text = $"读取失败：{ex.Message}";
        }
    }

    // ===== 页面拾取 =====
    private async void OnPickProblemClick(object sender, RoutedEventArgs e) => await PickAsync(ProblemSelectorBox, "题目区域");
    private async void OnPickTypeClick(object sender, RoutedEventArgs e) => await PickAsync(TypeSelectorBox, "代码输入区");
    private async void OnPickSubmitClick(object sender, RoutedEventArgs e) => await PickAsync(SubmitSelectorBox, "提交按钮");
    private async void OnPickErrorClick(object sender, RoutedEventArgs e) => await PickAsync(ErrorSelectorBox, "判题结果区");

    private async Task PickAsync(TextBox target, string label)
    {
        if (_pickCts is not null)
        {
            _pickCts.Cancel();
            _pickCts = null;
            Log($"已取消「{label}」拾取。");
            return;
        }

        if (!_web.IsAvailable)
        {
            Log("当前没有打开的网页：请先在右侧浏览器中打开题目页面。");
            return;
        }

        _pickCts = new CancellationTokenSource();
        Log($"区域拾取「{label}」：在右侧网页移动鼠标高亮目标区域，左键单击选取，Esc 取消。");
        try
        {
            var region = await _web.PickRegionAsync(_pickCts.Token);
            if (region is not null)
            {
                target.Text = region.Selector;
                Log(string.IsNullOrWhiteSpace(region.Label)
                    ? $"「{label}」→ {region.Selector}"
                    : $"「{label}」→ {region.Selector}（区域：{region.Label}）");
            }
            else
            {
                Log($"「{label}」拾取已取消或失败。");
            }
        }
        catch (Exception ex)
        {
            Log($"拾取「{label}」失败：{ex.Message}");
        }
        finally
        {
            _pickCts?.Dispose();
            _pickCts = null;
        }
    }

    // ===== 预览 / 运行 =====
    private async void OnPreviewClick(object sender, RoutedEventArgs e)
    {
        SaveUiToSettings();
        var site = _settings.SelectedSite;
        var url = BuildUrl(site.UrlTemplate, site.StartIndex);
        if (string.IsNullOrWhiteSpace(url))
        {
            Log("请先填写题组网址规律。");
            return;
        }
        Log($"打开预览：{url}");
        await _web.NavigateAsync(url);
    }

    private async void OnRunOfflineClick(object sender, RoutedEventArgs e)
    {
        if (_autoAnswer.IsRunning || _browser.IsRunning)
        {
            Log("已有任务在运行，请先停止。");
            return;
        }

        SaveUiToSettings();
        var site = _settings.SelectedSite;
        var s = _settings.Settings;

        if (string.IsNullOrWhiteSpace(site.UrlTemplate)) { Log("请填写题组网址规律。"); return; }
        if (string.IsNullOrWhiteSpace(site.TypeSelector)) { Log("请先拾取「代码输入区」。"); return; }
        if (string.IsNullOrWhiteSpace(site.SubmitSelector)) { Log("请先拾取「提交按钮」。"); return; }
        if (string.IsNullOrWhiteSpace(s.OllamaModel)) { Log("请先在「本机 Ollama」中选择或填写模型。"); return; }
        if (!_web.IsAvailable) { Log("当前没有打开的网页，请先在右侧打开题目页。"); return; }

        _runCts = new CancellationTokenSource();
        SetRunning(true, online: false);
        var progress = new Progress<AutoAnswerProgress>(p => Log(p.Message));

        try
        {
            await _autoAnswer.RunOfflineAsync(site, s.OllamaModel, s.OllamaBaseUrl, s.RunCount, progress, _runCts.Token);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "离线自动答题失败");
            Log($"离线任务异常：{ex.Message}");
        }
        finally
        {
            SetRunning(false, online: false);
            _runCts?.Dispose();
            _runCts = null;
        }
    }

    private async void OnRunOnlineClick(object sender, RoutedEventArgs e)
    {
        if (_autoAnswer.IsRunning || _browser.IsRunning)
        {
            Log("已有任务在运行，请先停止。");
            return;
        }

        var goal = GoalBox.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(goal))
        {
            Log("请填写任务目标。");
            return;
        }

        SaveUiToSettings();
        _runCts = new CancellationTokenSource();
        SetRunning(true, online: true);
        var progress = new Progress<string>(Log);

        try
        {
            var result = await _browser.RunAsync(goal, _settings.Settings.MaxSteps, progress, _runCts.Token);
            Log($"结束：{result}");
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "AI 浏览器控制失败");
            Log($"联网任务异常：{ex.Message}");
        }
        finally
        {
            SetRunning(false, online: true);
            _runCts?.Dispose();
            _runCts = null;
        }
    }

    private void OnStopClick(object sender, RoutedEventArgs e)
    {
        _runCts?.Cancel();
        _pickCts?.Cancel();
        Log("已请求停止…");
    }

    private void SetRunning(bool running, bool online)
    {
        RunOfflineButton.IsEnabled = !running;
        RunOnlineButton.IsEnabled = !running;
        StopOfflineButton.IsEnabled = running;
        StopOnlineButton.IsEnabled = running;
        OfflineRing.IsActive = running && !online;
        OnlineRing.IsActive = running && online;
    }

    private static string BuildUrl(string template, int n)
    {
        if (string.IsNullOrWhiteSpace(template)) return string.Empty;
        return template.Contains("{n}", StringComparison.Ordinal)
            ? template.Replace("{n}", n.ToString(), StringComparison.Ordinal)
            : template + n;
    }

    // ===== 日志 =====
    private void Log(string message)
    {
        var line = $"[{DateTime.Now:HH:mm:ss}] {message}";
        if (DispatcherQueue.HasThreadAccess)
        {
            AppendLog(line);
        }
        else
        {
            DispatcherQueue.TryEnqueue(() => AppendLog(line));
        }
    }

    private void AppendLog(string line)
    {
        LogBox.Text += line + Environment.NewLine;
        LogBox.SelectionStart = LogBox.Text.Length;
    }
}
