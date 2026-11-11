using System.Collections.ObjectModel;
using System.Text.Json;
using AI_Tool_Box.Controls;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using Serilog;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace AI_Tool_Box.Views;

/// <summary>EPUB 阅读器页：WebView2 承载 epub.js（Assets/Web/epub.html），支持本地打开 / 拖入 .epub、翻页、目录跳转、字号与夜间模式。</summary>
public sealed partial class EpubReaderPage : Page
{
    /// <summary>阅读器页面（构建后随 Assets\Web 内容复制到输出目录）。</summary>
    private static readonly Uri EpubHtmlUri = new(AppContext.BaseDirectory + "Assets/Web/epub.html");

    private readonly ILogger _logger;
    private readonly ObservableCollection<EpubTocItem> _tocItems = new();

    /// <summary>每块原始字节数（255KB：3 的整数倍，保证各块 base64 可安全拼接）；base64 后约 340KB。</summary>
    private const int ChunkBytes = 255 * 1024;

    private bool _jsReady;
    private byte[]? _pendingBytes;   // 页面未就绪时缓存的 .epub 原始字节（存 byte[] 而非超长 base64）
    private string? _pendingName;
    private int _openVersion;        // 每次投递递增，用于丢弃被新书取代的旧分块任务
    private bool _dark;

    /// <summary>初始化 EPUB 阅读器页并加载 epub.html。</summary>
    public EpubReaderPage()
    {
        InitializeComponent();

        _logger = App.Services.GetService(typeof(ILogger)) as ILogger
                  ?? Serilog.Log.Logger;
        TocList.ItemsSource = _tocItems;

        Web.Source = EpubHtmlUri;
        // 关键修复：必须复用 WebShell 的共享 Environment。
        // 无参 EnsureCoreWebView2Async 会另建环境与 WebShell（HiddenWebHost）的用户数据目录冲突 → NRE
        //（用户截图"WebView2 初始化失败：Object reference not set"的根因）。
        // WinUI 3 版 WebView2 控件无 CoreWebView2InitializationCompleted 事件（属 WPF/WinForms 版），故用 Loaded。
        Web.Loaded += async (_, _) =>
        {
            try
            {
                await Web.EnsureCoreWebView2Async(await WebShell.GetSharedEnvironmentAsync());
                Web.CoreWebView2.WebMessageReceived += OnWebMessageReceived;
                _logger.Information("EPUB 阅读器 WebView2 初始化完成（共享环境）");
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "EPUB 阅读器 WebView2 初始化失败");
                StatusText.Text = $"WebView2 初始化失败：{ex.Message}";
            }
        };

        _logger.Information("EpubReaderPage 已加载");
    }

    /// <summary>处理 epub.html 发来的消息（ready / error / toc / page）。</summary>
    private void OnWebMessageReceived(CoreWebView2 sender, CoreWebView2WebMessageReceivedEventArgs args)
    {
        try
        {
            using var doc = JsonDocument.Parse(args.WebMessageAsJson);
            var root = doc.RootElement;
            switch (root.TryGetProperty("type", out var typeEl) ? typeEl.GetString() : null)
            {
                case "ready":
                    _jsReady = true;
                    // 页面就绪前用户已选好书籍则在此补发（分块投递，避免超大 postMessage 失败 / 截断）
                    if (_pendingBytes is not null)
                    {
                        var pending = _pendingBytes;
                        var pendingName = _pendingName ?? "缓存书籍";
                        _pendingBytes = null;
                        _pendingName = null;
                        _ = SendEpubAsync(pending, pendingName);
                    }
                    break;

                case "error":
                    var message = root.TryGetProperty("message", out var msgEl) ? msgEl.GetString() : null;
                    StatusText.Text = $"阅读器错误：{message}";
                    _logger.Warning("EPUB 阅读器 JS 错误：{Message}", message);
                    break;

                case "toc":
                    ApplyToc(root);
                    break;

                case "page":
                    if (root.TryGetProperty("current", out var current) &&
                        root.TryGetProperty("total", out var total))
                    {
                        PageIndicator.Text = $"{current.GetInt32()} / {total.GetInt32()}";
                    }
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "处理 EPUB 阅读器消息失败");
        }
    }

    /// <summary>用 JS 发来的扁平化目录刷新右侧列表。</summary>
    private void ApplyToc(JsonElement root)
    {
        _tocItems.Clear();
        if (root.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in items.EnumerateArray())
            {
                _tocItems.Add(new EpubTocItem
                {
                    Label = item.TryGetProperty("label", out var label) ? label.GetString() ?? "未命名" : "未命名",
                    Href = item.TryGetProperty("href", out var href) ? href.GetString() ?? string.Empty : string.Empty
                });
            }
        }
        TocButton.Content = _tocItems.Count > 0 ? $"目录（{_tocItems.Count}）" : "目录";
    }

    /// <summary>向 epub.html 发送 JSON 指令。</summary>
    private void PostToJs(object payload)
    {
        if (Web.CoreWebView2 is null)
        {
            return;
        }
        Web.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(payload));
    }

    /// <summary>「打开 EPUB…」：FileOpenPicker 选 .epub → 分块投递（openBegin/openChunk…/openEnd）。</summary>
    private async void OnOpenClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FileOpenPicker
            {
                ViewMode = PickerViewMode.List,
                SuggestedStartLocation = PickerLocationId.ComputerFolder
            };
            picker.FileTypeFilter.Add(".epub");

            // WinUI 3: FileOpenPicker 需要绑定到当前窗口句柄
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

            var file = await picker.PickSingleFileAsync();
            if (file is not null)
            {
                await OpenEpubAsync(file);
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "打开 EPUB 失败");
            StatusText.Text = $"打开 EPUB 失败：{ex.Message}";
        }
    }

    /// <summary>读取 .epub 字节并投递给阅读器（页面未就绪时缓存字节，待 ready 后补发）。</summary>
    private async Task OpenEpubAsync(StorageFile file)
    {
        var bytes = await File.ReadAllBytesAsync(file.Path);
        if (bytes.Length == 0)
        {
            StatusText.Text = $"文件为空：{file.Name}";
            return;
        }

        _tocItems.Clear();
        PageIndicator.Text = "加载中…";
        StatusText.Text = $"已打开：{file.Name}（{bytes.LongLength / 1024.0:F0} KB），正在解析…";

        if (!_jsReady || Web.CoreWebView2 is null)
        {
            _pendingBytes = bytes;
            _pendingName = file.Name;
            return;
        }
        await SendEpubAsync(bytes, file.Name);
    }

    /// <summary>
    /// 分块投递 .epub 字节：openBegin → openChunk×N → openEnd。
    /// WebView2 的 PostWebMessageAsJson 对超大消息会失败 / 截断（用户「解析失败」的主要根因），
    /// 故按 255KB（3 字节对齐，保证 base64 可拼接）切块发送，每块之间让出 UI 线程。
    /// </summary>
    private async Task SendEpubAsync(byte[] bytes, string fileName)
    {
        try
        {
            // WebView2 的消息 API 有线程亲和性，统一切到 UI 线程（DispatcherQueue）发送
            await RunOnUiAsync(() => PostToJs(new { cmd = "openBegin" }));

            var version = ++_openVersion;
            var total = (bytes.Length + ChunkBytes - 1) / ChunkBytes;
            for (var i = 0; i < total; i++)
            {
                if (version != _openVersion || Web.CoreWebView2 is null) { return; }  // 已被更新的一次打开取代

                var offset = i * ChunkBytes;
                var length = Math.Min(ChunkBytes, bytes.Length - offset);
                var data = Convert.ToBase64String(bytes, offset, length);
                var index = i;

                await RunOnUiAsync(() => PostToJs(new { cmd = "openChunk", index, data }));
                await Task.Delay(1);   // 让出 UI 线程，避免大文件一次性阻塞界面
            }

            if (version != _openVersion || Web.CoreWebView2 is null) { return; }
            await RunOnUiAsync(() => PostToJs(new { cmd = "openEnd" }));
            _logger.Information("已分块发送 EPUB 至阅读器：{Name}（{Size} 字节，{Chunks} 块）", fileName, bytes.LongLength, total);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "分块发送 EPUB 失败");
            StatusText.Text = $"发送 EPUB 失败：{ex.Message}";
        }
    }

    /// <summary>确保后续 WebView2 调用在 UI 线程执行（已在 UI 线程时直接执行）。</summary>
    private Task RunOnUiAsync(Action action)
    {
        var dispatcher = DispatcherQueue;
        if (dispatcher is null || dispatcher.HasThreadAccess)
        {
            action();
            return Task.CompletedTask;
        }

        var tcs = new TaskCompletionSource<bool>();
        if (!dispatcher.TryEnqueue(() =>
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
            tcs.SetException(new InvalidOperationException("DispatcherQueue 排队失败，无法投递 EPUB 数据"));
        }
        return tcs.Task;
    }

    private void OnPrevClick(object sender, RoutedEventArgs e) => PostToJs(new { cmd = "prev" });

    private void OnNextClick(object sender, RoutedEventArgs e) => PostToJs(new { cmd = "next" });

    /// <summary>目录按钮：折叠 / 展开右侧目录面板。</summary>
    private void OnTocClick(object sender, RoutedEventArgs e)
    {
        TocPanel.Visibility = TocPanel.Visibility == Visibility.Visible
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    /// <summary>目录条目点击 → 跳转章节。</summary>
    private void OnTocItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is EpubTocItem item && !string.IsNullOrEmpty(item.Href))
        {
            PostToJs(new { cmd = "goto", href = item.Href });
        }
    }

    private void OnFontMinusClick(object sender, RoutedEventArgs e) => PostToJs(new { cmd = "font", delta = -1 });

    private void OnFontPlusClick(object sender, RoutedEventArgs e) => PostToJs(new { cmd = "font", delta = 1 });

    /// <summary>夜间 / 日间模式切换。</summary>
    private void OnThemeClick(object sender, RoutedEventArgs e)
    {
        _dark = !_dark;
        ThemeButton.Content = _dark ? "日间模式" : "夜间模式";
        PostToJs(new { cmd = "theme", dark = _dark });
    }

    /// <summary>整页允许拖放：接受复制操作。</summary>
    private void OnRootDragOver(object sender, DragEventArgs e)
    {
        e.AcceptedOperation = DataPackageOperation.Copy;
        e.Handled = true;
    }

    /// <summary>拖入 .epub 文件直接打开。</summary>
    private async void OnRootDrop(object sender, DragEventArgs e)
    {
        var deferral = e.GetDeferral();
        try
        {
            if (!e.DataView.Contains(StandardDataFormats.StorageItems))
            {
                return;
            }
            var items = await e.DataView.GetStorageItemsAsync();
            var file = items.OfType<StorageFile>().FirstOrDefault(f =>
                string.Equals(f.FileType, ".epub", StringComparison.OrdinalIgnoreCase));
            if (file is null)
            {
                StatusText.Text = "仅支持拖入 .epub 文件。";
                return;
            }
            await OpenEpubAsync(file);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "拖入 EPUB 处理失败");
            StatusText.Text = $"拖入 EPUB 失败：{ex.Message}";
        }
        finally
        {
            deferral.Complete();
        }
    }
}

/// <summary>EPUB 目录条目（epub.html 发送的 TOC 扁平化结果）。</summary>
public sealed class EpubTocItem
{
    /// <summary>显示标题（带层级缩进）。</summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>EPUB 内部跳转地址（spine href）。</summary>
    public string Href { get; set; } = string.Empty;
}
