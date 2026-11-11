using AI_Tool_Box.Controls;
using AI_Tool_Box.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Serilog;
using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using Windows.Data.Pdf;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace AI_Tool_Box.Views;

/// <summary>
/// 错题本导入预览对话框：直接预览来源文件（PDF 原文 / DOCX 渲染 HTML / 图片），
/// 支持按页局部选题并累积到 <see cref="Selected"/>。
///   • PDF：Windows.Data.Pdf 逐页渲染；可缩放（＋/－/适应宽度/100% 或 Ctrl+滚轮）并滚动；
///         框选=像截图工具一样任意方向拖拽矩形（记录 PDF 坐标点的 CropBox），
///         并把裁剪区域渲染成 PNG 缩略图写入 ThumbnailPath / AnswerImagePath。
///   • 图片：框选后用 BitmapDecoder 裁剪为 PNG（同时作为缩略图）。
///   • DOCX：word/document.xml → HTML 在 WebView2 中渲染，取选中范围的 HTML 片段。
/// </summary>
public sealed partial class WrongBookImportDialog : ContentDialog
{
    private readonly ILogger _logger;
    private readonly ITransformService _transform;
    private readonly string _path;
    private readonly string _fileName;
    private readonly string _ext;
    private readonly ObservableCollection<WrongBookImportRow> _added = new();

    private bool _isPdf, _isImage, _isDocx;

    // PDF
    private PdfDocument? _pdf;
    private int _pdfPages;
    private int _pageIndex = 1;

    // 缩放（显示比例）
    private double _scale = 1.0;
    private bool _initialFitDone;

    // 源尺寸（PDF 点 / 图片像素）与显示尺寸
    private double _pageW, _pageH;
    private double _dispW = 1, _dispH = 1;

    // 框选
    private bool _cropping;
    private Windows.Foundation.Point _cropStart;

    // 分隔条拖拽
    private bool _splitDragging;
    private double _splitStartX;
    private double _splitStartWidth;

    /// <summary>确认加入的错题集合（对话框「完成」后由调用方读取）。</summary>
    public IReadOnlyList<WrongQuestion> Selected => _added.Select(r => r.Question).ToList();

    /// <summary>初始化对话框。</summary>
    public WrongBookImportDialog(ILogger logger, ITransformService transform, string filePath)
    {
        InitializeComponent();
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _transform = transform ?? throw new ArgumentNullException(nameof(transform));
        _path = filePath;
        _fileName = Path.GetFileName(filePath);
        _ext = Path.GetExtension(filePath).ToLowerInvariant();
        AddedList.ItemsSource = _added;
        Opened += OnDialogOpened;
        PreviewScroller.PointerWheelChanged += OnPreviewWheelChanged;
        PreviewScroller.SizeChanged += OnPreviewSizeChanged;
    }

    private async void OnDialogOpened(ContentDialog sender, ContentDialogOpenedEventArgs args)
    {
        AdjustSizeToWindow();
        await LoadSourceAsync();
        // 若布局的 SizeChanged 早于文件加载触发，这里补一次按实际视口宽度的适配
        if (!_initialFitDone && (_isPdf || _isImage) && _pageW > 0 && PreviewScroller.ViewportWidth > 2)
        {
            _initialFitDone = true;
            _scale = FitScale();
            await ApplyZoomAsync();
        }
    }

    /// <summary>按当前窗口尺寸自适应（内容可滚动，避免按钮/视图超出窗口）。</summary>
    private void AdjustSizeToWindow()
    {
        double w = 1040, h = 660;
        try
        {
            var size = XamlRoot?.Size ?? default;
            if (size.Width > 1) w = size.Width;
            if (size.Height > 1) h = size.Height;
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "读取 XamlRoot 尺寸失败，使用默认大小");
        }

        // ContentDialog 自身还要占标题区（约 56）与底部按钮区（约 60）+ 内外边距，
        // 这里预留 220，避免「完成 / 取消」与操作面板底部按钮被窗口裁掉（用户反馈 1 的根因）。
        var availH = Math.Max(320, h - 220);
        var availW = Math.Max(560, w - 140);
        RootGrid.Width = Math.Min(availW, 1320);
        RootGrid.Height = Math.Min(availH, 780);
        RootGrid.MaxWidth = availW;
        RootGrid.MaxHeight = availH;
    }

    private async Task LoadSourceAsync()
    {
        try
        {
            _isPdf = _ext == ".pdf";
            _isDocx = _ext == ".docx";
            _isImage = !_isPdf && !_isDocx;

            TypeText.Text = _isPdf ? "PDF 原文预览" : _isDocx ? "DOCX（渲染为 HTML）" : "图片预览";
            var pageVis = _isPdf ? Visibility.Visible : Visibility.Collapsed;
            PrevBtn.Visibility = NextBtn.Visibility = PageIndicator.Visibility = pageVis;
            AddRegionBtn.Visibility = (_isPdf || _isImage) ? Visibility.Visible : Visibility.Collapsed;
            AddSelectionBtn.Visibility = _isDocx ? Visibility.Visible : Visibility.Collapsed;
            TargetBox.Visibility = (_isPdf || _isImage) ? Visibility.Visible : Visibility.Collapsed;
            ZoomBar.Visibility = (_isPdf || _isImage) ? Visibility.Visible : Visibility.Collapsed;
            HintText.Text = _isPdf
                ? "在页面上任意方向拖拽框选题目区域后点「加入错题」；不框选则整页加入。选中的区域会生成缩略图显示在「题目」栏。"
                : _isImage
                    ? "在图片上任意方向拖拽框选题目区域后点「加入错题」；不框选则整图加入。"
                    : "在右侧文档中选中题目文字后点「加入选中文字」（保留为 HTML）。";
            InfoText.Text = $"文件：{_fileName}｜类型：{TypeText.Text}｜每加入一条即为一道错题。";

            if (_isPdf)
            {
                await LoadPdfAsync();
            }
            else if (_isImage)
            {
                await LoadImageAsync();
            }
            else
            {
                await LoadDocxAsync();
            }
            EmptyHint.Visibility = Visibility.Collapsed;
            StatusText.Text = "预览已就绪，可直接选题。";
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "加载导入预览失败：{Path}", _path);
            EmptyHint.Text = $"加载失败：{ex.Message}";
            EmptyHint.Visibility = Visibility.Visible;
            StatusText.Text = $"加载失败：{ex.Message}";
        }
    }

    // ==================== PDF ====================

    private async Task LoadPdfAsync()
    {
        var file = await StorageFile.GetFileFromPathAsync(_path);
        _pdf = await PdfDocument.LoadFromFileAsync(file);
        _pdfPages = (int)_pdf.PageCount;
        _pageIndex = 1;
        using (var page = _pdf.GetPage(0))
        {
            _pageW = page.Size.Width;
            _pageH = page.Size.Height;
        }
        _scale = FitScale();
        await RenderPdfPageAsync();
    }

    /// <summary>渲染当前 PDF 页（超采样 2x 提升清晰度），并按缩放设置显示尺寸。</summary>
    private async Task RenderPdfPageAsync()
    {
        if (_pdf is null) return;
        using var page = _pdf.GetPage((uint)(_pageIndex - 1));
        var size = page.Size;
        _pageW = size.Width;
        _pageH = size.Height;

        var renderScale = _scale * 2.0;
        const double maxPx = 4096;
        if (_pageW * renderScale > maxPx) renderScale = maxPx / Math.Max(1.0, _pageW);
        if (_pageH * renderScale > maxPx) renderScale = Math.Min(renderScale, maxPx / Math.Max(1.0, _pageH));
        renderScale = Math.Max(renderScale, 0.05);

        var destW = (uint)Math.Max(1, Math.Round(_pageW * renderScale));
        var destH = (uint)Math.Max(1, Math.Round(_pageH * renderScale));

        var options = new PdfPageRenderOptions
        {
            DestinationWidth = destW,
            DestinationHeight = destH
        };
        using var stream = new InMemoryRandomAccessStream();
        await page.RenderToStreamAsync(stream, options);
        stream.Seek(0);

        var bmp = new BitmapImage();
        await bmp.SetSourceAsync(stream);
        PreviewImage.Source = bmp;

        ApplyDisplaySize();
        PageIndicator.Text = $"{_pageIndex} / {_pdfPages}";
        ZoomText.Text = $"{_scale * 100:0}%";
        PreviewScroller.ChangeView(0, 0, null, disableAnimation: true);
    }

    // ==================== 图片 ====================

    private async Task LoadImageAsync()
    {
        var file = await StorageFile.GetFileFromPathAsync(_path);
        using (var ras = await file.OpenAsync(FileAccessMode.Read))
        {
            var decoder = await BitmapDecoder.CreateAsync(ras);
            _pageW = decoder.PixelWidth;
            _pageH = decoder.PixelHeight;
        }

        var bmp = new BitmapImage();
        using (var stream = await file.OpenReadAsync())
        {
            await bmp.SetSourceAsync(stream);
        }
        PreviewImage.Source = bmp;

        _scale = FitScale();
        ApplyDisplaySize();
        ZoomText.Text = $"{_scale * 100:0}%";
    }

    // ==================== DOCX ====================

    private async Task LoadDocxAsync()
    {
        PreviewScroller.Visibility = Visibility.Collapsed;
        DocxWeb.Visibility = Visibility.Visible;
        await DocxWeb.EnsureCoreWebView2Async(await WebShell.GetSharedEnvironmentAsync());
        var html = await _transform.DocxToHtmlAsync(_path);
        DocxWeb.NavigateToString(WrapHtml(html));
    }

    private static string WrapHtml(string body) =>
        "<!DOCTYPE html><html lang=\"zh-CN\"><head><meta charset=\"utf-8\"><style>"
        + "html,body{margin:0;padding:16px;font-family:'Segoe UI','Microsoft YaHei',sans-serif;"
        + "font-size:15px;line-height:1.85;color:#111418;word-wrap:break-word}"
        + "img{max-width:100%}table{border-collapse:collapse}td,th{border:1px solid #99A3AD;padding:4px 6px}"
        + "p{margin:0 0 8px}::selection{background:#7fd4ff}"
        + "</style></head><body>" + body + "</body></html>";

    // ==================== 缩放 / 显示尺寸 ====================

    /// <summary>按预览区可视宽度计算「适应宽度」的缩放比例。</summary>
    private double FitScale()
    {
        var vw = PreviewScroller.ViewportWidth;
        if (vw <= 2) vw = 760; // 布局完成前的兜底宽度
        var s = (vw - 24) / Math.Max(1.0, _pageW);
        return Math.Clamp(s, 0.2, 5.0);
    }

    /// <summary>把当前缩放应用到显示尺寸（图片无需重渲染；PDF 由 RenderPdfPageAsync 调用）。</summary>
    private void ApplyDisplaySize()
    {
        _dispW = Math.Max(1.0, _pageW * _scale);
        _dispH = Math.Max(1.0, _pageH * _scale);
        PreviewImage.Width = _dispW;
        PreviewImage.Height = _dispH;
        PreviewImage.Stretch = Stretch.Fill;
        CropCanvas.Width = _dispW;
        CropCanvas.Height = _dispH;
        CropCanvas.IsHitTestVisible = true;
        ResetCrop();
    }

    private async Task ApplyZoomAsync()
    {
        _initialFitDone = true;
        if (_isPdf)
        {
            await RenderPdfPageAsync();
        }
        else if (_isImage)
        {
            ApplyDisplaySize();
        }
        if (_isPdf || _isImage) ZoomText.Text = $"{_scale * 100:0}%";
    }

    private async void OnZoomInClick(object sender, RoutedEventArgs e)
    {
        _scale = Math.Clamp(_scale * 1.2, 0.2, 5.0);
        await ApplyZoomAsync();
    }

    private async void OnZoomOutClick(object sender, RoutedEventArgs e)
    {
        _scale = Math.Clamp(_scale / 1.2, 0.2, 5.0);
        await ApplyZoomAsync();
    }

    private async void OnFitWidthClick(object sender, RoutedEventArgs e)
    {
        _scale = FitScale();
        await ApplyZoomAsync();
    }

    private async void OnZoomResetClick(object sender, RoutedEventArgs e)
    {
        _scale = 1.0;
        await ApplyZoomAsync();
    }

    /// <summary>Ctrl + 滚轮缩放（普通滚轮仍为滚动）。</summary>
    private async void OnPreviewWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        if (!(_isPdf || _isImage)) return;
        var ctrl = Microsoft.UI.Input.InputKeyboardSource
            .GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        if (!ctrl) return;
        var delta = e.GetCurrentPoint(PreviewScroller).Properties.MouseWheelDelta;
        _scale = Math.Clamp(_scale * (delta > 0 ? 1.1 : 0.9), 0.2, 5.0);
        e.Handled = true;
        await ApplyZoomAsync();
    }

    /// <summary>布局完成后按实际视口宽度首次自动适配（用户未手动缩放前）。</summary>
    private async void OnPreviewSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_initialFitDone) return;
        if (!(_isPdf || _isImage)) return;
        if (_pageW <= 0 || PreviewScroller.ViewportWidth <= 2) return;
        _initialFitDone = true;
        _scale = FitScale();
        await ApplyZoomAsync();
    }

    // ==================== 框选（截图式） ====================

    private void ResetCrop()
    {
        CropRect.Visibility = Visibility.Collapsed;
        Canvas.SetLeft(CropRect, 0);
        Canvas.SetTop(CropRect, 0);
        CropRect.Width = 0;
        CropRect.Height = 0;
        _cropping = false;
    }

    private void OnCanvasPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!(_isPdf || _isImage)) return;
        var pt = e.GetCurrentPoint(CropCanvas).Position;
        _cropping = true;
        _cropStart = pt;
        CropRect.Visibility = Visibility.Visible;
        Canvas.SetLeft(CropRect, pt.X);
        Canvas.SetTop(CropRect, pt.Y);
        CropRect.Width = 0;
        CropRect.Height = 0;
        CropCanvas.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void OnCanvasPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_cropping) return;
        var pt = e.GetCurrentPoint(CropCanvas).Position;
        // 任意方向：始终用较小/较大值定位，因此从任意起点向任意方向拖拽都正确
        var x = Math.Min(pt.X, _cropStart.X);
        var y = Math.Min(pt.Y, _cropStart.Y);
        Canvas.SetLeft(CropRect, x);
        Canvas.SetTop(CropRect, y);
        CropRect.Width = Math.Abs(pt.X - _cropStart.X);
        CropRect.Height = Math.Abs(pt.Y - _cropStart.Y);
        e.Handled = true;
    }

    private void OnCanvasPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_cropping) return;
        _cropping = false;
        CropCanvas.ReleasePointerCapture(e.Pointer);
        if (CropRect.Width < 6 || CropRect.Height < 6) ResetCrop();
    }

    private void OnCanvasPointerCaptureLost(object sender, PointerRoutedEventArgs e) => _cropping = false;

    private bool HasSelection => CropRect.Visibility == Visibility.Visible
                                 && CropRect.Width >= 6 && CropRect.Height >= 6;

    /// <summary>当前选区在「显示坐标」下的位置与宽高。</summary>
    private (double Left, double Top, double Width, double Height) Selection()
        => (Canvas.GetLeft(CropRect), Canvas.GetTop(CropRect), CropRect.Width, CropRect.Height);

    // ==================== 分隔条（调整预览区宽度） ====================

    private void OnSplitterPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        _splitDragging = true;
        _splitStartX = e.GetCurrentPoint(RootGrid).Position.X;
        _splitStartWidth = BodyGrid.ColumnDefinitions[2].Width.Value;
        Splitter.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void OnSplitterPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_splitDragging) return;
        var x = e.GetCurrentPoint(RootGrid).Position.X;
        var delta = _splitStartX - x; // 向左拖拽 → 预览区变宽（面板变窄）
        var w = Math.Clamp(_splitStartWidth + delta, 200, 560);
        BodyGrid.ColumnDefinitions[2].Width = new GridLength(w);
        e.Handled = true;
    }

    private void OnSplitterPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        _splitDragging = false;
        Splitter.ReleasePointerCapture(e.Pointer);
    }

    private void OnSplitterPointerCaptureLost(object sender, PointerRoutedEventArgs e) => _splitDragging = false;

    // ==================== 加入错题 ====================

    private async void OnAddRegionClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_isPdf) await AddPdfItemAsync();
            else if (_isImage) await AddImageItemAsync();
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "加入错题失败");
            StatusText.Text = $"加入失败：{ex.Message}";
        }
    }

    private async Task AddPdfItemAsync()
    {
        double[]? box = null;
        if (HasSelection)
        {
            var (left, top, w, h) = Selection();
            var x1 = left / _scale;
            var x2 = (left + w) / _scale;
            var y1 = (_dispH - (top + h)) / _scale;
            var y2 = (_dispH - top) / _scale;
            box = new[] { x1, y1, x2, y2 };
        }

        StatusText.Text = "正在生成题目缩略图…";
        var thumb = await RenderPdfThumbAsync(box);

        var q = new WrongQuestion
        {
            Kind = "pdf",
            SourcePath = _path,
            SourcePage = _pageIndex,
            CropBox = box,
            Question = $"PDF《{_fileName}》第 {_pageIndex} 页" + (box is null ? "（整页）" : "（裁剪区域）")
        };
        AssignImageTarget(q, thumb);
        AddItem(q);
        ResetCrop();
    }

    private async Task AddImageItemAsync()
    {
        string? imagePath = _path;
        var cropped = false;
        if (HasSelection)
        {
            var (left, top, w, h) = Selection();
            var sx = _pageW / Math.Max(1.0, _dispW);
            var sy = _pageH / Math.Max(1.0, _dispH);
            var x = (int)Math.Clamp(Math.Round(left * sx), 0, Math.Max(0, _pageW - 1));
            var y = (int)Math.Clamp(Math.Round(top * sy), 0, Math.Max(0, _pageH - 1));
            var cw = (int)Math.Clamp(Math.Round(w * sx), 1, Math.Max(1, (int)_pageW - x));
            var ch = (int)Math.Clamp(Math.Round(h * sy), 1, Math.Max(1, (int)_pageH - y));
            imagePath = await CropImageToPngAsync(_path, (uint)x, (uint)y, (uint)cw, (uint)ch);
            cropped = true;
        }
        var q = new WrongQuestion
        {
            Kind = "image",
            SourcePath = cropped ? imagePath! : _path,
            Question = $"图片《{_fileName}》" + (cropped ? "（裁剪区域）" : "（整图）")
        };
        AssignImageTarget(q, imagePath);
        AddItem(q);
        ResetCrop();
    }

    private async void OnAddSelectionClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (DocxWeb.CoreWebView2 is null)
            {
                StatusText.Text = "文档尚未就绪，请稍候再试。";
                return;
            }
            const string js =
                "(function(){var s=window.getSelection();if(!s||s.rangeCount===0)return '';"
                + "var r=s.getRangeAt(0).cloneContents();var d=document.createElement('div');"
                + "d.appendChild(r);return d.innerHTML;})()";
            var raw = await DocxWeb.CoreWebView2.ExecuteScriptAsync(js);
            var html = JsonSerializer.Deserialize<string>(raw) ?? string.Empty;
            if (string.IsNullOrWhiteSpace(html))
            {
                StatusText.Text = "请先在文档中选中要加入的题目文字。";
                return;
            }
            var plain = StripTags(html);
            var caption = plain.Length > 40 ? plain[..40] + "…" : plain;
            var q = new WrongQuestion
            {
                Kind = "html",
                SourcePath = _path,
                HtmlContent = html,
                Question = $"DOCX《{_fileName}》选中内容：{caption}"
            };
            AddItem(q);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "加入 DOCX 选中内容失败");
            StatusText.Text = $"加入失败：{ex.Message}";
        }
    }

    /// <summary>根据「加入为」选择，把缩略图路径写入题目或答案图片字段。</summary>
    private void AssignImageTarget(WrongQuestion q, string? path)
    {
        if (string.IsNullOrEmpty(path)) return;
        if (TargetBox.SelectedIndex == 1) q.AnswerImagePath = path;
        else q.ThumbnailPath = path;
    }

    private void AddItem(WrongQuestion q)
    {
        _added.Insert(0, new WrongBookImportRow(q));
        AddedCountText.Text = $"已加入 {_added.Count} 条";
        StatusText.Text = $"已加入：{q.Question}";
    }

    private void OnRemoveClick(object sender, RoutedEventArgs e)
    {
        if (AddedList.SelectedItem is WrongBookImportRow row)
        {
            _added.Remove(row);
            AddedCountText.Text = $"已加入 {_added.Count} 条";
            StatusText.Text = "已移除选中条目。";
        }
        else
        {
            StatusText.Text = "请先在列表中选中要移除的条目。";
        }
    }

    private void OnClearClick(object sender, RoutedEventArgs e)
    {
        _added.Clear();
        AddedCountText.Text = "已加入 0 条";
        StatusText.Text = "已清空本次加入的条目。";
    }

    // ==================== 翻页 ====================

    private async void OnPrevClick(object sender, RoutedEventArgs e)
    {
        if (_pdf is null || _pageIndex <= 1) return;
        _pageIndex--;
        await RenderPdfPageAsync();
    }

    private async void OnNextClick(object sender, RoutedEventArgs e)
    {
        if (_pdf is null || _pageIndex >= _pdfPages) return;
        _pageIndex++;
        await RenderPdfPageAsync();
    }

    // ==================== 缩略图生成 ====================

    /// <summary>把 PDF 指定区域（PDF 坐标点，左下原点；null=整页）渲染成 PNG 缩略图，返回文件路径。</summary>
    private async Task<string?> RenderPdfThumbAsync(double[]? box)
    {
        if (_pdf is null) return null;
        using var page = _pdf.GetPage((uint)(_pageIndex - 1));
        var size = page.Size;

        var rs = 2.5;
        const double maxPx = 4096;
        if (size.Width * rs > maxPx) rs = maxPx / Math.Max(1.0, size.Width);
        if (size.Height * rs > maxPx) rs = Math.Min(rs, maxPx / Math.Max(1.0, size.Height));
        rs = Math.Max(rs, 0.05);

        var destW = (uint)Math.Max(1, Math.Round(size.Width * rs));
        var destH = (uint)Math.Max(1, Math.Round(size.Height * rs));

        var options = new PdfPageRenderOptions { DestinationWidth = destW, DestinationHeight = destH };
        using var stream = new InMemoryRandomAccessStream();
        await page.RenderToStreamAsync(stream, options);

        if (box is { Length: 4 })
        {
            // PDF 坐标（左下原点）→ 像素区域（左上原点）
            var px = (uint)Math.Clamp(Math.Round(box[0] * rs), 0, Math.Max(0, (double)destW - 1));
            var py = (uint)Math.Clamp(Math.Round((size.Height - box[3]) * rs), 0, Math.Max(0, (double)destH - 1));
            var pw = (uint)Math.Clamp(Math.Round((box[2] - box[0]) * rs), 1, Math.Max(1.0, destW - px));
            var ph = (uint)Math.Clamp(Math.Round((box[3] - box[1]) * rs), 1, Math.Max(1.0, destH - py));
            return await CropStreamToPngAsync(stream, px, py, pw, ph);
        }
        return await SaveStreamToAssetAsync(stream, "wb");
    }

    /// <summary>按矩形区域裁剪图片文件为 PNG，保存到资源目录并返回路径。</summary>
    private static async Task<string> CropImageToPngAsync(string srcPath, uint x, uint y, uint w, uint h)
    {
        var file = await StorageFile.GetFileFromPathAsync(srcPath);
        using var ras = await file.OpenAsync(FileAccessMode.Read);
        return await CropStreamToPngAsync(ras, x, y, w, h);
    }

    /// <summary>从任意图片流按矩形区域裁剪并编码为 PNG 保存，返回文件路径。</summary>
    private static async Task<string> CropStreamToPngAsync(IRandomAccessStream source, uint x, uint y, uint w, uint h)
    {
        source.Seek(0);
        var decoder = await BitmapDecoder.CreateAsync(source);
        var cx = Math.Min(x, decoder.PixelWidth - 1);
        var cy = Math.Min(y, decoder.PixelHeight - 1);
        var cw = Math.Max(1, Math.Min(w, decoder.PixelWidth - cx));
        var ch = Math.Max(1, Math.Min(h, decoder.PixelHeight - cy));

        var transform = new BitmapTransform
        {
            Bounds = new BitmapBounds { X = cx, Y = cy, Width = cw, Height = ch }
        };
        var pixelData = await decoder.GetPixelDataAsync(
            BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, transform,
            ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage);
        var bytes = pixelData.DetachPixelData();

        using var outStream = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, outStream);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, cw, ch, decoder.DpiX, decoder.DpiY, bytes);
        await encoder.FlushAsync();
        return await SaveStreamToAssetAsync(outStream, "wb");
    }

    /// <summary>把 PNG 流写入错题本资源目录，返回文件路径。</summary>
    private static async Task<string> SaveStreamToAssetAsync(IRandomAccessStream stream, string prefix)
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AI-Tool-Box", "wrongbook-assets");
        Directory.CreateDirectory(dir);
        var outPath = Path.Combine(dir, $"{prefix}-{Guid.NewGuid():N}.png");
        stream.Seek(0);
        await using var fs = File.Create(outPath);
        await stream.AsStreamForRead().CopyToAsync(fs);
        return outPath;
    }

    private static string StripTags(string html)
    {
        var text = Regex.Replace(html ?? string.Empty, "<[^>]+>", string.Empty);
        text = System.Net.WebUtility.HtmlDecode(text);
        return Regex.Replace(text, @"\s+", " ").Trim();
    }
}

/// <summary>导入列表显示行：绑定 <see cref="WrongQuestion.Display"/> 文本与裁剪缩略图。</summary>
public sealed class WrongBookImportRow
{
    /// <summary>源错题条目。</summary>
    public WrongQuestion Question { get; }

    /// <summary>列表显示文本。</summary>
    public string Display { get; }

    /// <summary>缩略图（题目缩略图优先，其次答案图片；无则 null）。</summary>
    public ImageSource? Thumb { get; }

    /// <summary>构造显示行并加载缩略图。</summary>
    public WrongBookImportRow(WrongQuestion question)
    {
        Question = question;
        Display = question.Display;
        Thumb = LoadThumb(question);
    }

    private static ImageSource? LoadThumb(WrongQuestion q)
    {
        var path = !string.IsNullOrEmpty(q.ThumbnailPath) ? q.ThumbnailPath : q.AnswerImagePath;
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
        try
        {
            return new BitmapImage(new Uri(path));
        }
        catch
        {
            return null;
        }
    }
}
