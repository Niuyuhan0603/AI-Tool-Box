using AI_Tool_Box.Services;
using AI_Tool_Box.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using Serilog;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;

namespace AI_Tool_Box.Views;

/// <summary>本地 OCR 页：图片 / PDF 选择 → 语言选择 → 识别 → 复制 / 注入网页焦点输入框。</summary>
public sealed partial class OcrPage : Page
{
    private readonly ILogger _logger;
    private readonly OcrViewModel _viewModel;

    /// <summary>暴露 ViewModel 供 XAML x:Bind 绑定。</summary>
    public OcrViewModel ViewModel => _viewModel;

    /// <summary>初始化 OcrPage，注入 ViewModel。</summary>
    public OcrPage()
    {
        InitializeComponent();

        _logger = App.Services.GetService(typeof(ILogger)) as ILogger
                  ?? Serilog.Log.Logger;

        _viewModel = App.Services.GetRequiredService<OcrViewModel>();
        DataContext = _viewModel;

        _logger.Information("OcrPage 已加载");

        // Ctrl + 鼠标滚轮缩放 PDF 预览页（普通滚轮仍为滚动）
        PdfScroller.PointerWheelChanged += OnPdfScrollerPointerWheelChanged;
        // 首次显示 PDF 时窗口可能尚未完成布局，等尺寸就绪后再做一次“适应宽度”
        PdfScroller.SizeChanged += OnPdfScrollerSizeChanged;
    }

    // ==================== PDF 多页预览（Windows.Data.Pdf 渲染，镜像 PdfViewerPage） ====================

    private Windows.Data.Pdf.PdfDocument? _pdfDoc;
    private int _pdfPageCount;
    private int _pdfPageIndex = 1;
    private double _pdfScale = 1.0;
    private bool _pdfNeedFit;

    /// <summary>打开文件选择器选择图片。</summary>
    private async void OnPickImageClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FileOpenPicker
            {
                ViewMode = PickerViewMode.Thumbnail,
                SuggestedStartLocation = PickerLocationId.PicturesLibrary
            };
            picker.FileTypeFilter.Add(".png");
            picker.FileTypeFilter.Add(".jpg");
            picker.FileTypeFilter.Add(".jpeg");
            picker.FileTypeFilter.Add(".bmp");
            picker.FileTypeFilter.Add(".gif");
            picker.FileTypeFilter.Add(".tif");
            picker.FileTypeFilter.Add(".tiff");
            picker.FileTypeFilter.Add(".webp");

            // WinUI 3: FileOpenPicker 需要绑定到当前窗口句柄
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

            var file = await picker.PickSingleFileAsync();
            if (file is not null)
            {
                ResetPdfPreview();
                await _viewModel.SetSourceFileAsync(file);
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "选择图片失败");
            _viewModel.Status = $"选择图片失败：{ex.Message}";
        }
    }

    /// <summary>选择 PDF 并立即调用 LiteParse2.0 提取文字。</summary>
    private async void OnRecognizePdfClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FileOpenPicker
            {
                ViewMode = PickerViewMode.List,
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary
            };
            picker.FileTypeFilter.Add(".pdf");

            // WinUI 3: FileOpenPicker 需要绑定到当前窗口句柄
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

            var file = await picker.PickSingleFileAsync();
            if (file is null) return;

            await _viewModel.SetSourceFileAsync(file);
            // 先渲染 PDF 预览（多页可翻页查看），再执行整份 PDF 的文字提取
            await LoadPdfPreviewAsync(file);
            if (_viewModel.RecognizeCommand.CanExecute(null))
            {
                _viewModel.RecognizeCommand.Execute(null);
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "选择 PDF 失败");
            _viewModel.Status = $"选择 PDF 失败：{ex.Message}";
        }
    }

    /// <summary>加载 PDF 并渲染首页预览（按窗口宽度自适应）。</summary>
    private async Task LoadPdfPreviewAsync(StorageFile file)
    {
        try
        {
            _pdfDoc = await Windows.Data.Pdf.PdfDocument.LoadFromFileAsync(file);
            _pdfPageCount = (int)_pdfDoc.PageCount;
            _pdfPageIndex = 1;
            _pdfScale = 1.0;
            _pdfNeedFit = true;
            await RenderPdfPageAsync(fitWidth: true);
            _viewModel.Status = $"PDF 已加载（共 {_pdfPageCount} 页）：可翻页 / 缩放预览，点击「开始识别」提取全文，或「识别当前页」逐页识别。";
            _logger.Information("OCR PDF 预览已加载：{Path}（{Pages} 页）", file.Path, _pdfPageCount);
        }
        catch (Exception ex)
        {
            _pdfDoc = null;
            _logger.Error(ex, "加载 PDF 预览失败：{Path}", file.Path);
            _viewModel.Status = $"PDF 预览加载失败：{ex.Message}";
        }
    }

    /// <summary>清理 PDF 预览状态（切换到图片时调用）。</summary>
    private void ResetPdfPreview()
    {
        _pdfDoc = null;
        _pdfPageCount = 0;
        _pdfPageIndex = 1;
        _pdfNeedFit = false;
        PdfPageImage.Source = null;
        PdfPageIndicator.Text = "– / –";
    }

    /// <summary>渲染当前页到位图（2× 超采样提升清晰度）并显示；fitWidth=true 时按窗口宽度自适应。</summary>
    private async Task RenderPdfPageAsync(bool fitWidth = false)
    {
        if (_pdfDoc is null) return;
        try
        {
            var page = _pdfDoc.GetPage((uint)(_pdfPageIndex - 1));
            var size = page.Size;

            if (fitWidth)
            {
                var viewport = PdfScroller.ViewportWidth > 0 ? PdfScroller.ViewportWidth : PdfScroller.ActualWidth;
                if (viewport > 0)
                {
                    _pdfScale = Math.Max(0.2, (viewport - 24) / Math.Max(1.0, size.Width));
                    _pdfNeedFit = false;
                }
            }
            _pdfScale = Math.Clamp(_pdfScale, 0.2, 6);

            var dispW = Math.Max(1.0, size.Width * _pdfScale);
            var dispH = Math.Max(1.0, size.Height * _pdfScale);
            var renderScale = _pdfScale * 2.0;

            var options = new Windows.Data.Pdf.PdfPageRenderOptions
            {
                DestinationWidth = (uint)Math.Max(1, Math.Round(size.Width * renderScale)),
                DestinationHeight = (uint)Math.Max(1, Math.Round(size.Height * renderScale)),
            };
            using var stream = new InMemoryRandomAccessStream();
            await page.RenderToStreamAsync(stream, options);
            stream.Seek(0);

            var bmp = new BitmapImage();
            await bmp.SetSourceAsync(stream);
            PdfPageImage.Source = bmp;
            PdfPageImage.Width = dispW;
            PdfPageImage.Height = dispH;
            PdfPageImage.Stretch = Microsoft.UI.Xaml.Media.Stretch.Fill;

            PdfPageIndicator.Text = $"{_pdfPageIndex} / {_pdfPageCount}";

            if (fitWidth)
            {
                await Task.Yield();
                PdfScroller.ChangeView(0, 0, null, disableAnimation: true);
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "渲染 PDF 预览页失败：Page={Page}", _pdfPageIndex);
            _viewModel.Status = $"PDF 预览渲染失败：{ex.Message}";
        }
    }

    private void OnPdfPrevClick(object sender, RoutedEventArgs e)
    {
        if (_pdfDoc is null || _pdfPageIndex <= 1) return;
        _pdfPageIndex--;
        _ = RenderPdfPageAsync();
    }

    private void OnPdfNextClick(object sender, RoutedEventArgs e)
    {
        if (_pdfDoc is null || _pdfPageIndex >= _pdfPageCount) return;
        _pdfPageIndex++;
        _ = RenderPdfPageAsync();
    }

    private void OnPdfZoomInClick(object sender, RoutedEventArgs e)
    {
        if (_pdfDoc is null) return;
        _pdfScale = Math.Min(6, _pdfScale * 1.2);
        _ = RenderPdfPageAsync();
    }

    private void OnPdfZoomOutClick(object sender, RoutedEventArgs e)
    {
        if (_pdfDoc is null) return;
        _pdfScale = Math.Max(0.2, _pdfScale / 1.2);
        _ = RenderPdfPageAsync();
    }

    private async void OnPdfFitWidthClick(object sender, RoutedEventArgs e)
    {
        if (_pdfDoc is null) return;
        await RenderPdfPageAsync(fitWidth: true);
    }

    /// <summary>PDF 预览区尺寸就绪后，补做一次“适应宽度”（首次显示时窗口布局可能尚未完成）。</summary>
    private void OnPdfScrollerSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!_pdfNeedFit || _pdfDoc is null || PdfScroller.ViewportWidth <= 0) return;
        _ = RenderPdfPageAsync(fitWidth: true);
    }

    /// <summary>Ctrl + 鼠标滚轮缩放 PDF 预览页（普通滚轮仍为滚动）。</summary>
    private void OnPdfScrollerPointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        if (_pdfDoc is null) return;
        var ctrl = Microsoft.UI.Input.InputKeyboardSource
            .GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        if (!ctrl) return;
        var delta = e.GetCurrentPoint(PdfScroller).Properties.MouseWheelDelta;
        _pdfScale = Math.Clamp(_pdfScale * (delta > 0 ? 1.15 : 0.87), 0.2, 6);
        e.Handled = true;
        _ = RenderPdfPageAsync();
    }

    /// <summary>把当前预览页渲染为临时图片文件，再交给既有 OCR 管线逐页识别。</summary>
    private async void OnRecognizeCurrentPageClick(object sender, RoutedEventArgs e)
    {
        if (_pdfDoc is null)
        {
            _viewModel.Status = "请先选择 PDF 文件。";
            return;
        }

        StorageFile? temp = null;
        try
        {
            var page = _pdfDoc.GetPage((uint)(_pdfPageIndex - 1));
            var size = page.Size;
            var options = new Windows.Data.Pdf.PdfPageRenderOptions
            {
                DestinationWidth = (uint)Math.Max(1, Math.Round(size.Width * 2)),
                DestinationHeight = (uint)Math.Max(1, Math.Round(size.Height * 2)),
            };

            var tempDir = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar);
            var folder = await StorageFolder.GetFolderFromPathAsync(tempDir);
            temp = await folder.CreateFileAsync($"atb_ocrpdf_{Guid.NewGuid():N}.png", CreationCollisionOption.ReplaceExisting);
            using (var stream = await temp.OpenAsync(FileAccessMode.ReadWrite))
            {
                await page.RenderToStreamAsync(stream, options);
            }

            await _viewModel.RecognizePageImageAsync(temp.Path, _pdfPageIndex, _pdfPageCount);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "识别 PDF 当前页失败：Page={Page}", _pdfPageIndex);
            _viewModel.Status = $"识别当前页失败：{ex.Message}";
        }
        finally
        {
            if (temp is not null)
            {
                try { await temp.DeleteAsync(); }
                catch
                {
                    // 临时文件删除失败：忽略（下次启动可清理）
                }
            }
        }
    }
}
