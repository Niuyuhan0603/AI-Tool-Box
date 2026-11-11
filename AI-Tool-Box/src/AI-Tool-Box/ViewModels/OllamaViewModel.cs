using AI_Tool_Box.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;
using System.Collections.ObjectModel;
using System.Text;
using Windows.Storage.Pickers;

namespace AI_Tool_Box.ViewModels;

/// <summary>本地 Ollama 一键运行页 VM：检测安装 → 启动 serve → 拉取/删除模型 → 显示日志。</summary>
public partial class OllamaViewModel : ObservableObject
{
    private readonly ILogger _logger;
    private readonly IOllamaRuntimeService _runtime;
    private readonly IOllamaService _ollama;
    private readonly StringBuilder _log = new();
    private readonly StringBuilder _pullLog = new();

    [ObservableProperty] private string _exePath = "未检测到";
    [ObservableProperty] private string _status = "点击「检测状态」查看 Ollama 安装与运行情况。";
    [ObservableProperty] private bool _serverRunning;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _pullModel = "qwen2.5:3b";
    [ObservableProperty] private string _logText = string.Empty;
    [ObservableProperty] private string _pullLogText = string.Empty;

    /// <summary>已安装模型列表。</summary>
    public ObservableCollection<string> Models { get; } = new();

    /// <summary>常用模型候选（可自由输入其他模型名）。</summary>
    public IReadOnlyList<string> CommonModels { get; } = new[]
    {
        "qwen2.5:3b", "qwen2.5:7b", "llama3.2:3b", "llama3.1:8b",
        "deepseek-r1:7b", "gemma2:9b", "phi3.5:3.8b", "mistral:7b",
    };

    /// <summary>构造：注入运行时管理、模型列表与日志服务。</summary>
    public OllamaViewModel(ILogger logger, IOllamaRuntimeService runtime, IOllamaService ollama)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _ollama = ollama ?? throw new ArgumentNullException(nameof(ollama));
    }

    private bool CanRun => !IsBusy;

    /// <summary>追加一行日志（同时写文件日志）。</summary>
    private void AppendLog(string line)
    {
        _log.AppendLine($"[{DateTime.Now:HH:mm:ss}] {line}");
        LogText = _log.ToString();
    }

    /// <summary>页面上屏时自动检测一次。</summary>
    public async Task InitializeAsync()
    {
        try
        {
            await RefreshStatusAsync();
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Ollama 页面初始化失败");
        }
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task RefreshStatusAsync()
    {
        IsBusy = true;
        try
        {
            var exe = _runtime.Detect();
            ExePath = exe ?? "未检测到（请安装 Ollama 或点击「指定 ollama.exe」）";
            ServerRunning = await _runtime.IsServerRunningAsync();
            Status = ServerRunning
                ? "服务运行中：http://localhost:11434"
                : (exe is null ? "未安装 Ollama：请先安装，或指定已解压的 ollama.exe。" : "服务未运行：点击「一键启动」运行 ollama serve。");
            if (ServerRunning) await LoadModelsCoreAsync();
            AppendLog($"检测完成：exe={(exe ?? "无")}，服务={(ServerRunning ? "运行中" : "未运行")}");
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "检测 Ollama 状态失败");
            Status = $"检测失败：{ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task StartServeAsync()
    {
        IsBusy = true;
        Status = "正在启动 ollama serve（首次可能需要几秒）…";
        AppendLog("启动 ollama serve…");
        try
        {
            var ok = await _runtime.StartServeAsync();
            ServerRunning = ok;
            Status = ok ? "已启动：http://localhost:11434" : "启动失败：请确认已安装 Ollama（日志见下）。";
            AppendLog(ok ? "ollama serve 已就绪" : "ollama serve 启动失败或超时");
            if (ok) await LoadModelsCoreAsync();
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "启动 ollama serve 失败");
            Status = $"启动失败：{ex.Message}";
            AppendLog($"启动异常：{ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task StopServeAsync()
    {
        IsBusy = true;
        try
        {
            _runtime.StopServe();
            ServerRunning = await _runtime.IsServerRunningAsync();
            Status = ServerRunning
                ? "已停止本应用启动的进程，但检测到仍有其他 Ollama 服务在运行（可能由 Ollama 客户端托管）。"
                : "已停止 ollama serve。";
            AppendLog(Status);
        }
        catch (Exception ex)
        {
            Status = $"停止失败：{ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task RefreshModelsAsync()
    {
        IsBusy = true;
        try
        {
            ServerRunning = await _runtime.IsServerRunningAsync();
            if (ServerRunning) await LoadModelsCoreAsync();
            else Status = "服务未运行，无法列出模型。";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task LoadModelsCoreAsync()
    {
        var models = await _ollama.ListModelsAsync();
        Models.Clear();
        foreach (var m in models) Models.Add(m);
        AppendLog($"已安装模型：{Models.Count} 个");
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task PullAsync()
    {
        if (string.IsNullOrWhiteSpace(PullModel))
        {
            Status = "请输入要拉取的模型名（例如 qwen2.5:3b）。";
            return;
        }
        IsBusy = true;
        _pullLog.Clear();
        PullLogText = string.Empty;
        Status = $"开始拉取 {PullModel}（体积较大，请耐心等待）…";
        try
        {
            if (!await _runtime.IsServerRunningAsync())
            {
                AppendLog("服务未运行，先尝试启动 ollama serve…");
                await _runtime.StartServeAsync();
            }
            var progress = new Progress<string>(line =>
            {
                _pullLog.AppendLine(line);
                PullLogText = _pullLog.ToString();
                Status = line.Length > 120 ? line[..120] : line;
            });
            var ok = await _runtime.PullModelAsync(PullModel, progress);
            Status = ok ? $"{PullModel} 拉取完成。" : $"{PullModel} 拉取失败（详情见下方输出）。";
            ServerRunning = await _runtime.IsServerRunningAsync();
            if (ok) await LoadModelsCoreAsync();
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "拉取模型失败");
            Status = $"拉取异常：{ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task DeleteModelAsync(string? model)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            Status = "请先在列表中选中要删除的模型。";
            return;
        }
        IsBusy = true;
        Status = $"正在删除模型 {model}…";
        try
        {
            var ok = await _runtime.DeleteModelAsync(model);
            Status = ok ? $"已删除模型：{model}" : $"删除失败：{model}";
            AppendLog(Status);
            await LoadModelsCoreAsync();
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task BrowseExeAsync()
    {
        try
        {
            var picker = new FileOpenPicker();
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
            picker.FileTypeFilter.Add(".exe");
            picker.SuggestedStartLocation = PickerLocationId.ComputerFolder;
            var f = await picker.PickSingleFileAsync();
            if (f is null) return;
            _runtime.SetExecutablePath(f.Path);
            ExePath = f.Path;
            AppendLog($"已指定 ollama.exe：{f.Path}");
            await RefreshStatusAsync();
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "选择 ollama.exe 失败");
            Status = $"选择失败：{ex.Message}";
        }
    }

    partial void OnIsBusyChanged(bool value)
    {
        RefreshStatusCommand.NotifyCanExecuteChanged();
        StartServeCommand.NotifyCanExecuteChanged();
        StopServeCommand.NotifyCanExecuteChanged();
        RefreshModelsCommand.NotifyCanExecuteChanged();
        PullCommand.NotifyCanExecuteChanged();
        DeleteModelCommand.NotifyCanExecuteChanged();
    }
}
