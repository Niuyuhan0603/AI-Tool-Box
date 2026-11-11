using AI_Tool_Box.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Web.WebView2.Core;
using Serilog;
using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace AI_Tool_Box.ViewModels;

public enum TransformMode
{
    DocxToPdf = 0,
    PdfToDocx = 1,
    VideoToMp4 = 2,
    LinkDownload = 3,
    /// <summary>通用音视频格式互转（按目标扩展名自动选择 ffmpeg 参数）。</summary>
    MediaConvert = 4,
}

/// <summary>文件转换 VM。</summary>
public partial class TransformViewModel : ObservableObject
{
    private readonly ILogger _logger;
    private readonly ITransformService _transform;
    private readonly ISnifferService? _sniffer; // 可选：Task10 复用 T9 m3u8 下载
    private readonly FfmpegLocator _ffmpeg;

    [ObservableProperty] private TransformMode _mode = TransformMode.DocxToPdf;
    [ObservableProperty] private string _inputPath = string.Empty;
    [ObservableProperty] private string _outputPath = string.Empty;
    [ObservableProperty] private string _downloadUrl = string.Empty;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private double _progress;
    [ObservableProperty] private string _status = "选择任务类型，选取源文件或输入下载直链，点击转换/下载。";
    [ObservableProperty] private string _previewText = string.Empty;
    [ObservableProperty] private int _httpConcurrency = 128;
    [ObservableProperty] private int _m3u8Concurrency = 128;
    [ObservableProperty] private string? _ffmpegPath;

    // ===== FFmpeg 设置卡片 =====
    [ObservableProperty] private string? _customFfmpegPath;
    private bool _suppressFfmpegPersist;

    public ObservableCollection<string> RecentOutputs { get; } = new();

    public TransformViewModel(ILogger logger, ITransformService transform, ISnifferService? sniffer = null, FfmpegLocator? ffmpeg = null)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _transform = transform ?? throw new ArgumentNullException(nameof(transform));
        _sniffer = sniffer;
        _ffmpeg = ffmpeg ?? new FfmpegLocator();
        _suppressFfmpegPersist = true;
        CustomFfmpegPath = _ffmpeg.GetCustomPath();
        _suppressFfmpegPersist = false;
        ReloadFfmpeg();
    }

    // ==================== FFmpeg 设置 ====================

    private void ReloadFfmpeg()
    {
        var p = _transform.LocateFfmpeg();
        FfmpegPath = p is null ? "未检测到（请在上方指定外部 ffmpeg.exe 路径，或安装 ffmpeg 并加入系统 PATH）" : p;
    }

    partial void OnCustomFfmpegPathChanged(string? value)
    {
        if (_suppressFfmpegPersist) return;
        _ffmpeg.SetCustomPath(string.IsNullOrWhiteSpace(value) ? null : value);
        ReloadFfmpeg();
        Status = string.IsNullOrWhiteSpace(value) ? "已清除 FFmpeg 自定义路径（恢复默认解析）。" : "已保存 FFmpeg 自定义路径。";
    }

    [RelayCommand]
    private void RefreshFfmpeg()
    {
        ReloadFfmpeg();
        Status = "已刷新 FFmpeg 检测。";
    }

    [RelayCommand]
    private async Task BrowseFfmpeg()
    {
        try
        {
            var picker = new FileOpenPicker();
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow));
            picker.FileTypeFilter.Add(".exe");
            var f = await picker.PickSingleFileAsync();
            if (f is not null) CustomFfmpegPath = f.Path; // 触发持久化与刷新
        }
        catch (Exception ex) { _logger.Error(ex, "选择 ffmpeg.exe 失败"); Status = $"选择失败：{ex.Message}"; }
    }

    [RelayCommand]
    private void ResetFfmpeg()
    {
        if (string.IsNullOrWhiteSpace(CustomFfmpegPath))
        {
            Status = "当前未设置自定义 FFmpeg 路径（已在使用默认解析）。";
            return;
        }
        CustomFfmpegPath = null; // 触发持久化与刷新
    }

    partial void OnModeChanged(TransformMode value)
    {
        Progress = 0;
        PreviewText = string.Empty;
        Status = value switch
        {
            TransformMode.DocxToPdf => "选择 .docx 文件并指定输出 .pdf（本地渲染，正确显示中文与段落层级）。",
            TransformMode.PdfToDocx => "选择 .pdf 文件并指定输出 .docx（PDF 引擎提取文字，支持中文）。",
            TransformMode.VideoToMp4 => "选择任意视频源文件，调用 FFmpeg 转为 MP4（H.264 + AAC，faststart）。",
            TransformMode.LinkDownload => "粘贴直链或 m3u8：HTTP 直链自动 Range 分块；m3u8 自动调用媒体嗅探器下载器。",
            _ => Status
        };
    }

    [RelayCommand]
    private async Task PickInputFile()
    {
        try
        {
            var picker = new FileOpenPicker();
            // 关联窗口句柄（WinUI3 必填）
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
            picker.FileTypeFilter.Add("*");
            var f = await picker.PickSingleFileAsync();
            if (f is not null)
            {
                InputPath = f.Path;
                Status = $"已选择源文件：{f.Name}";
                if (Mode == TransformMode.DocxToPdf &&
                    string.IsNullOrEmpty(OutputPath) &&
                    InputPath.EndsWith(".docx", StringComparison.OrdinalIgnoreCase))
                {
                    OutputPath = Path.ChangeExtension(InputPath, ".pdf");
                }
                if (Mode == TransformMode.PdfToDocx &&
                    string.IsNullOrEmpty(OutputPath) &&
                    InputPath.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
                {
                    OutputPath = Path.ChangeExtension(InputPath, ".docx");
                }
                if (Mode == TransformMode.VideoToMp4 &&
                    string.IsNullOrEmpty(OutputPath) &&
                    !InputPath.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase))
                {
                    OutputPath = Path.ChangeExtension(InputPath, ".mp4");
                }
                if (Mode == TransformMode.MediaConvert && !string.IsNullOrEmpty(OutputPath) &&
                    string.Equals(Path.GetExtension(InputPath), Path.GetExtension(OutputPath), StringComparison.OrdinalIgnoreCase))
                {
                    OutputPath = string.Empty; // 同格式时清空，避免误覆盖源文件
                    Status = $"已选择源文件：{f.Name}（请选择不同扩展名的输出格式）";
                }
                if ((Mode == TransformMode.DocxToPdf || Mode == TransformMode.PdfToDocx))
                {
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            var preview = Mode == TransformMode.DocxToPdf
                                ? await _transform.DocxExtractTextAsync(InputPath)
                                : await _transform.PdfExtractTextAsync(InputPath);
                            PreviewText = preview.Length > 10_000 ? preview[..10_000] : preview;
                        }
                        catch (Exception ex) { PreviewText = $"预览失败：{ex.Message}"; }
                    });
                }
            }
        }
        catch (Exception ex) { _logger.Error(ex, "选择输入文件失败"); Status = $"选择失败：{ex.Message}"; }
    }

    [RelayCommand]
    private async Task PickOutputFile()
    {
        try
        {
            var picker = new FileSavePicker();
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
            picker.FileTypeChoices.Add("PDF", new[] { ".pdf" });
            picker.FileTypeChoices.Add("Word 文档", new[] { ".docx" });
            picker.FileTypeChoices.Add("MP4 视频", new[] { ".mp4" });
            picker.FileTypeChoices.Add("MP3 音频", new[] { ".mp3" });
            picker.FileTypeChoices.Add("无损 FLAC", new[] { ".flac" });
            picker.FileTypeChoices.Add("WAV 音频", new[] { ".wav" });
            picker.FileTypeChoices.Add("MKV 视频", new[] { ".mkv" });
            picker.FileTypeChoices.Add("任意", new[] { "*" });
            if (!string.IsNullOrEmpty(OutputPath))
            {
                picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
                picker.SuggestedFileName = Path.GetFileName(OutputPath);
            }
            var f = await picker.PickSaveFileAsync();
            if (f is not null) OutputPath = f.Path;
        }
        catch (Exception ex) { _logger.Error(ex, "选择输出文件失败"); Status = $"选择失败：{ex.Message}"; }
    }

    private IProgress<double> MakeProgress()
    {
        var dispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        var p = new Progress<double>(d =>
        {
            dispatcher?.TryEnqueue(() => Progress = d);
        });
        return p;
    }

    private bool CanRun => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task RunAsync()
    {
        IsBusy = true;
        Progress = 0;
        try
        {
            switch (Mode)
            {
                case TransformMode.DocxToPdf:
                    {
                        if (string.IsNullOrWhiteSpace(InputPath) || !File.Exists(InputPath)) throw new FileNotFoundException("请选择存在的 .docx 文件。");
                        if (string.IsNullOrWhiteSpace(OutputPath)) throw new ArgumentException("请选择输出 PDF 路径。");
                        Status = "本地渲染 DOCX（保留中文与段落层级）…";
                        await DocxToPdfAsync(InputPath, OutputPath);
                        Status = $"已导出 PDF：{OutputPath}";
                        RecentOutputs.Insert(0, OutputPath);
                    }
                    break;
                case TransformMode.PdfToDocx:
                    {
                        if (string.IsNullOrWhiteSpace(InputPath) || !File.Exists(InputPath)) throw new FileNotFoundException("请选择存在的 .pdf 文件。");
                        if (string.IsNullOrWhiteSpace(OutputPath)) throw new ArgumentException("请选择输出 DOCX 路径。");
                        Status = "用 PDF 引擎提取文字…";
                        var txt = await ExtractPdfTextViaWebAsync(InputPath);
                        if (string.IsNullOrWhiteSpace(txt))
                            throw new InvalidOperationException("未提取到文字（可能是扫描件/图片型 PDF，请改用本地 OCR）。");
                        var lines = txt.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
                        Status = "写出 DOCX…";
                        await _transform.WriteDocxFromLinesAsync(OutputPath, lines, title: Path.GetFileName(InputPath));
                        Status = $"已导出 DOCX：{OutputPath}";
                        RecentOutputs.Insert(0, OutputPath);
                    }
                    break;
                case TransformMode.VideoToMp4:
                    {
                        if (string.IsNullOrWhiteSpace(InputPath) || !File.Exists(InputPath)) throw new FileNotFoundException("请选择存在的视频文件。");
                        if (string.IsNullOrWhiteSpace(OutputPath)) throw new ArgumentException("请选择输出 MP4 路径。");
                        Status = "开始调用 FFmpeg 转码（可长时间运行）…";
                        var result = await _transform.ConvertToMp4Async(InputPath, OutputPath, progress: MakeProgress());
                        Status = $"转码完成：{result}";
                        RecentOutputs.Insert(0, result);
                    }
                    break;
                case TransformMode.MediaConvert:
                    {
                        if (string.IsNullOrWhiteSpace(InputPath) || !File.Exists(InputPath)) throw new FileNotFoundException("请选择存在的音视频文件。");
                        if (string.IsNullOrWhiteSpace(OutputPath)) throw new ArgumentException("请选择输出文件路径（扩展名决定目标格式，如 .mp3/.mkv/.flac）。");
                        if (string.Equals(Path.GetExtension(InputPath), Path.GetExtension(OutputPath), StringComparison.OrdinalIgnoreCase))
                            throw new ArgumentException("输入与输出扩展名相同，请选择不同的目标格式。");
                        Status = $"开始音视频转换（{Path.GetExtension(OutputPath).TrimStart('.').ToUpperInvariant()}）…";
                        var result = await _transform.ConvertMediaAsync(InputPath, OutputPath, progress: MakeProgress());
                        Status = $"转换完成：{result}";
                        RecentOutputs.Insert(0, result);
                    }
                    break;
                case TransformMode.LinkDownload:
                    {
                        if (string.IsNullOrWhiteSpace(DownloadUrl)) throw new ArgumentException("请输入要下载的直链 URL（HTTP/HTTPS 或 m3u8）。");
                        var url = DownloadUrl.Trim();
                        if (url.Contains(".m3u8", StringComparison.OrdinalIgnoreCase))
                        {
                            if (_sniffer is null) throw new InvalidOperationException("嗅探器未就绪（Task 9 未初始化）。");
                            Status = "调用媒体嗅探器的 m3u8 下载器…";
                            var item = new SnifferResourceItem
                            {
                                Url = url,
                                Kind = SnifferResourceKind.M3u8,
                                Title = string.IsNullOrWhiteSpace(OutputPath) ? $"m3u8-download-{DateTime.Now:yyyyMMdd-HHmmss}" : Path.GetFileNameWithoutExtension(OutputPath),
                                PageUrl = url,
                            };
                            var task = await _sniffer.DownloadAsync(item,
                                saveDir: string.IsNullOrWhiteSpace(OutputPath) ? null : Path.GetDirectoryName(OutputPath),
                                m3u8Concurrency: Math.Max(1, M3u8Concurrency));
                            Status = task.IsCompleted
                                ? $"m3u8 下载完成：{task.LocalPath}"
                                : $"m3u8 下载任务已派发：{task.Status} {task.ProgressPercentDisplay}";
                            RecentOutputs.Insert(0, task.LocalPath);
                        }
                        else
                        {
                            if (string.IsNullOrWhiteSpace(OutputPath))
                            {
                                OutputPath = Path.Combine(
                                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                                    "Downloads",
                                    Uri.TryCreate(url, UriKind.Absolute, out var u) ? (string.IsNullOrWhiteSpace(u.LocalPath.Trim('/')) ? $"file-{Guid.NewGuid():N}" : Path.GetFileName(u.LocalPath.TrimEnd('/'))) : $"file-{Guid.NewGuid():N}");
                            }
                            Status = "HTTP Range 分块下载…";
                            var path = await _transform.DownloadHttpRangeAsync(url, OutputPath,
                                concurrency: Math.Max(1, HttpConcurrency),
                                progress: MakeProgress());
                            Status = $"下载完成：{path}";
                            RecentOutputs.Insert(0, path);
                        }
                    }
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "转换/下载失败");
            Status = $"失败：{ex.Message}";
        }
        finally
        {
            IsBusy = false;
            if (Progress < 1 && Progress > 0) Progress = 0;
            RunCommand.NotifyCanExecuteChanged();
        }
    }

    partial void OnIsBusyChanged(bool value) => RunCommand.NotifyCanExecuteChanged();

    // ==================== PDF 工具（隐藏 WebView2 承载 Assets/Web/pdf.html） ====================

    /// <summary>PDF 输出目录：%USERPROFILE%\Downloads\AI-Tool-Box\Transform\</summary>
    private static string PdfOutputDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "AI-Tool-Box", "Transform");

    [ObservableProperty] private string _pdfStatus = "图片→PDF / PDF合并 / PDF→PNG，输出保存到 下载\\AI-Tool-Box\\Transform\\。首次使用需联网加载 PDF 组件。";
    [ObservableProperty] private bool _pdfBusy;

    private CoreWebView2? _pdfWeb;
    private bool _pdfReady;
    private int _pdfReceived;
    private int _pdfExpected = 1;
    private TaskCompletionSource<bool>? _pdfTcs;

    private bool CanPdf => !PdfBusy;

    /// <summary>发给 pdf.html 的文件载荷（键名与 pdf.html 协议约定的小写 name / dataBase64 对齐）。</summary>
    private sealed record PdfFilePayload([property: JsonPropertyName("name")] string Name,
                                         [property: JsonPropertyName("dataBase64")] string DataBase64);

    /// <summary>由页面把就绪的 CoreWebView2 交给 VM（必须在 UI 线程调用）。</summary>
    public void AttachPdfWeb(CoreWebView2 web) => _pdfWeb = web;

    // ===== 办公文件：DOCX → PDF（WebView2 打印，保留中文/段落）与 PDF → DOCX（PDF 引擎提取文字） =====

    private CoreWebView2? _printWeb;

    /// <summary>由页面把负责打印的 CoreWebView2 交给 VM（必须在 UI 线程调用）。</summary>
    public void AttachPrintWeb(CoreWebView2 web) => _printWeb = web;

    private bool _pdfCollectText;
    private readonly SortedDictionary<int, string> _pdfTextPages = new();

    /// <summary>把 DOCX 转成 HTML 后用 WebView2 打印为 PDF（避免标准 Helvetica 无法显示中文）。</summary>
    private async Task DocxToPdfAsync(string docxPath, string outPdf)
    {
        if (_printWeb is null)
            throw new InvalidOperationException("PDF 打印组件尚未就绪（WebView2 正在初始化），请稍候重试。");
        var bodyHtml = await _transform.DocxToHtmlAsync(docxPath);
        var html = BuildPrintHtml(bodyHtml, Path.GetFileNameWithoutExtension(docxPath));
        var tmp = Path.Combine(Path.GetTempPath(), $"aibox-print-{Guid.NewGuid():N}.html");
        await File.WriteAllTextAsync(tmp, html, new System.Text.UTF8Encoding(false));
        try
        {
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            void Handler(object? s, CoreWebView2NavigationCompletedEventArgs e)
            {
                _printWeb.NavigationCompleted -= Handler;
                tcs.TrySetResult(e.IsSuccess);
            }
            _printWeb.NavigationCompleted += Handler;
            _printWeb.Navigate(new Uri(tmp).AbsoluteUri);
            if (!await tcs.Task) throw new InvalidOperationException("DOCX 内容渲染失败，无法打印为 PDF。");

            var settings = _printWeb.Environment.CreatePrintSettings();
            settings.Orientation = CoreWebView2PrintOrientation.Portrait;
            settings.ShouldPrintBackgrounds = true;
            settings.MarginTop = 0.4;
            settings.MarginBottom = 0.4;
            settings.MarginLeft = 0.4;
            settings.MarginRight = 0.4;

            var dir = Path.GetDirectoryName(outPdf);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var ok = await _printWeb.PrintToPdfAsync(outPdf, settings);
            if (!ok || !File.Exists(outPdf)) throw new InvalidOperationException("打印为 PDF 失败。");
        }
        finally
        {
            try { File.Delete(tmp); } catch { /* 临时文件清理失败可忽略 */ }
        }
    }

    /// <summary>把 DOCX 转出的 HTML 片段包装成完整可打印页面（UTF-8 + 中文字体 + A4 版式）。</summary>
    private static string BuildPrintHtml(string bodyHtml, string title)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("<!DOCTYPE html><html lang=\"zh-CN\"><head><meta charset=\"utf-8\"><title>");
        sb.Append(System.Net.WebUtility.HtmlEncode(title));
        sb.Append("</title><style>");
        sb.Append("@page{size:A4;margin:16mm;}");
        sb.Append("body{font-family:\"Microsoft YaHei\",\"SimSun\",\"Segoe UI\",serif;font-size:12pt;line-height:1.6;color:#111;margin:0;}");
        sb.Append("h1{font-size:20pt;margin:14pt 0 8pt;}h2{font-size:17pt;margin:12pt 0 6pt;}h3{font-size:15pt;margin:10pt 0 6pt;}");
        sb.Append("h4,h5,h6{font-size:13pt;margin:8pt 0 4pt;}p{margin:0 0 6pt;}img{max-width:100%;}");
        sb.Append("</style></head><body>");
        sb.Append(bodyHtml);
        sb.Append("</body></html>");
        return sb.ToString();
    }

    /// <summary>调用 pdf.html 的 pdf2text 逐页提取文字（支持中文），按页码顺序拼接返回。</summary>
    private async Task<string> ExtractPdfTextViaWebAsync(string pdfPath)
    {
        if (_pdfWeb is null || !_pdfReady)
            throw new InvalidOperationException("PDF 工具页尚未就绪（等待 WebView2 与 PDF 库加载，请稍候重试）。");

        _pdfCollectText = true;
        _pdfTextPages.Clear();
        try
        {
            var bytes = await File.ReadAllBytesAsync(pdfPath);
            var payload = new
            {
                cmd = "pdf2text",
                file = new PdfFilePayload(Path.GetFileName(pdfPath), Convert.ToBase64String(bytes)),
            };
            _pdfReceived = 0;
            _pdfExpected = 1;
            _pdfTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pdfWeb.PostWebMessageAsJson(JsonSerializer.Serialize(payload));
            await _pdfTcs.Task;

            var sb = new System.Text.StringBuilder();
            foreach (var kv in _pdfTextPages)
            {
                sb.Append(kv.Value);
                if (!kv.Value.EndsWith('\n')) sb.Append('\n');
            }
            return sb.ToString();
        }
        finally
        {
            _pdfCollectText = false;
            _pdfTcs = null;
        }
    }

    /// <summary>WebView2 初始化失败回调。</summary>
    public void OnPdfWebInitFailed(string? message)
    {
        _pdfReady = false;
        PdfStatus = $"PDF 工具 WebView2 初始化失败：{message}";
    }

    /// <summary>处理 pdf.html 发来的消息（ready / pdf-lib-load-error / progress / done / error）。UI 线程调用。</summary>
    public void OnPdfWebMessage(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            switch (root.TryGetProperty("type", out var t) ? t.GetString() : null)
            {
                case "ready":
                    _pdfReady = true;
                    PdfStatus = "PDF 工具就绪。";
                    break;

                case "pdf-lib-load-error":
                    _pdfReady = false;
                    PdfStatus = "PDF 组件（pdf-lib / pdf.js）在线加载失败，请检查网络连接后重试。";
                    _pdfTcs?.TrySetException(new InvalidOperationException("PDF 库加载失败，请检查网络。"));
                    break;

                case "progress":
                    {
                        int done = root.TryGetProperty("done", out var dEl) && dEl.ValueKind == JsonValueKind.Number ? (int)dEl.GetDouble() : 0;
                        int total = root.TryGetProperty("total", out var tEl) && tEl.ValueKind == JsonValueKind.Number ? (int)tEl.GetDouble() : 0;
                        if (total > 0) _pdfExpected = total;
                        PdfStatus = $"PDF 处理中：{done}/{total}…";
                    }
                    break;

                case "done":
                    HandlePdfDone(root);
                    break;

                case "error":
                    var msg = root.TryGetProperty("message", out var mEl) ? mEl.GetString() : "未知错误";
                    _logger.Warning("PDF 工具 JS 错误：{Message}", msg);
                    PdfStatus = $"PDF 操作失败：{msg}";
                    _pdfTcs?.TrySetException(new InvalidOperationException(msg ?? "未知错误"));
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "处理 PDF 工具消息失败");
            _pdfTcs?.TrySetException(ex);
        }
    }

    private void HandlePdfDone(JsonElement root)
    {
        string name = root.TryGetProperty("name", out var nEl) && nEl.GetString() is { Length: > 0 } n ? n : $"output-{DateTime.Now:yyyyMMdd-HHmmss}";
        string b64 = root.TryGetProperty("dataBase64", out var dEl) && dEl.ValueKind == JsonValueKind.String ? dEl.GetString() ?? string.Empty : string.Empty;

        if (_pdfCollectText)
        {
            // pdf2text：每页一条 done（name 形如 text-page-N.txt，dataBase64 为该页 UTF-8 文本）
            var page = 1;
            var m = System.Text.RegularExpressions.Regex.Match(name, @"(\d+)");
            if (m.Success) int.TryParse(m.Groups[1].Value, out page);
            try
            {
                var text = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(b64));
                _pdfTextPages[page] = text;
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "解码 PDF 文字页失败：{Name}", name);
            }
            _pdfReceived++;
            PdfStatus = $"已提取第 {page} 页文字…";
            if (_pdfReceived >= Math.Max(1, _pdfExpected)) _pdfTcs?.TrySetResult(true);
            return;
        }

        Directory.CreateDirectory(PdfOutputDir);
        var path = UniqueOutputPath(Path.Combine(PdfOutputDir, SanitizeFileName(name)));
        File.WriteAllBytes(path, Convert.FromBase64String(b64));
        RecentOutputs.Insert(0, path);

        _pdfReceived++;
        PdfStatus = $"PDF 输出：{path}";
        if (_pdfReceived >= Math.Max(1, _pdfExpected)) _pdfTcs?.TrySetResult(true);
    }

    private async Task RunPdfOpAsync(object payload, string startStatus)
    {
        if (_pdfWeb is null || !_pdfReady)
            throw new InvalidOperationException("PDF 工具页尚未就绪（等待 WebView2 与 PDF 库加载，请稍候重试）。");
        PdfBusy = true;
        try
        {
            _pdfReceived = 0;
            _pdfExpected = 1;
            _pdfTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            PdfStatus = startStatus;
            _pdfWeb.PostWebMessageAsJson(JsonSerializer.Serialize(payload));
            await _pdfTcs.Task;
        }
        finally
        {
            _pdfTcs = null;
            PdfBusy = false;
        }
    }

    partial void OnPdfBusyChanged(bool value)
    {
        ImagesToPdfCommand.NotifyCanExecuteChanged();
        MergePdfsCommand.NotifyCanExecuteChanged();
        PdfToPngsCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanPdf))]
    private async Task ImagesToPdf()
    {
        try
        {
            var files = await PickFilesAsync(".jpg", ".jpeg", ".png");
            if (files.Count == 0) return;
            PdfStatus = $"正在读取 {files.Count} 张图片…";
            var payload = new { cmd = "img2pdf", files = await ReadPayloadsAsync(files) };
            await RunPdfOpAsync(payload, $"图片合成 PDF（{files.Count} 张）…");
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "图片转 PDF 失败");
            PdfStatus = $"图片转 PDF 失败：{ex.Message}";
        }
    }

    [RelayCommand(CanExecute = nameof(CanPdf))]
    private async Task MergePdfs()
    {
        try
        {
            var files = await PickFilesAsync(".pdf");
            if (files.Count == 0) return;
            if (files.Count < 2) { PdfStatus = "PDF 合并至少需要选择 2 个文件。"; return; }
            PdfStatus = $"正在读取 {files.Count} 个 PDF…";
            var payload = new { cmd = "merge", files = await ReadPayloadsAsync(files) };
            await RunPdfOpAsync(payload, $"合并 {files.Count} 个 PDF…");
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "PDF 合并失败");
            PdfStatus = $"PDF 合并失败：{ex.Message}";
        }
    }

    [RelayCommand(CanExecute = nameof(CanPdf))]
    private async Task PdfToPngs()
    {
        try
        {
            var files = await PickFilesAsync(".pdf");
            if (files.Count == 0) return;
            PdfStatus = "正在读取 PDF…";
            var bytes = await File.ReadAllBytesAsync(files[0]);
            var payload = new
            {
                cmd = "pdf2png",
                file = new PdfFilePayload(Path.GetFileName(files[0]), Convert.ToBase64String(bytes)),
                scale = 2,
            };
            await RunPdfOpAsync(payload, "PDF 逐页转 PNG（2 倍缩放）…");
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "PDF 转 PNG 失败");
            PdfStatus = $"PDF 转 PNG 失败：{ex.Message}";
        }
    }

    // ==================== NCM 转换（个人格式转换） ====================

    [ObservableProperty] private string _ncmStatus = "选择 .ncm 文件（可多选）后开始转换；输出 .mp3/.flac 保存在源文件所在目录。";
    [ObservableProperty] private string _ncmProgressText = string.Empty;
    [ObservableProperty] private string _ncmSummary = "未选择文件";
    [ObservableProperty] private bool _ncmBusy;

    private List<string> _ncmFiles = new();

    private bool CanConvertNcm => !NcmBusy;

    partial void OnNcmBusyChanged(bool value) => ConvertNcmCommand.NotifyCanExecuteChanged();

    [RelayCommand]
    private async Task PickNcmFiles()
    {
        try
        {
            var files = await PickFilesAsync(".ncm");
            if (files.Count == 0) return;
            _ncmFiles = files.ToList();
            NcmSummary = _ncmFiles.Count == 1
                ? Path.GetFileName(_ncmFiles[0])
                : $"{_ncmFiles.Count} 个文件（{Path.GetFileName(_ncmFiles[0])} …）";
            NcmStatus = $"已选择 {_ncmFiles.Count} 个 .ncm 文件，点击「开始转换」。";
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "选择 NCM 文件失败");
            NcmStatus = $"选择失败：{ex.Message}";
        }
    }

    [RelayCommand(CanExecute = nameof(CanConvertNcm))]
    private async Task ConvertNcm()
    {
        if (_ncmFiles.Count == 0)
        {
            NcmStatus = "请先选择 .ncm 文件。";
            return;
        }
        NcmBusy = true;
        try
        {
            int ok = 0, fail = 0;
            string lastOut = string.Empty;
            for (int i = 0; i < _ncmFiles.Count; i++)
            {
                var file = _ncmFiles[i];
                NcmProgressText = $"{i + 1}/{_ncmFiles.Count}";
                NcmStatus = $"转换中 {i + 1}/{_ncmFiles.Count}：{Path.GetFileName(file)}";
                try
                {
                    var r = await Task.Run(() => _transform.ConvertNcmAsync(file));
                    ok++;
                    lastOut = r.OutputPath;
                    RecentOutputs.Insert(0, r.OutputPath);
                }
                catch (Exception exFile)
                {
                    fail++;
                    _logger.Error(exFile, "NCM 单文件转换失败：{File}", file);
                    NcmStatus = $"失败 {Path.GetFileName(file)}：{exFile.Message}";
                }
            }
            NcmStatus = $"NCM 转换完成：成功 {ok} / 失败 {fail}。" +
                        (lastOut.Length > 0 ? $" 最新输出：{lastOut}" : string.Empty);
        }
        finally
        {
            NcmBusy = false;
            NcmProgressText = string.Empty;
        }
    }

    // ==================== 公共工具 ====================

    private static FileOpenPicker MakeOpenPicker(params string[] extensions)
    {
        var picker = new FileOpenPicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow));
        foreach (var e in extensions) picker.FileTypeFilter.Add(e);
        return picker;
    }

    private static async Task<IReadOnlyList<string>> PickFilesAsync(params string[] extensions)
    {
        var picked = await MakeOpenPicker(extensions).PickMultipleFilesAsync();
        return picked.Select(f => f.Path).ToList();
    }

    private static async Task<List<PdfFilePayload>> ReadPayloadsAsync(IReadOnlyList<string> paths)
    {
        var list = new List<PdfFilePayload>(paths.Count);
        foreach (var p in paths)
        {
            var bytes = await File.ReadAllBytesAsync(p);
            list.Add(new PdfFilePayload(Path.GetFileName(p), Convert.ToBase64String(bytes)));
        }
        return list;
    }

    private static string SanitizeFileName(string name)
    {
        var sb = new System.Text.StringBuilder(name.Length);
        foreach (var ch in name)
        {
            sb.Append(Array.IndexOf(Path.GetInvalidFileNameChars(), ch) >= 0 ? '_' : ch);
        }
        return sb.ToString().Trim();
    }

    private static string UniqueOutputPath(string path)
    {
        if (!File.Exists(path)) return path;
        var dir = Path.GetDirectoryName(path) ?? ".";
        var name = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (int i = 2; ; i++)
        {
            var candidate = Path.Combine(dir, $"{name} ({i}){ext}");
            if (!File.Exists(candidate)) return candidate;
        }
    }
}
