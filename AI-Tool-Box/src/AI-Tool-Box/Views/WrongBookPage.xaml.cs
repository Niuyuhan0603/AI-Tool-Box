using AI_Tool_Box.Controls;
using AI_Tool_Box.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.Web.WebView2.Core;
using Serilog;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Windows.Storage.Pickers;

namespace AI_Tool_Box.Views;

/// <summary>错题本页：本地 JSON 存储 + 检索/标签/掌握标记 + 图片 OCR 录入 + AI 出题与讲解。</summary>
public sealed partial class WrongBookPage : Page
{
    private static readonly Uri PdfToolHtmlUri = new(AppContext.BaseDirectory + "Assets/Web/pdf.html");

    private readonly ILogger _logger;
    private readonly IWrongBookService _store;
    private readonly IAiQuickService _ai;
    private readonly ILiteParseOcrService _ocr;
    private readonly ITransformService _transform;
    private readonly ObservableCollection<WrongQuestion> _view = new();
    private WrongQuestion? _current;
    private bool _ready;

    // PDF 工具页（pdf-lib）请求/响应配对
    private readonly Dictionary<int, TaskCompletionSource<JsonElement>> _pendingPdf = new();
    private int _reqSeq;
    private bool _pdfToolReady;

    // HTML 打印 WebView2 导航同步
    private TaskCompletionSource<bool>? _htmlNavTcs;
    private bool _htmlWebReady;

    // HTML 片段预览 WebView2（编辑区）
    private bool _htmlPreviewReady;

    /// <summary>初始化页面。</summary>
    public WrongBookPage()
    {
        InitializeComponent();
        _logger = App.Services.GetService(typeof(ILogger)) as ILogger ?? Serilog.Log.Logger;
        _store = App.Services.GetRequiredService<IWrongBookService>();
        _ai = App.Services.GetRequiredService<IAiQuickService>();
        _ocr = App.Services.GetRequiredService<ILiteParseOcrService>();
        _transform = App.Services.GetRequiredService<ITransformService>();
        QuestionList.ItemsSource = _view;

        // 隐藏 PDF 工具页（pdf-lib：单页裁剪 / 图片转 PDF / 合并）
        PdfToolWeb.Source = PdfToolHtmlUri;
        Loaded += async (_, _) =>
        {
            // 注意：XAML 里 ComboBox 的 SelectedIndex="0" 会在 InitializeComponent 期间触发
            // SelectionChanged，此时字段（_store 等）尚未赋值，必须等待 Loaded 后再刷新列表。
            _ready = true;
            RefreshList();
            try
            {
                await PdfToolWeb.EnsureCoreWebView2Async(await WebShell.GetSharedEnvironmentAsync());
                if (!_pdfToolReady)
                {
                    PdfToolWeb.CoreWebView2.WebMessageReceived += OnPdfToolMessageReceived;
                    _pdfToolReady = true;
                }
                await EnsureHtmlWebAsync();
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "错题本隐藏 WebView2 初始化失败");
            }
            _logger.Information("WrongBookPage 已加载");
        };
    }

    // ==================== 列表 ====================

    private void RefreshList()
    {
        if (!_ready || _store is null) return;
        var keyword = SearchBox.Text?.Trim() ?? string.Empty;
        var subject = SubjectFilter.SelectedIndex > 0 ? SubjectFilter.SelectedItem as string : null;

        var items = _store.Items.AsEnumerable();
        if (!string.IsNullOrEmpty(keyword))
            items = items.Where(q => q.Question.Contains(keyword, StringComparison.OrdinalIgnoreCase)
                                     || q.Tags.Contains(keyword, StringComparison.OrdinalIgnoreCase)
                                     || q.Answer.Contains(keyword, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrEmpty(subject))
            items = items.Where(q => q.Subject == subject);

        _view.Clear();
        foreach (var q in items) _view.Add(q);
        var total = _store.Items.Count;
        var mastered = _store.Items.Count(x => x.Mastered);
        var reviews = _store.Items.Sum(x => x.ReviewCount);
        StatusText.Text = $"筛选 {_view.Count} 条 / 总计 {total} 条（已掌握 {mastered}，未掌握 {total - mastered}，累计复习 {reviews} 次）。";
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e) => RefreshList();

    private void OnFilterChanged(object sender, SelectionChangedEventArgs e) => RefreshList();

    private async void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (QuestionList.SelectedItem is not WrongQuestion q) return;
        _current = q;
        SubjectBox.SelectedItem = q.Subject;
        TagsBox.Text = q.Tags;
        QuestionBox.Text = q.Question;
        AnswerBox.Text = q.Answer;
        AnalysisBox.Text = q.Analysis;
        await UpdateVisualsAsync(q);
        StatusText.Text = $"选中：{q.Subject}｜复习 {q.ReviewCount} 次｜{(q.Mastered ? "已掌握" : "未掌握")}";
    }

    // ==================== 可视化内容（题目/答案图片、HTML 片段） ====================

    /// <summary>根据当前条目刷新题目/答案图片与 HTML 片段预览（图片型「题目/答案」栏）。</summary>
    private async Task UpdateVisualsAsync(WrongQuestion? q)
    {
        SetImagePanel(QuestionImagePanel, QuestionImage, q?.ThumbnailPath);
        SetImagePanel(AnswerImagePanel, AnswerImage, q?.AnswerImagePath);

        var html = q is not null && string.Equals(q.Kind, "html", StringComparison.OrdinalIgnoreCase)
            ? q.HtmlContent
            : string.Empty;
        if (!string.IsNullOrWhiteSpace(html))
        {
            HtmlPanel.Visibility = Visibility.Visible;
            try
            {
                await EnsureHtmlPreviewAsync();
                HtmlPreview.NavigateToString(WrapPreviewHtml(html));
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "HTML 片段预览失败");
                HtmlPanel.Visibility = Visibility.Collapsed;
            }
        }
        else
        {
            HtmlPanel.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>按路径加载图片到面板；路径为空或文件不存在时隐藏面板。</summary>
    private static void SetImagePanel(Border panel, Image image, string? path)
    {
        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
        {
            try
            {
                image.Source = new BitmapImage(new Uri(path));
                panel.Visibility = Visibility.Visible;
                return;
            }
            catch
            {
                // 加载失败则按无图片处理
            }
        }
        image.Source = null;
        panel.Visibility = Visibility.Collapsed;
    }

    private async Task EnsureHtmlPreviewAsync()
    {
        if (_htmlPreviewReady && HtmlPreview.CoreWebView2 is not null) return;
        await HtmlPreview.EnsureCoreWebView2Async(await WebShell.GetSharedEnvironmentAsync());
        _htmlPreviewReady = true;
    }

    private static string WrapPreviewHtml(string body) =>
        "<!DOCTYPE html><html lang=\"zh-CN\"><head><meta charset=\"utf-8\"><style>"
        + "html,body{margin:0;padding:10px;font-family:'Segoe UI','Microsoft YaHei',sans-serif;"
        + "font-size:15px;line-height:1.8;color:#111418;word-wrap:break-word}"
        + "img{max-width:100%}table{border-collapse:collapse}td,th{border:1px solid #99A3AD;padding:4px 6px}"
        + "p{margin:0 0 8px}"
        + "</style></head><body>" + body + "</body></html>";

    private void OnRemoveQuestionImageClick(object sender, RoutedEventArgs e) => RemoveImage(true);

    private void OnRemoveAnswerImageClick(object sender, RoutedEventArgs e) => RemoveImage(false);

    private void RemoveImage(bool isQuestion)
    {
        if (_current is null)
        {
            StatusText.Text = "请先选择条目。";
            return;
        }
        if (isQuestion)
        {
            _current.ThumbnailPath = string.Empty;
            QuestionImage.Source = null;
            QuestionImagePanel.Visibility = Visibility.Collapsed;
        }
        else
        {
            _current.AnswerImagePath = string.Empty;
            AnswerImage.Source = null;
            AnswerImagePanel.Visibility = Visibility.Collapsed;
        }
        _store.Upsert(_current);
        StatusText.Text = "已移除图片。";
    }

    // ==================== 编辑 ====================

    private void OnNewClick(object sender, RoutedEventArgs e)
    {
        _current = null;
        QuestionList.SelectedItem = null;
        QuestionBox.Text = string.Empty;
        AnswerBox.Text = string.Empty;
        AnalysisBox.Text = string.Empty;
        TagsBox.Text = string.Empty;
        if (SubjectFilter.SelectedIndex > 0) SubjectBox.SelectedItem = SubjectFilter.SelectedItem;
        _ = UpdateVisualsAsync(null);
        StatusText.Text = "新建条目：填写题干后点击「保存」。";
    }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(QuestionBox.Text))
        {
            StatusText.Text = "题干不能为空。";
            return;
        }
        var item = _current ?? new WrongQuestion();
        item.Subject = SubjectBox.SelectedItem as string ?? "未分类";
        item.Tags = TagsBox.Text?.Trim() ?? string.Empty;
        item.Question = QuestionBox.Text.Trim();
        item.Answer = AnswerBox.Text?.Trim() ?? string.Empty;
        item.Analysis = AnalysisBox.Text?.Trim() ?? string.Empty;
        _store.Upsert(item);
        _current = item;
        RefreshList();
        StatusText.Text = "已保存。";
    }

    private async void OnDeleteClick(object sender, RoutedEventArgs e)
    {
        if (_current is null)
        {
            StatusText.Text = "请先选择要删除的条目。";
            return;
        }
        var dialog = new ContentDialog
        {
            Title = "删除错题",
            Content = "确定删除当前条目吗？此操作不可撤销。",
            PrimaryButtonText = "删除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        _store.Delete(_current.Id);
        _current = null;
        OnNewClick(sender, e);
        RefreshList();
        StatusText.Text = "已删除。";
    }

    private void OnToggleMasteredClick(object sender, RoutedEventArgs e)
    {
        if (_current is null)
        {
            StatusText.Text = "请先选择条目。";
            return;
        }
        _current.Mastered = !_current.Mastered;
        _store.Upsert(_current);
        RefreshList();
        StatusText.Text = _current.Mastered ? "已标记为掌握。" : "已取消掌握标记。";
    }

    private void OnReviewClick(object sender, RoutedEventArgs e)
    {
        if (_current is null)
        {
            StatusText.Text = "请先选择条目。";
            return;
        }
        _current.ReviewCount++;
        _current.LastReviewedAt = DateTime.Now;
        _store.Upsert(_current);
        RefreshList();
        StatusText.Text = $"已记录一次复习（累计 {_current.ReviewCount} 次）。";
    }

    // ==================== AI ====================

    private async void OnAiGenerateClick(object sender, RoutedEventArgs e)
    {
        var source = QuestionBox.Text;
        if (string.IsNullOrWhiteSpace(source))
        {
            StatusText.Text = "请先填写题干（或选中已有错题），再让 AI 生成同类题。";
            return;
        }
        BusyRing.IsActive = true;
        try
        {
            StatusText.Text = $"AI（{_ai.Describe()}）生成同类题中…";
            var result = await _ai.AskAsync(
                "你是学科老师。请根据给出的错题，生成 1 道同知识点、同难度但情境不同的新题；"
                + "严格使用标记输出：【题目】题目正文【答案】答案【解析】解析。不要输出其它内容。",
                source);
            _current = null;
            QuestionList.SelectedItem = null;
            var q = ExtractSection(result, "题目");
            var a = ExtractSection(result, "答案");
            var s = ExtractSection(result, "解析");
            QuestionBox.Text = string.IsNullOrEmpty(q) ? result : q;
            AnswerBox.Text = a;
            AnalysisBox.Text = s;
            StatusText.Text = "AI 已生成新题并拆分到题干/答案/解析，可修改后保存。";
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "AI 生成同类题失败");
            StatusText.Text = $"AI 失败：{ex.Message}";
        }
        finally
        {
            BusyRing.IsActive = false;
        }
    }

    private async void OnAiExplainClick(object sender, RoutedEventArgs e)
    {
        var question = QuestionBox.Text;
        if (string.IsNullOrWhiteSpace(question))
        {
            StatusText.Text = "请先填写题干。";
            return;
        }
        BusyRing.IsActive = true;
        try
        {
            StatusText.Text = $"AI（{_ai.Describe()}）讲解中…";
            var content = $"题目：{question}\n\n原答案：{AnswerBox.Text}\n\n已知解析：{AnalysisBox.Text}";
            AnalysisBox.Text = await _ai.AskAsync(
                "你是耐心的学科老师。请讲解这道题：给出正确解法（分步骤）、指出常见错误，并把讲解写成可直接粘贴到「解析/错因」栏的纯文本（可用简单 Markdown）。",
                content);
            StatusText.Text = "AI 讲解已填入解析栏，记得点击「保存」。";
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "AI 讲解失败");
            StatusText.Text = $"AI 失败：{ex.Message}";
        }
        finally
        {
            BusyRing.IsActive = false;
        }
    }

    private static string ExtractSection(string text, string tag)
    {
        var start = text.IndexOf($"【{tag}】", StringComparison.Ordinal);
        if (start < 0) return string.Empty;
        start += tag.Length + 2;
        var next = text.IndexOf('【', start);
        return (next < 0 ? text[start..] : text[start..next]).Trim();
    }

    // ==================== OCR 录入 / 导入导出 ====================

    private async void OnOcrImportClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FileOpenPicker
            {
                ViewMode = PickerViewMode.Thumbnail,
                SuggestedStartLocation = PickerLocationId.PicturesLibrary
            };
            foreach (var ext in new[] { ".png", ".jpg", ".jpeg", ".bmp", ".tif", ".tiff", ".webp" })
            {
                picker.FileTypeFilter.Add(ext);
            }
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow!));

            var file = await picker.PickSingleFileAsync();
            if (file is null) return;

            var lang = await ResolveOcrLanguageAsync();
            if (lang is null)
            {
                StatusText.Text = "没有可用的 OCR 语言数据（LiteParse2.0 缺少 traineddata），请检查「OCR 文字识别」页的引擎目录。";
                return;
            }

            BusyRing.IsActive = true;
            StatusText.Text = $"正在识别图片文字（{lang.DisplayName}）…";
            var text = await _ocr.RecognizeAsync(file.Path, lang.Code);
            if (string.IsNullOrWhiteSpace(text))
            {
                StatusText.Text = "未识别到文字，请换一张更清晰的图片。";
                return;
            }

            AppendToQuestion(text);
            StatusText.Text = $"识别完成（{lang.DisplayName}），已追加到题干；可点「AI 拆分题干/答案」整理格式。";
            _logger.Information("错题本图片识别录入：{File}", file.Path);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "错题本图片识别录入失败");
            StatusText.Text = $"识别失败：{ex.Message}";
        }
        finally
        {
            BusyRing.IsActive = false;
        }
    }

    /// <summary>导入文件：打开预览对话框，直接预览源文件（PDF 原文 / DOCX 渲染 HTML / 图片），
    /// 由用户按页局部选取题目后加入错题本（不再先行 OCR）。</summary>
    private async void OnImportFileClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FileOpenPicker
            {
                ViewMode = PickerViewMode.List,
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary
            };
            foreach (var ext in new[] { ".pdf", ".docx",
                         ".png", ".jpg", ".jpeg", ".bmp", ".tif", ".tiff", ".webp" })
            {
                picker.FileTypeFilter.Add(ext);
            }
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow!));

            var file = await picker.PickSingleFileAsync();
            if (file is null) return;

            var dialog = new WrongBookImportDialog(_logger, _transform, file.Path) { XamlRoot = XamlRoot };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

            var added = dialog.Selected;
            if (added.Count == 0)
            {
                StatusText.Text = "未选取任何题目区域。";
                return;
            }
            foreach (var q in added)
            {
                if (string.IsNullOrWhiteSpace(q.Id)) q.Id = Guid.NewGuid().ToString("N");
                _store.Upsert(q);
            }
            RefreshList();
            // 自动选中本次加入的第一条，让「题目」栏立即展示其缩略图/内容
            var firstAdded = added[0];
            var match = _view.FirstOrDefault(v => v.Id == firstAdded.Id);
            if (match is not null) QuestionList.SelectedItem = match;
            StatusText.Text = $"已从《{file.Name}》加入 {added.Count} 道错题。";
            _logger.Information("错题本导入文件：{File}，{Count} 条", file.Path, added.Count);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "错题本文件导入失败");
            StatusText.Text = $"导入失败：{ex.Message}";
        }
    }

    /// <summary>把新文本追加到题干（已有内容时用空行分隔）。</summary>
    private void AppendToQuestion(string text)
    {
        var existing = QuestionBox.Text?.Trim() ?? string.Empty;
        QuestionBox.Text = string.IsNullOrEmpty(existing) ? text.Trim() : existing + "\n\n" + text.Trim();
    }

    private async Task<OcrLanguageInfo?> ResolveOcrLanguageAsync()
    {
        var languages = await _ocr.GetLanguagesAsync();
        var installed = languages.Where(l => l.IsInstalled).ToList();
        return installed.FirstOrDefault(l => l.Code.StartsWith("chi_sim", StringComparison.OrdinalIgnoreCase))
               ?? installed.FirstOrDefault(l => l.Code.StartsWith("eng", StringComparison.OrdinalIgnoreCase))
               ?? installed.FirstOrDefault();
    }

    // ==================== 统一导出为单个 PDF ====================

    /// <summary>把列表当前（筛选后）的全部错题渲染为 PDF 字节，合并后另存为单个 PDF 文件。</summary>
    private async void OnExportPdfClick(object sender, RoutedEventArgs e)
    {
        if (_view.Count == 0)
        {
            StatusText.Text = "列表中暂无可导出的错题。";
            return;
        }
        BusyRing.IsActive = true;
        try
        {
            var parts = new List<(string Name, byte[] Data)>();
            for (int i = 0; i < _view.Count; i++)
            {
                var q = _view[i];
                StatusText.Text = $"正在渲染 {i + 1}/{_view.Count}：{q.Display}";
                try
                {
                    var part = await RenderItemToPdfAsync(q);
                    if (part is not null) parts.Add(part.Value);
                }
                catch (Exception ex)
                {
                    // 单项失败不影响整体导出，跳过并记录
                    _logger.Warning(ex, "渲染错题项失败，已跳过：{Id}", q.Id);
                }
            }
            if (parts.Count == 0)
            {
                StatusText.Text = "未能生成任何 PDF 页。";
                return;
            }

            (string Name, byte[] Data) final;
            if (parts.Count == 1)
            {
                final = parts[0];
            }
            else
            {
                StatusText.Text = $"正在合并 {parts.Count} 页为单个 PDF…";
                final = await CallPdfToolAsync(new Dictionary<string, object?>
                {
                    ["cmd"] = "merge",
                    ["files"] = parts.Select(p => new { name = p.Name, dataBase64 = Convert.ToBase64String(p.Data) }).ToArray()
                });
            }

            var picker = new FileSavePicker
            {
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
                SuggestedFileName = $"错题本-{DateTime.Now:yyyyMMdd-HHmm}"
            };
            picker.FileTypeChoices.Add("PDF 文档", new List<string> { ".pdf" });
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow!));
            var file = await picker.PickSaveFileAsync();
            if (file is null)
            {
                StatusText.Text = "已取消保存。";
                return;
            }
            await File.WriteAllBytesAsync(file.Path, final.Data);
            StatusText.Text = $"已导出 {parts.Count} 道错题为单个 PDF：{file.Path}";
            _logger.Information("错题本导出 PDF：{File}（{Count} 页，{KB} KB）", file.Path, parts.Count, final.Data.Length / 1024);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "错题本导出 PDF 失败");
            StatusText.Text = $"导出失败：{ex.Message}";
        }
        finally
        {
            BusyRing.IsActive = false;
        }
    }

    /// <summary>按错题类型渲染为单个 PDF：
    /// pdf→pdf-lib 抽取该页并裁剪 CropBox；image→pdf-lib 图片转 PDF；html/纯文本→WebView2 打印为 PDF。</summary>
    private async Task<(string Name, byte[] Data)?> RenderItemToPdfAsync(WrongQuestion q)
    {
        var kind = string.IsNullOrEmpty(q.Kind) ? "none" : q.Kind.ToLowerInvariant();
        switch (kind)
        {
            case "pdf":
            {
                if (string.IsNullOrEmpty(q.SourcePath) || !File.Exists(q.SourcePath)) return null;
                var bytes = await File.ReadAllBytesAsync(q.SourcePath);
                var payload = new Dictionary<string, object?>
                {
                    ["cmd"] = "cropPage",
                    ["file"] = new { name = Path.GetFileName(q.SourcePath), dataBase64 = Convert.ToBase64String(bytes) },
                    ["page"] = q.SourcePage <= 0 ? 1 : q.SourcePage
                };
                if (q.CropBox is { Length: 4 }) payload["box"] = q.CropBox;
                return await CallPdfToolAsync(payload);
            }
            case "image":
            {
                if (string.IsNullOrEmpty(q.SourcePath) || !File.Exists(q.SourcePath)) return null;
                var bytes = await File.ReadAllBytesAsync(q.SourcePath);
                return await CallPdfToolAsync(new Dictionary<string, object?>
                {
                    ["cmd"] = "img2pdf",
                    ["files"] = new[] { new { name = Path.GetFileName(q.SourcePath), dataBase64 = Convert.ToBase64String(bytes) } }
                });
            }
            default:
            {
                var data = await HtmlToPdfAsync(BuildItemHtml(q));
                return ($"wb-{q.Id}.pdf", data);
            }
        }
    }

    /// <summary>把错题（题干/HTML 片段/答案/解析/标签）拼成可打印的 HTML 片段。</summary>
    private static string BuildItemHtml(WrongQuestion q)
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(q.Question))
        {
            sb.Append("<h3>").Append(WebUtility.HtmlEncode(q.Question)).Append("</h3>");
        }
        if (string.Equals(q.Kind, "html", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(q.HtmlContent))
        {
            sb.Append("<div class=\"frag\">").Append(q.HtmlContent).Append("</div>");
        }
        if (!string.IsNullOrWhiteSpace(q.Answer))
        {
            sb.Append("<p><b>答案：</b>").Append(WebUtility.HtmlEncode(q.Answer)).Append("</p>");
        }
        if (!string.IsNullOrWhiteSpace(q.Analysis))
        {
            sb.Append("<p><b>解析/错因：</b>").Append(WebUtility.HtmlEncode(q.Analysis)).Append("</p>");
        }
        if (!string.IsNullOrWhiteSpace(q.Tags))
        {
            sb.Append("<p class=\"tag\">标签：").Append(WebUtility.HtmlEncode(q.Tags)).Append("</p>");
        }
        return sb.ToString();
    }

    // ==================== HTML → PDF（WebView2 打印） ====================

    private async Task EnsureHtmlWebAsync()
    {
        if (_htmlWebReady && HtmlWeb.CoreWebView2 is not null) return;
        await HtmlWeb.EnsureCoreWebView2Async(await WebShell.GetSharedEnvironmentAsync());
        if (!_htmlWebReady)
        {
            HtmlWeb.CoreWebView2.NavigationCompleted += OnHtmlNavigationCompleted;
            _htmlWebReady = true;
        }
    }

    private void OnHtmlNavigationCompleted(CoreWebView2 sender, CoreWebView2NavigationCompletedEventArgs args)
    {
        _htmlNavTcs?.TrySetResult(args.IsSuccess);
    }

    /// <summary>把 HTML 片段经隐藏 WebView2 打印为 PDF 字节。</summary>
    private async Task<byte[]> HtmlToPdfAsync(string bodyHtml)
    {
        await EnsureHtmlWebAsync();
        if (HtmlWeb.CoreWebView2 is null) throw new InvalidOperationException("HTML 打印组件尚未就绪。");

        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _htmlNavTcs = tcs;
        HtmlWeb.NavigateToString(WrapPrintHtml(bodyHtml));
        if (!await tcs.Task) throw new InvalidOperationException("HTML 页面加载失败。");

        var tmp = Path.Combine(Path.GetTempPath(), $"aitoolbox-wb-{Guid.NewGuid():N}.pdf");
        try
        {
            var settings = HtmlWeb.CoreWebView2.Environment.CreatePrintSettings();
            settings.ShouldPrintBackgrounds = true;
            await HtmlWeb.CoreWebView2.PrintToPdfAsync(tmp, settings);
            return await File.ReadAllBytesAsync(tmp);
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* 临时文件清理失败不影响导出 */ }
        }
    }

    private static string WrapPrintHtml(string body) =>
        "<!DOCTYPE html><html lang=\"zh-CN\"><head><meta charset=\"utf-8\"><style>"
        + "@page{margin:16mm}"
        + "body{margin:0;font-family:'Segoe UI','Microsoft YaHei',sans-serif;font-size:14px;line-height:1.7;color:#111418}"
        + "h1,h2,h3{line-height:1.4;margin:0 0 8px}img{max-width:100%}"
        + "table{border-collapse:collapse}td,th{border:1px solid #99A3AD;padding:4px 6px}"
        + ".frag{border:1px solid #d9dee3;border-radius:6px;padding:8px 10px;margin:6px 0}"
        + ".tag{color:#5b6570;font-size:12px}</style></head><body>" + body + "</body></html>";

    // ==================== PDF 工具页（pdf-lib）桥接 ====================

    private void OnPdfToolMessageReceived(CoreWebView2 sender, CoreWebView2WebMessageReceivedEventArgs args)
    {
        try
        {
            using var doc = JsonDocument.Parse(args.WebMessageAsJson);
            var root = doc.RootElement;
            var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;
            if (type != "done" && type != "error") return; // ready/progress 等忽略
            if (!root.TryGetProperty("id", out var idEl) || idEl.ValueKind != JsonValueKind.Number) return;
            if (!_pendingPdf.TryGetValue(idEl.GetInt32(), out var tcs)) return;

            if (type == "error")
            {
                var msg = root.TryGetProperty("message", out var m) ? m.GetString() : "未知错误";
                tcs.TrySetException(new InvalidOperationException(msg));
            }
            else
            {
                tcs.TrySetResult(root.Clone());
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "处理 PDF 工具页消息失败");
        }
    }

    /// <summary>向 pdf.html 发送指令并等待配对（id）的单条 done 结果。</summary>
    private async Task<(string Name, byte[] Data)> CallPdfToolAsync(Dictionary<string, object?> payload)
    {
        if (PdfToolWeb.CoreWebView2 is null)
        {
            throw new InvalidOperationException("PDF 工具页尚未就绪，请稍候重试。");
        }
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        var id = Interlocked.Increment(ref _reqSeq);
        _pendingPdf[id] = tcs;
        payload["id"] = id;
        try
        {
            PdfToolWeb.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(payload));
            var root = await tcs.Task;
            var name = root.TryGetProperty("name", out var n) ? n.GetString() ?? "out.pdf" : "out.pdf";
            var b64 = root.TryGetProperty("dataBase64", out var b) ? b.GetString() ?? string.Empty : string.Empty;
            return (name, Convert.FromBase64String(b64));
        }
        finally
        {
            _pendingPdf.Remove(id);
        }
    }

    private async void OnAiSplitClick(object sender, RoutedEventArgs e)
    {
        var raw = QuestionBox.Text;
        if (string.IsNullOrWhiteSpace(raw))
        {
            StatusText.Text = "请先粘贴或识别原始文本，再让 AI 拆分。";
            return;
        }
        BusyRing.IsActive = true;
        try
        {
            StatusText.Text = $"AI（{_ai.Describe()}）拆分题干/答案/解析中…";
            var result = await _ai.AskAsync(
                "你是错题整理助手。请把用户给出的原始文本拆分为三部分：题干、答案、解析/错因。"
                + "严格使用标记输出：【题干】…【答案】…【解析】…；原文没有的部分留空。不要输出其它内容。",
                raw);
            var q = ExtractSection(result, "题干");
            var a = ExtractSection(result, "答案");
            var s = ExtractSection(result, "解析");
            if (string.IsNullOrEmpty(q) && string.IsNullOrEmpty(a) && string.IsNullOrEmpty(s))
            {
                StatusText.Text = "AI 未按格式返回，请重试或手动整理。";
                return;
            }
            if (!string.IsNullOrEmpty(q)) QuestionBox.Text = q;
            if (!string.IsNullOrEmpty(a)) AnswerBox.Text = a;
            if (!string.IsNullOrEmpty(s)) AnalysisBox.Text = s;
            StatusText.Text = "已拆分到题干/答案/解析，核对后点击「保存」。";
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "AI 拆分错题失败");
            StatusText.Text = $"AI 失败：{ex.Message}";
        }
        finally
        {
            BusyRing.IsActive = false;
        }
    }

    private async void OnExportClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FileSavePicker
            {
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
                SuggestedFileName = $"wrongbook-{DateTime.Now:yyyyMMdd-HHmm}"
            };
            picker.FileTypeChoices.Add("JSON 文件", new List<string> { ".json" });
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow!));

            var file = await picker.PickSaveFileAsync();
            if (file is null) return;

            var json = JsonSerializer.Serialize(_store.Items, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(file.Path, json);
            StatusText.Text = $"已导出 {_store.Items.Count} 条到 {file.Path}";
            _logger.Information("错题本导出：{File}", file.Path);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "错题本导出失败");
            StatusText.Text = $"导出失败：{ex.Message}";
        }
    }

    private async void OnImportClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
            picker.FileTypeFilter.Add(".json");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow!));

            var file = await picker.PickSingleFileAsync();
            if (file is null) return;

            var json = await File.ReadAllTextAsync(file.Path);
            var list = JsonSerializer.Deserialize<List<WrongQuestion>>(json);
            if (list is null || list.Count == 0)
            {
                StatusText.Text = "文件中没有可导入的条目。";
                return;
            }
            foreach (var item in list)
            {
                if (string.IsNullOrWhiteSpace(item.Id)) item.Id = Guid.NewGuid().ToString("N");
                _store.Upsert(item);
            }
            RefreshList();
            StatusText.Text = $"已导入 {list.Count} 条（同 ID 条目已覆盖）。";
            _logger.Information("错题本导入：{File}，{Count} 条", file.Path, list.Count);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "错题本导入失败");
            StatusText.Text = $"导入失败：{ex.Message}";
        }
    }
}
