using AI_Tool_Box.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DispatcherQueue = Microsoft.UI.Dispatching.DispatcherQueue;
using DispatcherQueueController = Microsoft.UI.Dispatching.DispatcherQueueController;
using DispatcherQueuePriority = Microsoft.UI.Dispatching.DispatcherQueuePriority;
using Serilog;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace AI_Tool_Box.ViewModels;

/// <summary>OCR 语言项包装（LiteParse2.0）：区分可用与缺失（目录固定 11 种常用语言：联合国 6 种工作语言 + 日/韩/德/葡/中文繁体）。</summary>
public sealed class OcrLanguageItem
{
    /// <summary>初始化 OCR 语言项。</summary>
    public OcrLanguageItem(string code, string displayName, bool isInstalled, string category)
    {
        Code = code ?? throw new ArgumentNullException(nameof(code));
        DisplayName = displayName ?? code;
        IsInstalled = isInstalled;
        Category = category ?? string.Empty;
    }

    /// <summary>Tesseract 兼容语言代码（LiteParse2.0 / lit.exe 使用，如 chi_sim / eng）。</summary>
    public string Code { get; }

    /// <summary>用户友好显示名。</summary>
    public string DisplayName { get; }

    /// <summary>该语言 traineddata 是否随 LiteParse2.0 引擎目录分发到本地。</summary>
    public bool IsInstalled { get; }

    /// <summary>分类："推荐 · 联合国工作语言"、"推荐 · 常用外语"、"本地已下载"。</summary>
    public string Category { get; }
}

/// <summary>带格式识别的代码语言配置：决定最小缩进单元；IndentWidth=0 表示普通文本模式（不按代码对齐）。</summary>
public sealed class OcrCodeStyle
{
    /// <summary>初始化代码语言配置。</summary>
    public OcrCodeStyle(string name, int indentWidth, bool useTabs)
    {
        Name = name;
        IndentWidth = indentWidth;
        UseTabs = useTabs;
    }

    /// <summary>显示名（含缩进单元说明）。</summary>
    public string Name { get; }

    /// <summary>最小缩进单元（空格数）；0 表示普通文本模式。</summary>
    public int IndentWidth { get; }

    /// <summary>是否用 Tab 表示一级缩进（Go）。</summary>
    public bool UseTabs { get; }
}

/// <summary>本地 OCR 视图模型（LiteParse2.0 / lit.exe 引擎，随应用分发、无需安装；语言数据完全离线）：
/// 图片 / PDF 上传 → 语言选择 → 识别 → 复制/注入网页。</summary>
public partial class OcrViewModel : ObservableObject
{
    private readonly ILogger _logger;
    private readonly ILiteParseOcrService _ocr;
    private readonly IWebShellService _webShell;
    private readonly DispatcherQueue _dispatcher;
    private StorageFile? _sourceFile = null;

    [ObservableProperty]
    private IReadOnlyList<OcrLanguageItem> _availableLanguages = Array.Empty<OcrLanguageItem>();

    [ObservableProperty]
    private OcrLanguageItem? _selectedLanguage;

    /// <summary>当前待识别文件路径（图片或 PDF）。</summary>
    [ObservableProperty]
    private string? _sourceFilePath;

    /// <summary>是否选择了 PDF（用于左侧标题与占位提示切换）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SourceTitle))]
    [NotifyPropertyChangedFor(nameof(PdfHintText))]
    private bool _isPdf;

    [ObservableProperty]
    private Microsoft.UI.Xaml.Media.ImageSource? _previewImage;

    [ObservableProperty]
    private string _resultText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNotBusy))]
    [NotifyCanExecuteChangedFor(nameof(RecognizeCommand))]
    [NotifyCanExecuteChangedFor(nameof(RecognizeFormattedCommand))]
    [NotifyCanExecuteChangedFor(nameof(CopyResultCommand))]
    [NotifyCanExecuteChangedFor(nameof(InjectToWebCommand))]
    private bool _isBusy;

    [ObservableProperty]
    private string _status = "请先选择图片或 PDF，再选择识别语言。";

    /// <summary>是否已加载文件（图片或 PDF）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NoFilePlaceholderVisibility))]
    [NotifyCanExecuteChangedFor(nameof(RecognizeCommand))]
    [NotifyCanExecuteChangedFor(nameof(RecognizeFormattedCommand))]
    private bool _hasFile;

    /// <summary>是否不在忙状态（操作按钮启用条件）。</summary>
    public bool IsNotBusy => !IsBusy;

    /// <summary>左侧预览区标题（图片 → 图片预览；PDF → PDF 文件）。</summary>
    public string SourceTitle => IsPdf ? "PDF 文件" : "图片预览";

    /// <summary>选择 PDF 后在预览区的提示文本（PDF 无法直接渲染预览）。</summary>
    public string PdfHintText => IsPdf
        ? "已选择 PDF 文件，点击「开始识别」提取全文文字。\n（多页结果会保留「--- Page N ---」分页标记）"
        : string.Empty;

    /// <summary>无文件占位符显示状态（无文件=Visible，有文件=Collapsed）。</summary>
    public Microsoft.UI.Xaml.Visibility NoFilePlaceholderVisibility
        => HasFile ? Microsoft.UI.Xaml.Visibility.Collapsed : Microsoft.UI.Xaml.Visibility.Visible;

    /// <summary>带格式识别的模式清单：普通文本 + 各代码语言（缩进单元不同）。</summary>
    public IReadOnlyList<OcrCodeStyle> CodeStyles { get; } = new[]
    {
        new OcrCodeStyle("普通文本 / 界面截图（按原样还原空格）", 0, false),
        new OcrCodeStyle("Python（缩进 4 空格）", 4, false),
        new OcrCodeStyle("JavaScript / TypeScript（缩进 2 空格）", 2, false),
        new OcrCodeStyle("Java / C / C++ / C#（缩进 4 空格）", 4, false),
        new OcrCodeStyle("Go（缩进 Tab）", 4, true),
        new OcrCodeStyle("Rust（缩进 4 空格）", 4, false),
        new OcrCodeStyle("HTML / CSS（缩进 2 空格）", 2, false),
        new OcrCodeStyle("SQL（缩进 4 空格）", 4, false),
        new OcrCodeStyle("JSON / YAML（缩进 2 空格）", 2, false),
    };

    [ObservableProperty]
    private OcrCodeStyle? _selectedCodeStyle;

    /// <summary>初始化 OcrViewModel。</summary>
    public OcrViewModel(ILogger logger, ILiteParseOcrService ocr, IWebShellService webShell)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _ocr = ocr ?? throw new ArgumentNullException(nameof(ocr));
        _webShell = webShell ?? throw new ArgumentNullException(nameof(webShell));
        _dispatcher = DispatcherQueue.GetForCurrentThread() ?? DispatcherQueueController.CreateOnDedicatedThread().DispatcherQueue;
        SelectedCodeStyle = CodeStyles[0];
        _ = LoadLanguagesAsync();
    }

    partial void OnResultTextChanged(string value)
    {
        CopyResultCommand.NotifyCanExecuteChanged();
        InjectToWebCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedLanguageChanged(OcrLanguageItem? value)
    {
        RecognizeCommand.NotifyCanExecuteChanged();
        RecognizeFormattedCommand.NotifyCanExecuteChanged();
    }

    private bool CanRecognize() => !IsBusy && HasFile && SelectedLanguage is not null && SelectedLanguage.IsInstalled;
    private bool CanRecognizeFormatted() => CanRecognize();
    private bool CanCopyResult() => !IsBusy && !string.IsNullOrEmpty(ResultText);
    private bool CanInjectToWeb() => !IsBusy && !string.IsNullOrEmpty(ResultText);

    /// <summary>扫描 LiteParse2.0 引擎目录加载语言列表（语言数据随引擎离线分发，无需下载）。</summary>
    [RelayCommand]
    private async Task LoadLanguagesAsync()
    {
        try
        {
            var list = (await _ocr.GetLanguagesAsync())
                .Select(l => new OcrLanguageItem(l.Code, l.DisplayName, l.IsInstalled, l.Category))
                .ToList();

            var installedCount = list.Count(l => l.IsInstalled);
            await _dispatcher.EnqueueOrRunAsync(() =>
            {
                AvailableLanguages = list;
                if (SelectedLanguage is null || !list.Any(l => l.Code == SelectedLanguage.Code))
                {
                    SelectedLanguage = list.FirstOrDefault(l => l.IsInstalled && l.Code.StartsWith("chi_sim", StringComparison.OrdinalIgnoreCase))
                                    ?? list.FirstOrDefault(l => l.IsInstalled && l.Code.StartsWith("eng", StringComparison.OrdinalIgnoreCase))
                                    ?? list.FirstOrDefault(l => l.IsInstalled);
                }
                Status = $"已加载 {list.Count} 种推荐语言（可用 {installedCount} 种）。LiteParse2.0 引擎目录：{_ocr.EngineDirectory}。";
            });
            _logger.Information("LiteParse2.0 语言列表加载完成：共 {Total} / 可用 {Installed}", list.Count, installedCount);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "加载 LiteParse2.0 语言列表失败");
            Status = $"加载语言列表失败：{ex.Message}";
        }
    }

    /// <summary>设置待识别文件（图片或 PDF；由 Page 层 FileOpenPicker 调用）。</summary>
    public async Task SetSourceFileAsync(StorageFile file)
    {
        if (file is null)
        {
            return;
        }
        _sourceFile = file;
        SourceFilePath = file.Path;
        IsPdf = Path.GetExtension(file.Path).Equals(".pdf", StringComparison.OrdinalIgnoreCase);
        HasFile = true;

        // PDF 无法直接作为图片预览，只显示文件名与提示
        if (IsPdf)
        {
            PreviewImage = null;
            Status = "PDF 已加载，请选择语言后点击「开始识别」提取文字。";
            _logger.Information("OCR PDF 已加载：{Path}", file.Path);
            return;
        }

        try
        {
            // 预览图（BitmapImage 需要 UI 线程创建，这里通过 Dispatcher 保证）
            Microsoft.UI.Xaml.Media.ImageSource? preview = null;
            await _dispatcher.EnqueueOrRunAsync(async () =>
            {
                var bmp = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage();
                using var ras = await file.OpenAsync(FileAccessMode.Read);
                await bmp.SetSourceAsync(ras);
                preview = bmp;
            });
            PreviewImage = preview;

            Status = "图片已加载，请选择语言后点击“开始识别”。";
            _logger.Information("OCR 图片已加载：{Path}", file.Path);
        }
        catch (Exception ex)
        {
            HasFile = false;
            PreviewImage = null;
            _logger.Error(ex, "加载图片失败：{Path}", file.Path);
            Status = $"加载图片失败：{ex.Message}";
        }
    }

    /// <summary>执行 OCR / PDF 文本提取（LiteParse2.0 / lit.exe 在后台进程运行）。</summary>
    [RelayCommand(CanExecute = nameof(CanRecognize))]
    private async Task RecognizeAsync()
    {
        if (_sourceFile is null || SelectedLanguage is null)
        {
            return;
        }

        IsBusy = true;
        ResultText = string.Empty;
        Status = IsPdf
            ? $"PDF 解析中…（{SelectedLanguage.DisplayName}）"
            : $"识别中…（{SelectedLanguage.DisplayName}）";
        try
        {
            var text = await _ocr.RecognizeAsync(_sourceFile.Path, SelectedLanguage.Code);
            ResultText = text;
            Status = string.IsNullOrEmpty(ResultText)
                ? (IsPdf ? "PDF 解析完成，但未提取到任何文字。" : "识别完成，但未提取到任何文字。")
                : IsPdf
                    ? $"PDF 文字提取完成，共 {ResultText.Length} 字符（多页已拼接，保留分页标记）。"
                    : $"识别完成，共 {ResultText.Length} 字符。可复制或直接注入网页焦点输入框。";
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "OCR 识别失败");
            Status = $"识别失败：{ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>识别 PDF 当前预览页：先把该页渲染为图片文件（由 View 层完成），再走既有图片 OCR 流程。</summary>
    /// <param name="pageImagePath">当前页渲染得到的临时图片路径。</param>
    /// <param name="pageNumber">页码（1 起，用于状态提示）。</param>
    /// <param name="pageCount">总页数。</param>
    public async Task RecognizePageImageAsync(string pageImagePath, int pageNumber, int pageCount)
    {
        if (SelectedLanguage is null)
        {
            Status = "请先选择识别语言。";
            return;
        }

        IsBusy = true;
        ResultText = string.Empty;
        Status = $"识别第 {pageNumber} / {pageCount} 页中…（{SelectedLanguage.DisplayName}）";
        try
        {
            var text = await _ocr.RecognizeAsync(pageImagePath, SelectedLanguage.Code);
            ResultText = text;
            Status = string.IsNullOrEmpty(text)
                ? $"第 {pageNumber} 页识别完成，但未提取到文字。"
                : $"第 {pageNumber} 页识别完成，共 {text.Length} 字符。";
            _logger.Information("PDF 第 {Page}/{Total} 页识别完成：{Len} 字符", pageNumber, pageCount, text.Length);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "识别 PDF 第 {Page} 页失败", pageNumber);
            Status = $"识别第 {pageNumber} 页失败：{ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>带格式识别：按字符坐标还原空格与缩进（代码模式按所选语言的最小缩进单元对齐）。</summary>
    [RelayCommand(CanExecute = nameof(CanRecognizeFormatted))]
    private async Task RecognizeFormattedAsync()
    {
        if (_sourceFile is null || SelectedLanguage is null)
        {
            return;
        }

        var style = SelectedCodeStyle;
        var codeMode = style is { IndentWidth: > 0 };
        IsBusy = true;
        ResultText = string.Empty;
        Status = codeMode
            ? $"代码识别中…（{SelectedLanguage.DisplayName} · {style!.Name}）"
            : $"带格式识别中…（{SelectedLanguage.DisplayName} · 普通文本模式）";
        try
        {
            var text = await _ocr.RecognizeFormattedAsync(
                _sourceFile.Path,
                SelectedLanguage.Code,
                style?.IndentWidth ?? 0,
                style?.UseTabs ?? false);
            ResultText = text;
            Status = string.IsNullOrEmpty(ResultText)
                ? "带格式识别完成，但未提取到任何文字。"
                : $"带格式识别完成，共 {ResultText.Length} 字符（{(codeMode ? style!.Name : "普通文本模式")}）。";
            _logger.Information("带格式识别完成：{Mode}，{Len} 字符", codeMode ? style!.Name : "普通文本", ResultText.Length);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "带格式识别失败");
            Status = $"带格式识别失败：{ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>复制识别结果到剪贴板。</summary>
    [RelayCommand(CanExecute = nameof(CanCopyResult))]
    private void CopyResult()
    {
        try
        {
            var dp = new DataPackage();
            dp.SetText(ResultText);
            Clipboard.SetContent(dp);
            Clipboard.Flush();
            Status = "识别结果已复制到剪贴板。";
            _logger.Information("OCR 结果已复制到剪贴板：{Len} 字符", ResultText.Length);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "复制到剪贴板失败");
            Status = $"复制失败：{ex.Message}";
        }
    }

    /// <summary>注入识别结果到当前网页焦点输入框。</summary>
    [RelayCommand(CanExecute = nameof(CanInjectToWeb))]
    private async Task InjectToWebAsync()
    {
        if (!_webShell.IsAvailable)
        {
            Status = "无可用网页：请先在侧栏“网址导航”打开网页，并在网页上点击激活输入框后再点此按钮。";
            return;
        }
        IsBusy = true;
        try
        {
            var ok = await _webShell.InjectToActiveElementAsync(ResultText);
            Status = ok
                ? "识别结果已注入到当前网页焦点输入框。"
                : "注入失败：当前网页焦点元素不支持写入，请先点击网页输入框激活后再试。";
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "注入网页失败");
            Status = $"注入失败：{ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }
}

/// <summary>DispatcherQueue 扩展：如果当前线程就是 Dispatcher 线程则直接执行，否则 Enqueue。</summary>
internal static class DispatcherQueueExtensions
{
    internal static Task EnqueueOrRunAsync(this DispatcherQueue dispatcher, Action action)
    {
        if (dispatcher.HasThreadAccess)
        {
            action();
            return Task.CompletedTask;
        }
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!dispatcher.TryEnqueue(DispatcherQueuePriority.Normal, () =>
        {
            try
            {
                action();
                tcs.TrySetResult(true);
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        }))
        {
            tcs.TrySetException(new InvalidOperationException("DispatcherQueue 已关闭，无法调度到 UI 线程。"));
        }
        return tcs.Task;
    }

    internal static Task EnqueueOrRunAsync(this DispatcherQueue dispatcher, Func<Task> func)
    {
        if (dispatcher.HasThreadAccess)
        {
            return func();
        }
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!dispatcher.TryEnqueue(DispatcherQueuePriority.Normal, async () =>
        {
            try
            {
                await func();
                tcs.TrySetResult(true);
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        }))
        {
            tcs.TrySetException(new InvalidOperationException("DispatcherQueue 已关闭，无法调度到 UI 线程。"));
        }
        return tcs.Task;
    }
}
