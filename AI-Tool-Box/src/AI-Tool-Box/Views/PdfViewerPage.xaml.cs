using AI_Tool_Box.Controls;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using Microsoft.Web.WebView2.Core;
using Serilog;
using System.Text.Json;
using Windows.Data.Pdf;
using Windows.Foundation;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;

namespace AI_Tool_Box.Views;

/// <summary>
/// PDF 查看器（C# 原生实现）：
///   • 查看/翻页/缩放：Windows.Data.Pdf（系统内置渲染，无 JS）
///   • 批注（文字/画笔）：AnnotCanvas 叠加（TextBlock / Polyline），坐标记录为 PDF 坐标
///   • 裁剪：拖拽矩形选区 → 应用后经 pdf-lib 写入 CropBox
///   • 合并 / 导出：隐藏 PdfToolWeb 承载 Assets/Web/pdf.html（pdf-lib）处理
/// </summary>
public sealed partial class PdfViewerPage : Page
{
    private static readonly Uri PdfToolHtmlUri = new(AppContext.BaseDirectory + "Assets/Web/pdf.html");

    private readonly ILogger _logger;

    private enum Tool { Pan, Text, Pen, Crop }
    private Tool _tool = Tool.Pan;

    private Windows.Data.Pdf.PdfDocument? _doc;
    private int _pageCount;
    private int _pageIndex = 1;
    private double _scale = 1.2;
    private StorageFile? _pdfFile;
    private double _renderW = 1, _renderH = 1;

    // 批注：PDF 坐标（左下原点）
    private sealed class PdfPoint { public double X; public double Y; }
    private sealed class AnnotItem
    {
        public int Page;
        public string Kind = "text";          // text | pen
        public double X, Y;
        public string? Text;
        public List<PdfPoint>? Points;
    }
    private readonly List<AnnotItem> _annots = new();

    // 交互状态
    private bool _drawing;                    // 画笔进行中
    private readonly List<PdfPoint> _penPoints = new();
    private Polyline? _curPolyline;
    private bool _cropping;                   // 裁剪进行中
    private Point _cropStart;

    public PdfViewerPage()
    {
        InitializeComponent();
        _logger = App.Services.GetService(typeof(ILogger)) as ILogger
                  ?? Serilog.Log.Logger;

        // 隐藏 PDF 工具页（pdf-lib 合并/导出）
        PdfToolWeb.Source = PdfToolHtmlUri;
        PdfToolWeb.Loaded += async (_, _) =>
        {
            try
            {
                await PdfToolWeb.EnsureCoreWebView2Async(await WebShell.GetSharedEnvironmentAsync());
                PdfToolWeb.CoreWebView2.WebMessageReceived += OnToolWebMessageReceived;
                _logger.Information("PDF 工具页 WebView2 初始化完成");
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "PDF 工具页 WebView2 初始化失败");
            }
        };

        // Ctrl + 鼠标滚轮缩放
        ViewScroller.PointerWheelChanged += OnViewPointerWheelChanged;
    }

    /// <summary>Ctrl + 滚轮缩放页面（普通滚轮仍为滚动）。</summary>
    private void OnViewPointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        if (_doc is null) return;
        var ctrl = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        if (!ctrl) return;
        var delta = e.GetCurrentPoint(ViewScroller).Properties.MouseWheelDelta;
        _scale = Math.Clamp(_scale * (delta > 0 ? 1.15 : 0.87), 0.3, 8);
        e.Handled = true;
        StatusText.Text = $"缩放：{_scale:P0}";
        _ = RenderPageAsync();
    }

    // ==================== 工具页消息（done → 保存） ====================

    private void OnToolWebMessageReceived(CoreWebView2 sender, CoreWebView2WebMessageReceivedEventArgs args)
    {
        try
        {
            using var doc = JsonDocument.Parse(args.WebMessageAsJson);
            var root = doc.RootElement;
            var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;
            if (type == "done")
            {
                var name = root.TryGetProperty("name", out var n) ? n.GetString() ?? "export.pdf" : "export.pdf";
                var b64 = root.TryGetProperty("dataBase64", out var b) ? b.GetString() ?? string.Empty : string.Empty;
                _ = SaveExportAsync(name, b64);
            }
            else if (type == "error")
            {
                var message = root.TryGetProperty("message", out var m) ? m.GetString() : null;
                StatusText.Text = $"错误：{message}";
                _logger.Warning("PDF 工具页错误：{Message}", message);
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "处理 PDF 工具页消息失败");
        }
    }

    private void PostToTool(object payload)
    {
        if (PdfToolWeb.CoreWebView2 is null) return;
        try
        {
            PdfToolWeb.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(payload));
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "发送 PDF 工具指令失败");
        }
    }

    // ==================== 打开 / 渲染 ====================

    private async void OnOpenClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
            picker.FileTypeFilter.Add(".pdf");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow));
            var file = await picker.PickSingleFileAsync();
            if (file is null) return;

            await OpenDocumentAsync(file);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "打开 PDF 失败");
            StatusText.Text = $"打开失败：{ex.Message}";
        }
    }

    private async Task OpenDocumentAsync(StorageFile file)
    {
        try
        {
            StatusText.Text = $"正在打开：{file.Name}…";
            var doc = await LoadPdfWithPasswordAsync(file);
            _doc = doc;
            _pdfFile = file;
            _pageCount = (int)doc.PageCount;
            _pageIndex = 1;
            _annots.Clear();
            _scale = 1.2;
            EmptyHint.Visibility = Visibility.Collapsed;
            await RenderPageAsync();
            StatusText.Text = $"已打开：{file.Name}（{_pageCount} 页）";
            _logger.Information("PDF 已打开：{Path}（{Pages} 页）", file.Path, _pageCount);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "加载 PDF 失败：{Path}", file.Path);
            StatusText.Text = $"打开失败：{ex.Message}";
        }
    }

    /// <summary>加载 PDF：先尝试无密码；加密文档（需密码）时弹窗输入密码后重载。</summary>
    private async Task<Windows.Data.Pdf.PdfDocument> LoadPdfWithPasswordAsync(StorageFile file)
    {
        try
        {
            return await Windows.Data.Pdf.PdfDocument.LoadFromFileAsync(file);
        }
        catch (Exception ex)
        {
            _logger.Information("PDF 加载失败（可能加密）：{Error}", ex.Message);
            var password = await PromptPasswordAsync();
            if (string.IsNullOrEmpty(password))
            {
                throw new InvalidOperationException("未输入密码，无法打开加密的 PDF。");
            }
            StatusText.Text = "正在用密码打开…";
            return await Windows.Data.Pdf.PdfDocument.LoadFromFileAsync(file, password);
        }
    }

    /// <summary>用 Windows.Data.Pdf 渲染当前页到位图并显示（超采样 2x 提升清晰度）。</summary>
    private async Task RenderPageAsync()
    {
        if (_doc is null) return;
        try
        {
            var page = _doc.GetPage((uint)(_pageIndex - 1));
            var size = page.Size; // PDF 点
            var dispW = Math.Max(1.0, size.Width * _scale);   // 显示逻辑尺寸
            var dispH = Math.Max(1.0, size.Height * _scale);
            var renderScale = _scale * 2.0;                    // 超采样：渲染 2x 分辨率，显示时缩回（更清晰）
            var destW = (uint)Math.Max(1, Math.Round(size.Width * renderScale));
            var destH = (uint)Math.Max(1, Math.Round(size.Height * renderScale));
            _renderW = dispW;
            _renderH = dispH;

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
            PageImage.Source = bmp;
            PageImage.Width = dispW;
            PageImage.Height = dispH;
            PageImage.Stretch = Stretch.Fill;   // 超采样位图缩放到显示尺寸
            AnnotCanvas.Width = dispW;
            AnnotCanvas.Height = dispH;
            AnnotCanvas.IsHitTestVisible = true;

            PageIndicator.Text = $"{_pageIndex} / {_pageCount}";
            CropRect.Visibility = Visibility.Collapsed;
            CropHint.Visibility = Visibility.Collapsed;
            _cropping = false;
            _drawing = false;
            DrawAnnotsForPage();

            // 关键修复：重新渲染后重置滚动位置，避免缩小/翻页后页面内容不完整（右侧被裁切/停留旧偏移）
            await System.Threading.Tasks.Task.Yield();
            ViewScroller.ChangeView(0, 0, null, disableAnimation: true);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "渲染 PDF 页失败：Page={Page}", _pageIndex);
            StatusText.Text = $"渲染失败：{ex.Message}";
        }
    }

    /// <summary>把当前页批注绘制到 AnnotCanvas。</summary>
    private void DrawAnnotsForPage()
    {
        AnnotCanvas.Children.Clear();
        foreach (var a in _annots)
        {
            if (a.Page != _pageIndex) continue;
            if (a.Kind == "text")
            {
                var tb = new TextBlock
                {
                    Text = a.Text ?? "",
                    FontSize = 14,
                    Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(235, 230, 30, 30)),
                    FontFamily = new FontFamily("Microsoft YaHei")
                };
                Canvas.SetLeft(tb, a.X * _scale);
                Canvas.SetTop(tb, _renderH - a.Y * _scale);
                AnnotCanvas.Children.Add(tb);
            }
            else if (a.Kind == "pen" && a.Points is { Count: > 1 })
            {
                var pl = new Polyline
                {
                    Stroke = new SolidColorBrush(Windows.UI.Color.FromArgb(235, 230, 30, 30)),
                    StrokeThickness = 2
                };
                foreach (var p in a.Points)
                {
                    pl.Points.Add(new Point(p.X * _scale, _renderH - p.Y * _scale));
                }
                AnnotCanvas.Children.Add(pl);
            }
        }
    }

    // ==================== 坐标转换 ====================

    private PdfPoint ToPdf(Point canvasPt)
    {
        return new PdfPoint { X = canvasPt.X / _scale, Y = (_renderH - canvasPt.Y) / _scale };
    }

    // ==================== 翻页 / 缩放 ====================

    private void OnPrevClick(object sender, RoutedEventArgs e)
    {
        if (_doc is null || _pageIndex <= 1) return;
        _pageIndex--;
        _ = RenderPageAsync();
    }

    private void OnNextClick(object sender, RoutedEventArgs e)
    {
        if (_doc is null || _pageIndex >= _pageCount) return;
        _pageIndex++;
        _ = RenderPageAsync();
    }

    private void OnZoomInClick(object sender, RoutedEventArgs e)
    {
        _scale = Math.Min(5, _scale * 1.2);
        if (_doc is not null) _ = RenderPageAsync();
    }

    private void OnZoomOutClick(object sender, RoutedEventArgs e)
    {
        _scale = Math.Max(0.3, _scale / 1.2);
        if (_doc is not null) _ = RenderPageAsync();
    }

    private async void OnFitWidthClick(object sender, RoutedEventArgs e)
    {
        if (_doc is null) return;
        try
        {
            var page = _doc.GetPage((uint)(_pageIndex - 1));
            var w = page.Size.Width;
            var avail = Math.Max(200, ViewScroller.ViewportWidth - 24);
            _scale = Math.Max(0.3, avail / w);
            await RenderPageAsync();
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "适应宽度失败");
        }
    }

    // ==================== 工具切换 ====================

    private void OnToolClick(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton tb)
        {
            ToolPanBtn.IsChecked = ReferenceEquals(tb, ToolPanBtn);
            ToolTextBtn.IsChecked = ReferenceEquals(tb, ToolTextBtn);
            ToolPenBtn.IsChecked = ReferenceEquals(tb, ToolPenBtn);
            ToolCropBtn.IsChecked = ReferenceEquals(tb, ToolCropBtn);
        }
        _tool = ReferenceEquals(sender, ToolTextBtn) ? Tool.Text
            : ReferenceEquals(sender, ToolPenBtn) ? Tool.Pen
            : ReferenceEquals(sender, ToolCropBtn) ? Tool.Crop
            : Tool.Pan;
        if (_tool != Tool.Crop)
        {
            CropRect.Visibility = Visibility.Collapsed;
            CropHint.Visibility = Visibility.Collapsed;
            _cropping = false;
        }
    }

    // ==================== 叠加层交互（画笔 / 文字 / 裁剪） ====================

    private async void OnCanvasPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_doc is null) return;
        var pt = e.GetCurrentPoint(AnnotCanvas).Position;

        if (_tool == Tool.Pen)
        {
            _drawing = true;
            _penPoints.Clear();
            _penPoints.Add(ToPdf(pt));
            _curPolyline = new Polyline
            {
                Stroke = new SolidColorBrush(Windows.UI.Color.FromArgb(235, 230, 30, 30)),
                StrokeThickness = 2
            };
            _curPolyline.Points.Add(pt);
            AnnotCanvas.Children.Add(_curPolyline);
            AnnotCanvas.CapturePointer(e.Pointer);
        }
        else if (_tool == Tool.Crop)
        {
            _cropping = true;
            _cropStart = pt;
            CropRect.Visibility = Visibility.Visible;
            CropHint.Visibility = Visibility.Visible;
            Canvas.SetLeft(CropRect, pt.X);
            Canvas.SetTop(CropRect, pt.Y);
            CropRect.Width = 0;
            CropRect.Height = 0;
            AnnotCanvas.CapturePointer(e.Pointer);
        }
        else if (_tool == Tool.Text)
        {
            var text = await PromptInputAsync("输入批注文字：");
            if (!string.IsNullOrWhiteSpace(text))
            {
                var pdf = ToPdf(pt);
                _annots.Add(new AnnotItem
                {
                    Page = _pageIndex,
                    Kind = "text",
                    X = pdf.X,
                    Y = pdf.Y,
                    Text = text.Trim()
                });
                DrawAnnotsForPage();
            }
        }
    }

    private void OnCanvasPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_doc is null) return;
        var pt = e.GetCurrentPoint(AnnotCanvas).Position;

        if (_tool == Tool.Pen && _drawing && _curPolyline is not null)
        {
            _penPoints.Add(ToPdf(pt));
            _curPolyline.Points.Add(pt);
        }
        else if (_tool == Tool.Crop && _cropping)
        {
            var x = Math.Min(pt.X, _cropStart.X);
            var y = Math.Min(pt.Y, _cropStart.Y);
            Canvas.SetLeft(CropRect, x);
            Canvas.SetTop(CropRect, y);
            CropRect.Width = Math.Abs(pt.X - _cropStart.X);
            CropRect.Height = Math.Abs(pt.Y - _cropStart.Y);
        }
    }

    private void OnCanvasPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_tool == Tool.Pen && _drawing)
        {
            _drawing = false;
            if (_penPoints.Count >= 2)
            {
                _annots.Add(new AnnotItem { Page = _pageIndex, Kind = "pen", Points = new List<PdfPoint>(_penPoints) });
            }
            _penPoints.Clear();
            _curPolyline = null;
        }
        else if (_tool == Tool.Crop && _cropping)
        {
            _cropping = false;
            // 过小的选区视为取消
            if (CropRect.Width < 8 || CropRect.Height < 8)
            {
                CropRect.Visibility = Visibility.Collapsed;
                CropHint.Visibility = Visibility.Collapsed;
            }
        }
    }

    private void OnCanvasPointerExited(object sender, PointerRoutedEventArgs e)
    {
        // 画笔拖出画布时结束当前笔画（避免无限绘制）
        if (_tool == Tool.Pen && _drawing)
        {
            _drawing = false;
            if (_penPoints.Count >= 2)
            {
                _annots.Add(new AnnotItem { Page = _pageIndex, Kind = "pen", Points = new List<PdfPoint>(_penPoints) });
            }
            _penPoints.Clear();
            _curPolyline = null;
        }
    }

    private void OnClearAnnotsClick(object sender, RoutedEventArgs e)
    {
        var clearAll = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Menu)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        if (clearAll) _annots.Clear();
        else _annots.RemoveAll(a => a.Page == _pageIndex);
        DrawAnnotsForPage();
        StatusText.Text = clearAll ? "已清除全部批注" : "已清除当前页批注";
    }

    // ==================== 导出 / 裁剪 / 合并 ====================

    private async void OnExportClick(object sender, RoutedEventArgs e) => await ExportAnnotatedAsync();

    private async void OnApplyCropClick(object sender, RoutedEventArgs e) => await ExportAnnotatedAsync(applyCrop: true);

    /// <summary>收集批注（+可选裁剪）→ pdf.html exportAnnotated → 保存。</summary>
    private async Task ExportAnnotatedAsync(bool applyCrop = false)
    {
        if (_doc is null || _pdfFile is null)
        {
            StatusText.Text = "请先打开 PDF 文档。";
            return;
        }
        try
        {
            var bytes = await File.ReadAllBytesAsync(_pdfFile.Path);
            var annots = new List<object>();
            foreach (var a in _annots)
            {
                if (a.Kind == "text")
                {
                    annots.Add(new { page = a.Page, kind = "text", x = a.X, y = a.Y, text = a.Text });
                }
                else if (a.Kind == "pen" && a.Points is { Count: > 1 })
                {
                    annots.Add(new
                    {
                        page = a.Page,
                        kind = "pen",
                        points = a.Points.Select(p => new { x = p.X, y = p.Y }).ToArray()
                    });
                }
            }

            object? crop = null;
            if (applyCrop && CropRect.Visibility == Visibility.Visible)
            {
                var x1 = Canvas.GetLeft(CropRect) / _scale;
                var y1 = (_renderH - (Canvas.GetTop(CropRect) + CropRect.Height)) / _scale;
                var x2 = (Canvas.GetLeft(CropRect) + CropRect.Width) / _scale;
                var y2 = (_renderH - Canvas.GetTop(CropRect)) / _scale;
                crop = new { page = _pageIndex, box = new[] { x1, y1, x2, y2 } };
            }

            StatusText.Text = "正在导出…";
            PostToTool(new { cmd = "exportAnnotated", file = new { name = _pdfFile.Name, dataBase64 = Convert.ToBase64String(bytes) }, annots, crop });
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "导出 PDF 失败");
            StatusText.Text = $"导出失败：{ex.Message}";
        }
    }

    private async void OnMergeClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
            picker.FileTypeFilter.Add(".pdf");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow));
            var files = await picker.PickMultipleFilesAsync();
            if (files is null || files.Count < 2)
            {
                StatusText.Text = "合并需要至少选择 2 个 PDF 文件。";
                return;
            }

            var payloads = new List<object>();
            foreach (var f in files)
            {
                var bytes = await File.ReadAllBytesAsync(f.Path);
                payloads.Add(new { name = f.Name, dataBase64 = Convert.ToBase64String(bytes) });
            }
            PostToTool(new { cmd = "merge", files = payloads });
            StatusText.Text = $"正在合并 {files.Count} 个 PDF…";
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "合并 PDF 失败");
            StatusText.Text = $"合并失败：{ex.Message}";
        }
    }

    private async Task SaveExportAsync(string name, string dataBase64)
    {
        try
        {
            var picker = new FileSavePicker
            {
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
                SuggestedFileName = name
            };
            picker.FileTypeChoices.Add("PDF 文档", new List<string> { ".pdf" });
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow));
            var file = await picker.PickSaveFileAsync();
            if (file is null)
            {
                StatusText.Text = "已取消保存。";
                return;
            }
            var bytes = Convert.FromBase64String(dataBase64);
            await File.WriteAllBytesAsync(file.Path, bytes);
            StatusText.Text = $"已保存：{file.Path}";
            _logger.Information("PDF 已保存：{Path}（{KB} KB）", file.Path, bytes.Length / 1024);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "保存导出结果失败");
            StatusText.Text = $"保存失败：{ex.Message}";
        }
    }

    // ==================== 简易输入框 ====================

    private static async Task<string?> PromptInputAsync(string title)
    {
        // 使用本页 XamlRoot 承载对话框
        if (App.MainWindow is not MainWindow mw || mw.Content is not FrameworkElement root)
        {
            return null;
        }
        var box = new TextBox { PlaceholderText = "输入文字…", Width = 300 };
        var dialog = new ContentDialog
        {
            Title = title,
            Content = box,
            PrimaryButtonText = "确定",
            CloseButtonText = "取消",
            XamlRoot = root.XamlRoot
        };
        var result = await dialog.ShowAsync();
        return result == ContentDialogResult.Primary ? box.Text : null;
    }

    /// <summary>弹出密码输入框（加密 PDF）。</summary>
    private static async Task<string?> PromptPasswordAsync()
    {
        if (App.MainWindow is not MainWindow mw || mw.Content is not FrameworkElement root)
        {
            return null;
        }
        var box = new PasswordBox { PlaceholderText = "输入 PDF 密码…", Width = 300 };
        var dialog = new ContentDialog
        {
            Title = "该 PDF 受密码保护",
            Content = box,
            PrimaryButtonText = "打开",
            CloseButtonText = "取消",
            XamlRoot = root.XamlRoot
        };
        var result = await dialog.ShowAsync();
        return result == ContentDialogResult.Primary ? box.Password : null;
    }
}
