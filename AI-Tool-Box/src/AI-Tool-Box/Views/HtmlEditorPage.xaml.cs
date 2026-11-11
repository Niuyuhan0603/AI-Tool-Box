using AI_Tool_Box.Controls;
using AI_Tool_Box.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using Serilog;
using System;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace AI_Tool_Box.Views;

/// <summary>HTML 编辑器页：WebView2 contenteditable 富文本 + AI 改写 + 多格式导出。</summary>
public sealed partial class HtmlEditorPage : Page
{
    private const string SampleHtml = """
<h1>示例文档</h1>
<p>这是一段正文。可以直接在页面上编辑，选中文字后使用上方的 <b>AI 改写</b> / <b>AI 续写</b> 让 AI 帮你补全内容。</p>
<h2>支持的功能</h2>
<ul>
  <li>字号、段落样式、加粗/斜体/下划线/删除线</li>
  <li>文字颜色、高亮、对齐、列表、表格、链接</li>
  <li>导出 HTML / PDF / DOCX / PPTX / SVG</li>
</ul>
<h2>导出说明</h2>
<p>PDF 使用 WebView2 打印（保留排版）；DOCX/PPTX 为文本化导出；SVG 使用 foreignObject 保留 HTML 排版。</p>
<h2>演示文稿</h2>
<p>点击“插入分节线”或用 &lt;hr&gt; / &lt;section class="slide"&gt; 分页，即可导出为全屏演示播放器（点击/方向键翻页、Esc 退出）。</p>
""";

    private readonly ILogger _logger;
    private readonly IAiQuickService _ai;
    private readonly IOfficeExportService _export;
    private readonly IPresentationService _presentation;
    private bool _webReady;
    private bool _previewReady;
    private TaskCompletionSource<(string Html, string Text)>? _contentTcs;

    /// <summary>初始化页面并解析依赖服务。</summary>
    public HtmlEditorPage()
    {
        InitializeComponent();
        _logger = App.Services.GetService(typeof(ILogger)) as ILogger ?? Serilog.Log.Logger;
        _ai = App.Services.GetRequiredService<IAiQuickService>();
        _export = App.Services.GetRequiredService<IOfficeExportService>();
        _presentation = App.Services.GetRequiredService<IPresentationService>();

        Loaded += OnLoaded;
        _logger.Information("HtmlEditorPage 已加载");
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            var env = await WebShell.GetSharedEnvironmentAsync();
            await Editor.EnsureCoreWebView2Async(env);
            Editor.DefaultBackgroundColor = Windows.UI.Color.FromArgb(255, 10, 15, 20);
            // 页面实例可能被导航缓存复用：先注销再订阅，避免重复挂接
            Editor.CoreWebView2.WebMessageReceived -= OnWebMessage;
            Editor.CoreWebView2.WebMessageReceived += OnWebMessage;
            Editor.Source = new Uri(Path.Combine(AppContext.BaseDirectory, "Assets", "Web", "editor", "index.html"));
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "HTML 编辑器初始化失败");
            StatusText.Text = $"编辑器初始化失败：{ex.Message}";
        }
    }

    private void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var doc = JsonDocument.Parse(e.WebMessageAsJson);
            var root = doc.RootElement;
            var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;

            switch (type)
            {
                case "ready":
                    _webReady = true;
                    Send(new { type = "set-html", html = SampleHtml });
                    StatusText.Text = "编辑器就绪。";
                    break;

                case "html":
                    _contentTcs?.TrySetResult((
                        GetString(root, "html"),
                        GetString(root, "text")));
                    break;

                case "ai":
                    _ = HandleAiAsync(GetString(root, "mode"), GetString(root, "text"));
                    break;

                case "pick-media":
                    _ = HandlePickMediaAsync(GetString(root, "kind"));
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "处理编辑器消息失败");
        }
    }

    private static string GetString(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) ? (v.GetString() ?? string.Empty) : string.Empty;

    private void Send(object payload)
    {
        if (!_webReady || Editor.CoreWebView2 is null) return;
        Editor.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(payload));
    }

    private async Task HandleAiAsync(string mode, string text)
    {
        try
        {
            ShowBusy(true, "AI 处理中…");
            var instruction = mode switch
            {
                "rewrite" => "你是中文写作助手。请改写用户给出的片段：保持原意，语言更通顺、书面化，只输出改写后的纯文本（可用简单 Markdown）。",
                "continue" => "你是中文写作助手。请自然地续写下面这段内容（约 200–400 字），只输出续写部分，不要重复原文。",
                _ => "你是中文写作助手。请润色下面的整篇文章：保留原有结构与事实，修正语病、统一文风；只输出润色后的完整正文（可用简单 Markdown）。",
            };
            var result = await _ai.AskAsync(instruction, text);
            Send(new { type = "ai-result", mode, html = AiTextToHtml(result) });
            ShowBusy(false, "AI 结果已应用。");
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "AI 处理失败");
            Send(new { type = "ai-error", message = ex.Message });
            ShowBusy(false, $"AI 失败：{ex.Message}");
        }
    }

    /// <summary>把 AI 返回的简单 Markdown 转成 HTML 片段。</summary>
    private static string AiTextToHtml(string text)
    {
        var sb = new StringBuilder();
        foreach (var raw in (text ?? string.Empty).Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line.Length == 0) continue;
            var encoded = WebUtility.HtmlEncode(line).Replace("**", string.Empty);
            if (line.StartsWith("### ")) sb.Append($"<h3>{WebUtility.HtmlEncode(line[4..])}</h3>");
            else if (line.StartsWith("## ")) sb.Append($"<h2>{WebUtility.HtmlEncode(line[3..])}</h2>");
            else if (line.StartsWith("# ")) sb.Append($"<h1>{WebUtility.HtmlEncode(line[2..])}</h1>");
            else if (line.StartsWith("- ") || line.StartsWith("* ")) sb.Append($"<p>• {WebUtility.HtmlEncode(line[2..])}</p>");
            else sb.Append($"<p>{encoded}</p>");
        }
        return sb.ToString();
    }

    private void ShowBusy(bool busy, string status)
    {
        BusyRing.IsActive = busy;
        StatusText.Text = status;
    }

    // ==================== 导出 ====================

    private async Task<(string Html, string Text)> RequestContentAsync()
    {
        if (!_webReady) return (string.Empty, string.Empty);
        _contentTcs = new TaskCompletionSource<(string, string)>(TaskCreationOptions.RunContinuationsAsynchronously);
        Send(new { type = "get-html" });
        var task = _contentTcs.Task;
        var done = await Task.WhenAny(task, Task.Delay(3000));
        _contentTcs = null;
        return done == task ? task.Result : (string.Empty, string.Empty);
    }

    private async Task<string?> PickSaveAsync(string typeName, string ext, string? suggestedName = null)
    {
        var picker = new FileSavePicker();
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
        picker.FileTypeChoices.Add(typeName, new[] { ext });
        picker.SuggestedFileName = suggestedName ?? $"document-{DateTime.Now:yyyyMMdd-HHmmss}";
        var file = await picker.PickSaveFileAsync();
        return file?.Path;
    }

    /// <summary>弹出文件选择器选取媒体（图片/视频/音频），把本地路径回传给编辑器插入。</summary>
    private async Task HandlePickMediaAsync(string kind)
    {
        try
        {
            var picker = new FileOpenPicker();
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
            switch (kind)
            {
                case "video":
                    picker.SuggestedStartLocation = PickerLocationId.VideosLibrary;
                    foreach (var e in new[] { ".mp4", ".webm", ".ogg", ".mov", ".m4v" }) picker.FileTypeFilter.Add(e);
                    break;
                case "audio":
                    picker.SuggestedStartLocation = PickerLocationId.MusicLibrary;
                    foreach (var e in new[] { ".mp3", ".wav", ".m4a", ".aac", ".ogg", ".flac" }) picker.FileTypeFilter.Add(e);
                    break;
                default:
                    picker.SuggestedStartLocation = PickerLocationId.PicturesLibrary;
                    foreach (var e in new[] { ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".svg" }) picker.FileTypeFilter.Add(e);
                    break;
            }
            var file = await picker.PickSingleFileAsync();
            if (file is null) return;
            Send(new { type = "insert-media", kind, src = new Uri(file.Path).AbsoluteUri, name = file.Name });
            StatusText.Text = $"已插入{KindLabel(kind)}：{file.Name}（导出演示文稿时会复制到同名 .assets 文件夹）";
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "选择媒体文件失败");
            StatusText.Text = $"选择媒体失败：{ex.Message}";
        }
    }

    private static string KindLabel(string kind) => kind switch
    {
        "video" => "视频",
        "audio" => "音频",
        _ => "图片",
    };

    private void OnClearClick(object sender, RoutedEventArgs e)
    {
        Send(new { type = "set-html", html = string.Empty });
        StatusText.Text = "已清空。";
    }

    private void OnSampleClick(object sender, RoutedEventArgs e)
    {
        Send(new { type = "set-html", html = SampleHtml });
        StatusText.Text = "已插入示例内容。";
    }

    private async void OnExportHtmlClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var (html, _) = await RequestContentAsync();
            if (string.IsNullOrWhiteSpace(html)) { StatusText.Text = "没有可导出的内容。"; return; }
            var path = await PickSaveAsync("HTML 网页", ".html");
            if (path is null) return;
            const string head = "<!DOCTYPE html>\n<html lang=\"zh-CN\"><head><meta charset=\"utf-8\"><title>文档</title>\n"
                + "<style>body{max-width:900px;margin:0 auto;padding:24px;font-family:'Segoe UI','Microsoft YaHei',sans-serif;"
                + "line-height:1.75;color:#111418}table{border-collapse:collapse;width:100%}td,th{border:1px solid #99A3AD;"
                + "padding:6px 8px}img{max-width:100%}</style>\n</head><body>";
            var full = head + html + "</body></html>";
            await File.WriteAllTextAsync(path, full, new UTF8Encoding(false));
            StatusText.Text = $"已导出 HTML：{path}";
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "导出 HTML 失败");
            StatusText.Text = $"导出失败：{ex.Message}";
        }
    }

    private async void OnExportPdfClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var (html, _) = await RequestContentAsync();
            if (string.IsNullOrWhiteSpace(html)) { StatusText.Text = "没有可导出的内容。"; return; }
            var path = await PickSaveAsync("PDF 文档", ".pdf");
            if (path is null) return;
            ShowBusy(true, "正在生成 PDF…");
            var settings = Editor.CoreWebView2.Environment.CreatePrintSettings();
            settings.ShouldPrintBackgrounds = true;
            await Editor.CoreWebView2.PrintToPdfAsync(path, settings);
            ShowBusy(false, $"已导出 PDF：{path}");
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "导出 PDF 失败");
            ShowBusy(false, $"导出 PDF 失败：{ex.Message}");
        }
    }

    private async void OnExportDocxClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var (html, _) = await RequestContentAsync();
            if (string.IsNullOrWhiteSpace(html)) { StatusText.Text = "没有可导出的内容。"; return; }
            var path = await PickSaveAsync("Word 文档", ".docx");
            if (path is null) return;
            ShowBusy(true, "正在生成 DOCX…");
            await _export.ExportDocxFromHtmlAsync(html, path);
            ShowBusy(false, $"已导出 DOCX：{path}");
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "导出 DOCX 失败");
            ShowBusy(false, $"导出 DOCX 失败：{ex.Message}");
        }
    }

    private async void OnExportPptxClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var (html, _) = await RequestContentAsync();
            if (string.IsNullOrWhiteSpace(html)) { StatusText.Text = "没有可导出的内容。"; return; }
            var path = await PickSaveAsync("PowerPoint 演示文稿", ".pptx");
            if (path is null) return;
            ShowBusy(true, "正在生成 PPTX…");
            _export.ExportPptxFromHtml(html, path);
            ShowBusy(false, $"已导出 PPTX：{path}");
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "导出 PPTX 失败");
            ShowBusy(false, $"导出 PPTX 失败：{ex.Message}");
        }
    }

    private async void OnExportSvgClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var (html, _) = await RequestContentAsync();
            if (string.IsNullOrWhiteSpace(html)) { StatusText.Text = "没有可导出的内容。"; return; }
            var path = await PickSaveAsync("SVG 图片", ".svg");
            if (path is null) return;
            await File.WriteAllTextAsync(path, _export.BuildSvgFromHtml(html), new UTF8Encoding(false));
            StatusText.Text = $"已导出 SVG：{path}";
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "导出 SVG 失败");
            StatusText.Text = $"导出失败：{ex.Message}";
        }
    }

    // ==================== 演示文稿 ====================

    /// <summary>演示模式预览：把当前内容按约定切分为幻灯片，加载进应用内全屏播放器覆盖层。</summary>
    private async void OnPreviewDeckClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var (html, _) = await RequestContentAsync();
            if (string.IsNullOrWhiteSpace(html)) { StatusText.Text = "没有可预览的内容。"; return; }

            var slides = _presentation.SplitSlides(html);
            var player = _presentation.BuildPlayerHtml(slides, "演示文稿预览");

            // 写入临时文件再导航：file:// 源可正常加载媒体（相对/绝对引用均可）
            var tmpDir = Path.Combine(Path.GetTempPath(), "AI-Tool-Box");
            Directory.CreateDirectory(tmpDir);
            var tmpFile = Path.Combine(tmpDir, $"deck-preview-{Guid.NewGuid():N}.html");
            await File.WriteAllTextAsync(tmpFile, player, new UTF8Encoding(false));

            var env = await WebShell.GetSharedEnvironmentAsync();
            DeckPreviewOverlay.Visibility = Visibility.Visible;
            await DeckPreview.EnsureCoreWebView2Async(env);
            _previewReady = true;
            DeckPreview.CoreWebView2.Navigate(new Uri(tmpFile).AbsoluteUri);

            StatusText.Text = $"演示预览中：共 {slides.Count} 页（点击页面前进、方向键翻页、Esc 退出）。";
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "演示文稿预览失败");
            StatusText.Text = $"预览失败：{ex.Message}";
        }
    }

    /// <summary>退出演示预览：隐藏覆盖层并停播媒体（导航到空白页）。</summary>
    private void OnExitPreviewClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_previewReady && DeckPreview.CoreWebView2 is not null)
            {
                DeckPreview.CoreWebView2.Navigate("about:blank");
            }
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "退出预览时停止播放失败");
        }
        DeckPreviewOverlay.Visibility = Visibility.Collapsed;
        StatusText.Text = "已退出演示预览。";
    }

    /// <summary>导出演示文稿 HTML：写便携播放器；含本地媒体时生成同名 .assets 文件夹并复制媒体。</summary>
    private async void OnExportDeckClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var (html, _) = await RequestContentAsync();
            if (string.IsNullOrWhiteSpace(html)) { StatusText.Text = "没有可导出的内容。"; return; }
            var path = await PickSaveAsync("演示文稿 HTML", ".html", $"presentation-{DateTime.Now:yyyyMMdd-HHmmss}");
            if (path is null) return;
            ShowBusy(true, "正在生成演示文稿…");
            var result = await _presentation.ExportAsync(html, path, Path.GetFileNameWithoutExtension(path));
            var mediaNote = result.MediaCount > 0
                ? $"，媒体 {result.MediaCount} 个 → {result.AssetFolder}"
                : "，无嵌入媒体";
            ShowBusy(false, $"已导出演示文稿：{result.HtmlPath}（{result.SlideCount} 页{mediaNote}）");
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "导出演示文稿失败");
            ShowBusy(false, $"导出演示文稿失败：{ex.Message}");
        }
    }
}
