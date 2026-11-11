using AI_Tool_Box.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using Serilog;
using System.Text;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace AI_Tool_Box.ViewModels;

/// <summary>音视频 AI 总结 VM：抽取音频 → 转写（AI/导入字幕）→ AI 总结 → 导出。</summary>
public partial class MediaSummaryViewModel : ObservableObject
{
    private readonly ILogger _logger;
    private readonly IMediaSummaryService _summary;
    private readonly IAiQuickService _ai;
    private readonly DispatcherQueue _dispatcher;

    [ObservableProperty] private string _inputPath = string.Empty;
    [ObservableProperty] private string _audioPath = string.Empty;
    [ObservableProperty] private string _transcript = string.Empty;
    [ObservableProperty] private string _summaryText = string.Empty;
    [ObservableProperty] private string _status = "1) 选择音视频 → 2) 抽取音频 → 3) 转写（或导入字幕）→ 4) AI 总结。";
    [ObservableProperty] private double _progress;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _focus = "通用";
    [ObservableProperty] private string _language = "自动";

    /// <summary>总结风格候选。</summary>
    public IReadOnlyList<string> FocusOptions { get; } = new[] { "通用", "课程笔记", "会议纪要", "视频脚本大纲" };

    /// <summary>转写语言候选（值传给 Whisper 的 language 参数）。</summary>
    public IReadOnlyList<string> LanguageOptions { get; } = new[] { "自动", "zh", "en", "ja", "ko", "fr", "de", "ru", "es" };

    /// <summary>构造：注入总结服务、AI 快捷服务与日志。</summary>
    public MediaSummaryViewModel(ILogger logger, IMediaSummaryService summary, IAiQuickService ai)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _summary = summary ?? throw new ArgumentNullException(nameof(summary));
        _ai = ai ?? throw new ArgumentNullException(nameof(ai));
        _dispatcher = DispatcherQueue.GetForCurrentThread() ?? DispatcherQueueController.CreateOnDedicatedThread().DispatcherQueue;
        Status = $"AI 提供商：{_ai.Describe()}。{Status}";
    }

    private bool CanRun => !IsBusy;

    private IProgress<double> MakeProgress() => new Progress<double>(d => _dispatcher.TryEnqueue(() => Progress = d));

    private IProgress<string> MakeStatus() => new Progress<string>(s => _dispatcher.TryEnqueue(() => Status = s));

    [RelayCommand]
    private async Task PickMediaAsync()
    {
        try
        {
            var picker = new FileOpenPicker();
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
            picker.SuggestedStartLocation = PickerLocationId.VideosLibrary;
            foreach (var ext in new[] { ".mp4", ".mkv", ".avi", ".mov", ".webm", ".flv", ".wmv", ".ts", ".m4v", ".mp3", ".wav", ".flac", ".m4a", ".aac", ".ogg" })
                picker.FileTypeFilter.Add(ext);
            var f = await picker.PickSingleFileAsync();
            if (f is null) return;
            InputPath = f.Path;
            AudioPath = string.Empty;
            Status = $"已选择：{f.Name}";
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "选择音视频失败");
            Status = $"选择失败：{ex.Message}";
        }
    }

    [RelayCommand]
    private async Task ImportTranscriptAsync()
    {
        try
        {
            var picker = new FileOpenPicker();
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
            picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
            foreach (var ext in new[] { ".txt", ".srt", ".vtt", ".md" }) picker.FileTypeFilter.Add(ext);
            var f = await picker.PickSingleFileAsync();
            if (f is null) return;
            Transcript = await FileIO.ReadTextAsync(f);
            Status = $"已导入字幕/文本：{f.Name}（{Transcript.Length} 字）";
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "导入字幕失败");
            Status = $"导入失败：{ex.Message}";
        }
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task ExtractAudioAsync()
    {
        if (string.IsNullOrWhiteSpace(InputPath) || !File.Exists(InputPath))
        {
            Status = "请先选择音视频文件。";
            return;
        }
        IsBusy = true;
        Progress = 0;
        Status = "正在抽取音频（ffmpeg）…";
        try
        {
            AudioPath = await _summary.ExtractAudioAsync(InputPath, MakeProgress());
            Status = $"音频已抽取：{AudioPath}";
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "抽取音频失败");
            Status = $"抽取失败：{ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task TranscribeAsync()
    {
        var source = !string.IsNullOrWhiteSpace(AudioPath) && File.Exists(AudioPath)
            ? AudioPath
            : (File.Exists(InputPath) ? InputPath : null);
        if (source is null)
        {
            Status = "请先选择媒体文件（或先抽取音频）。";
            return;
        }
        IsBusy = true;
        Status = "正在调用语音转写（Whisper，需联网）…";
        try
        {
            var lang = Language == "自动" ? null : Language;
            Transcript = await _ai.TranscribeAudioAsync(source, lang);
            Status = $"转写完成：{Transcript.Length} 字。";
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "语音转写失败");
            Status = $"转写失败：{ex.Message}（可导入 .srt/.txt 字幕替代）";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task SummarizeAsync()
    {
        if (string.IsNullOrWhiteSpace(Transcript))
        {
            Status = "请先转写或导入字幕文本。";
            return;
        }
        IsBusy = true;
        SummaryText = string.Empty;
        try
        {
            SummaryText = await _summary.SummarizeAsync(Transcript, Focus, MakeStatus(), CancellationToken.None);
            Status = "总结完成。";
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "AI 总结失败");
            Status = $"总结失败：{ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void CopySummary()
    {
        if (string.IsNullOrWhiteSpace(SummaryText)) return;
        var dp = new DataPackage();
        dp.SetText(SummaryText);
        Clipboard.SetContent(dp);
        Status = "总结已复制到剪贴板。";
    }

    [RelayCommand]
    private async Task SaveSummaryAsync()
    {
        if (string.IsNullOrWhiteSpace(SummaryText))
        {
            Status = "暂无可保存的总结。";
            return;
        }
        try
        {
            var picker = new FileSavePicker();
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
            picker.FileTypeChoices.Add("Markdown", new[] { ".md" });
            picker.FileTypeChoices.Add("文本", new[] { ".txt" });
            picker.SuggestedFileName = Path.GetFileNameWithoutExtension(InputPath) + "-summary";
            var f = await picker.PickSaveFileAsync();
            if (f is null) return;
            await FileIO.WriteTextAsync(f, SummaryText);
            Status = $"已保存：{f.Path}";
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "保存总结失败");
            Status = $"保存失败：{ex.Message}";
        }
    }

    partial void OnIsBusyChanged(bool value)
    {
        ExtractAudioCommand.NotifyCanExecuteChanged();
        TranscribeCommand.NotifyCanExecuteChanged();
        SummarizeCommand.NotifyCanExecuteChanged();
    }
}
