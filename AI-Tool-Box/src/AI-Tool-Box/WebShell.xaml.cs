using AI_Tool_Box.Models;
using AI_Tool_Box.Services;
using AI_Tool_Box.Views;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.Web.WebView2.Core;
using Serilog;
using System.Collections.ObjectModel;
using System.Text.Json;
using Windows.ApplicationModel.DataTransfer;

namespace AI_Tool_Box.Controls;

/// <summary>网页外壳控件：多标签 WebView2 + 地址栏 + 悬浮球（书签/历史/下载）。
/// 支持类似 Edge 的标签拖出/拖回：拖到标签栏外 → tear-out 为独立窗口（BrowserTearoutWindow，传 URL 重建）；
/// 从独立窗口拖回标签栏 → 本壳新开该 URL 标签并关闭源窗口。主/卫星壳共享同一 CoreWebView2Environment（同 Profile 登录态）。</summary>
public sealed partial class WebShell : UserControl
{
    private static readonly string DataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AI-Tool-Box", "webdata");

    /// <summary>跨窗口标签拖拽的自定义 DataPackage 格式名。</summary>
    internal const string TearOutFormat = "aitb-tab-tearout";

    /// <summary>主壳（MainWindow 内常驻的 WebShell）静态引用；卫星壳用于"合并回主窗口"。</summary>
    private static WebShell? _primaryShell;

    /// <summary>全部存活卫星窗口注册表：拖回时按源窗口 Id 定位并关闭。</summary>
    private static readonly Dictionary<Guid, BrowserTearoutWindow> SatelliteWindows = new();

    /// <summary>最近一次"标签被其他窗口标签栏成功接收"的记录：抑制源窗口随后的 TabDroppedOutside，避免重复 tear-out。</summary>
    private static (Guid SourceId, string Url, DateTime At)? _remoteDropHandled;

    /// <summary>进程内共享的 CoreWebView2Environment：同一 UserDataFolder（默认目录，登录态共享），同进程复用同一浏览器进程。
    /// 注意：无参 EnsureCoreWebView2Async 在多控件场景会各自创建环境导致用户数据目录占用冲突，故全壳统一走此共享环境。</summary>
    private static Task<CoreWebView2Environment>? _sharedEnvironmentTask;

    /// <summary>书签/历史/下载 JSON 写入锁：主壳与卫星壳并发写同一文件时避免共享冲突。</summary>
    private static readonly SemaphoreSlim JsonWriteGate = new(1, 1);

    private readonly ILogger _logger;
    private readonly ObservableCollection<WebView2> _tabs = new();
    private readonly Guid _instanceId = Guid.NewGuid();
    private readonly DispatcherQueue _dispatcher;
    private WebView2? _active;
    private bool _isShuttingDown;

    /// <summary>是否为独立窗口中的卫星壳实例。</summary>
    public bool IsSatellite { get; }

    /// <summary>右侧 AI 辅助侧栏开合状态（程序化赋值会同步触发 Checked/Unchecked 联动可见性）。</summary>
    public bool IsAiAssistOpen
    {
        get => AiAssistToggle.IsChecked == true;
        set => AiAssistToggle.IsChecked = value;
    }

    /// <summary>AI 辅助开关切换：联动侧栏可见性。</summary>
    private void OnAiAssistToggleChanged(object sender, RoutedEventArgs e)
    {
        var open = AiAssistToggle.IsChecked == true;
        AiAssistSidebar.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        _logger.Information("右侧栏AI辅助：{State}", open ? "展开" : "收起");
    }

    /// <summary>侧栏面板内部请求收起（关闭按钮）：复位开关并隐藏。</summary>
    private void OnAiAssistPanelCollapse(object sender, EventArgs e)
    {
        AiAssistToggle.IsChecked = false;
        AiAssistSidebar.Visibility = Visibility.Collapsed;
    }

    /// <summary>承载本卫星壳的窗口（卫星壳专用）。</summary>
    internal BrowserTearoutWindow? OwningWindow { get; set; }

    /// <summary>活动标签标题/URL 变化时触发（独立窗口据此同步标题栏）。</summary>
    public event EventHandler<string>? ActiveTitleChanged;

    /// <summary>初始化 WebShell（主壳）并创建首个标签页。</summary>
    public WebShell() : this(isSatellite: false)
    {
    }

    /// <summary>初始化 WebShell；isSatellite=true 表示独立窗口卫星壳。</summary>
    public WebShell(bool isSatellite)
    {
        IsSatellite = isSatellite;
        InitializeComponent();
        _logger = App.Services.GetService(typeof(ILogger)) as ILogger
                  ?? Serilog.Log.Logger;
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        if (!isSatellite)
        {
            _primaryShell = this;
        }

        try
        {
            Directory.CreateDirectory(DataDir);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "创建网页数据目录失败");
        }

        // 主壳 Loaded 时自动补首个标签；卫星壳的初始标签由 BrowserTearoutWindow 统一控制（避免并发双标签）
        Loaded += (_, _) =>
        {
            if (!IsSatellite)
            {
                _ = EnsureFirstTabAsync();
            }
        };
    }

    /// <summary>获取（或创建）全进程共享的 CoreWebView2Environment（默认用户数据目录，Profile/登录态一致）。
    /// internal 供 EpubReaderPage / TransformPage 等独立 WebView2 页面复用：多控件必须共享同一环境，
    /// 无参 EnsureCoreWebView2Async 会各自创建环境导致用户数据目录占用冲突（表现为 NRE）。</summary>
    internal static Task<CoreWebView2Environment> GetSharedEnvironmentAsync()
        => _sharedEnvironmentTask ??= CoreWebView2Environment.CreateAsync().AsTask();

    /// <summary>指示是否有可用网页内容。</summary>
    public bool IsAvailable => _active?.CoreWebView2 is not null;

    /// <summary>当前活动标签页 URL。</summary>
    public string? CurrentUrl => _active?.CoreWebView2?.Source;

    /// <summary>新书签添加事件。</summary>
    public event EventHandler<BookmarkItem>? BookmarkAdded;

    /// <summary>历史记录新增事件。</summary>
    public event EventHandler<HistoryItem>? HistoryAdded;

    /// <summary>下载记录变更事件。</summary>
    public event EventHandler<DownloadItem>? DownloadChanged;

    /// <summary>设置地址栏可见性。</summary>
    public void SetAddressBarVisible(bool visible)
    {
        AddressBar.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>异步抓取当前网页正文。</summary>
    public async Task<string?> GetContentAsync()
    {
        if (_active?.CoreWebView2 is null)
        {
            _logger.Warning("WebShell 无活动 WebView2，无法抓取正文");
            return null;
        }

        try
        {
            var json = await _active.CoreWebView2.ExecuteScriptAsync("document.body.innerText");
            if (string.IsNullOrEmpty(json) || json == "null")
            {
                return null;
            }
            return JsonSerializer.Deserialize<string>(json);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "抓取网页正文失败");
            return null;
        }
    }

    /// <summary>将文本注入当前网页焦点输入框（textarea / input / contenteditable）。</summary>
    public async Task<bool> InjectToActiveElementAsync(string text)
    {
        if (_active?.CoreWebView2 is null)
        {
            _logger.Warning("WebShell 无活动 WebView2，无法注入文本");
            return false;
        }
        try
        {
            var escaped = JsonSerializer.Serialize(text ?? string.Empty);
            var script = $@"
(function () {{
    var t = {escaped};
    var el = document.activeElement;
    if (!el) {{ el = document.querySelector(':focus'); }}
    if (!el) {{ return false; }}
    var tag = (el.tagName || '').toLowerCase();
    if (tag === 'textarea' || (tag === 'input' && (el.type === 'text' || el.type === 'search' || el.type === '' || el.type === 'password' || el.type === 'email' || el.type === 'url' || el.type === 'tel' || el.type === 'number'))) {{
        var start = el.selectionStart ?? el.value.length;
        var end = el.selectionEnd ?? el.value.length;
        el.value = el.value.substring(0, start) + t + el.value.substring(end);
        el.selectionStart = el.selectionEnd = start + t.length;
        el.dispatchEvent(new Event('input', {{ bubbles: true }}));
        el.dispatchEvent(new Event('change', {{ bubbles: true }}));
        return true;
    }}
    if (el.isContentEditable) {{
        el.focus();
        document.execCommand('insertText', false, t);
        el.dispatchEvent(new Event('input', {{ bubbles: true }}));
        return true;
    }}
    return false;
}})();";
            var json = await _active.CoreWebView2.ExecuteScriptAsync(script);
            var ok = string.Equals(json?.Trim(), "true", StringComparison.OrdinalIgnoreCase);
            _logger.Information("注入网页焦点框结果：{Ok}，长度：{Len}", ok, text?.Length ?? 0);
            return ok;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "注入网页焦点框失败");
            return false;
        }
    }

    /// <summary>导航当前活动标签页到指定 URL。</summary>
    public Task<bool> NavigateAsync(string url)
    {
        if (_active?.CoreWebView2 is null)
        {
            return Task.FromResult(false);
        }

        var normalized = NormalizeUrl(url?.Trim() ?? string.Empty);
        if (!Uri.TryCreate(normalized, UriKind.Absolute, out var uri))
        {
            ShowError($"无效的网址：{url}");
            return Task.FromResult(false);
        }

        ErrorBar.IsOpen = false;
        LoadingBar.Visibility = Visibility.Visible;
        try
        {
            _active.CoreWebView2.Navigate(uri.ToString());
            return Task.FromResult(true);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "导航失败：{Url}", uri);
            ShowError($"无法加载：{uri}");
            LoadingBar.Visibility = Visibility.Collapsed;
            return Task.FromResult(false);
        }
    }

    /// <summary>活动标签页后退。</summary>
    public void GoBack()
    {
        try { _active?.CoreWebView2?.GoBack(); } catch (Exception ex) { _logger.Error(ex, "后退失败"); }
    }

    /// <summary>活动标签页前进。</summary>
    public void GoForward()
    {
        try { _active?.CoreWebView2?.GoForward(); } catch (Exception ex) { _logger.Error(ex, "前进失败"); }
    }

    /// <summary>活动标签页刷新。</summary>
    public void Refresh()
    {
        try { _active?.CoreWebView2?.Reload(); } catch (Exception ex) { _logger.Error(ex, "刷新失败"); }
    }

    /// <summary>新建空白标签页。</summary>
    public void NewTab()
    {
        _ = CreateTabAsync("about:blank");
    }

    /// <summary>关闭指定 URL 的标签页。</summary>
    public void CloseTab(string url)
    {
        for (int i = _tabs.Count - 1; i >= 0; i--)
        {
            if (_tabs[i].CoreWebView2?.Source == url)
            {
                CloseTabAt(i);
                return;
            }
        }
    }

    /// <summary>加载书签列表。</summary>
    public Task<IReadOnlyList<BookmarkItem>> LoadBookmarksAsync() => LoadJsonAsync<BookmarkItem>("bookmarks.json");

    /// <summary>添加当前页面为书签。</summary>
    public async Task<bool> AddBookmarkAsync()
    {
        if (_active?.CoreWebView2 is null)
        {
            return false;
        }
        var item = new BookmarkItem
        {
            Title = _active.CoreWebView2.DocumentTitle ?? CurrentUrl ?? "无标题",
            Url = CurrentUrl ?? string.Empty
        };
        if (string.IsNullOrEmpty(item.Url) || item.Url == "about:blank")
        {
            return false;
        }
        var list = (await LoadBookmarksAsync()).ToList();
        if (list.Any(b => b.Url == item.Url))
        {
            return false;
        }
        list.Add(item);
        await SaveJsonAsync("bookmarks.json", list);
        BookmarkAdded?.Invoke(this, item);
        _logger.Information("已添加书签：{Title}", item.Title);
        return true;
    }

    /// <summary>删除书签。</summary>
    public async Task RemoveBookmarkAsync(string url)
    {
        var list = (await LoadBookmarksAsync()).Where(b => b.Url != url).ToList();
        await SaveJsonAsync("bookmarks.json", list);
    }

    /// <summary>批量删除书签（按 URL 集合）。</summary>
    public async Task RemoveBookmarksAsync(IEnumerable<string> urls)
    {
        var set = new HashSet<string>(urls, StringComparer.Ordinal);
        var list = (await LoadBookmarksAsync()).Where(b => !set.Contains(b.Url)).ToList();
        await SaveJsonAsync("bookmarks.json", list);
    }

    /// <summary>批量删除历史记录（按 URL + 访问时间精确匹配）。</summary>
    public async Task RemoveHistoryAsync(IEnumerable<HistoryItem> items)
    {
        var keys = new HashSet<string>(items.Select(i => $"{i.Url}\u0001{i.VisitedAt:O}"), StringComparer.Ordinal);
        var list = (await LoadHistoryAsync(500))
            .Where(h => !keys.Contains($"{h.Url}\u0001{h.VisitedAt:O}"))
            .ToList();
        await SaveJsonAsync("history.json", list);
    }

    /// <summary>加载历史记录。</summary>
    public Task<IReadOnlyList<HistoryItem>> LoadHistoryAsync(int limit = 200) => LoadJsonAsync<HistoryItem>("history.json", limit);

    /// <summary>记录历史。</summary>
    internal async Task AddHistoryAsync(string url, string title)
    {
        if (string.IsNullOrEmpty(url) || url == "about:blank")
        {
            return;
        }
        var item = new HistoryItem { Url = url, Title = title };
        var list = (await LoadHistoryAsync(500)).ToList();
        list.Insert(0, item);
        if (list.Count > 500)
        {
            list = list.Take(500).ToList();
        }
        await SaveJsonAsync("history.json", list);
        HistoryAdded?.Invoke(this, item);
    }

    /// <summary>清空历史记录。</summary>
    public async Task ClearHistoryAsync()
    {
        await SaveJsonAsync("history.json", new List<HistoryItem>());
    }

    /// <summary>加载下载记录。</summary>
    public Task<IReadOnlyList<DownloadItem>> LoadDownloadsAsync() => LoadJsonAsync<DownloadItem>("downloads.json");

    /// <summary>记录下载。</summary>
    internal async Task AddDownloadAsync(DownloadItem item)
    {
        var list = (await LoadDownloadsAsync()).ToList();
        var existing = list.FirstOrDefault(d => d.Url == item.Url && d.StartedAt == item.StartedAt);
        if (existing is not null)
        {
            existing.State = item.State;
            existing.Size = item.Size;
            existing.LocalPath = item.LocalPath;
        }
        else
        {
            list.Insert(0, item);
        }
        if (list.Count > 200)
        {
            list = list.Take(200).ToList();
        }
        await SaveJsonAsync("downloads.json", list);
        DownloadChanged?.Invoke(this, item);
    }

    private async Task EnsureFirstTabAsync()
    {
        if (_tabs.Count == 0)
        {
            await CreateTabAsync("about:blank");
        }
    }

    private async Task<bool> CreateTabAsync(string url)
    {
        var wv = new WebView2
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };
        try
        {
            // 使用进程内共享环境：与主壳同一 UserDataFolder / 浏览器进程，登录态（Cookie/Profile）天然共享
            var env = await GetSharedEnvironmentAsync();
            await wv.EnsureCoreWebView2Async(env);
            if (wv.CoreWebView2 is not null)
            {
                wv.CoreWebView2.NavigationCompleted += (s, e) => OnTabNavCompleted(wv, e);
                wv.CoreWebView2.SourceChanged += (s, e) => OnTabSourceChanged(wv);
                wv.CoreWebView2.DownloadStarting += (s, e) => OnDownloadStarting(wv, e);

                // ==================================
                // 关键修复：链接跳转 / window.open 一律在壳内新建 Tab，而不是独立窗口/默认浏览器
                // ==================================
                // (1) <a target="_blank">、<area target="_blank">、form target="_blank" 提交、
                //     以及 JS window.open()、showModalDialog() 等全部触发 NewWindowRequested。
                //     只要 e.Handled=true + NewWindow 自己设置，就不会弹默认浏览器/独立窗口。
                wv.CoreWebView2.NewWindowRequested += (s, args) =>
                {
                    try
                    {
                        args.Handled = true;
                        string target = args.Uri ?? string.Empty;
                        // 用户指定了 WindowFeatures? 忽略（我们统一 Tab 形式）。
                        // 在当前 WebShell 壳内新开一个标签
                        var dq = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
                        if (dq is not null)
                        {
                            dq.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Normal,
                                async () =>
                                {
                                    try { await OpenUrlInNewTabAsync(target); }
                                    catch (Exception ex) { _logger.Error(ex, "NewWindowRequested 开新标签页失败：{Url}", target); }
                                });
                        }
                        else
                        {
                            _ = OpenUrlInNewTabAsync(target);
                        }
                        _logger.Information("拦截 NewWindowRequested：新标签内打开 {Url}", target);
                    }
                    catch (Exception ex)
                    {
                        _logger.Error(ex, "处理 NewWindowRequested 异常");
                    }
                };

                if (!string.IsNullOrEmpty(url) && url != "about:blank")
                {
                    wv.CoreWebView2.Navigate(url);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "创建 WebView2 标签页失败");
        }

        _tabs.Add(wv);
        var tab = new TabViewItem
        {
            Header = "新标签",
            Tag = wv
            // 注意：此处不设置 TabViewItem.Content —— WebView2 统一放入独立 WebViewHost
        };
        AttachTabContextMenu(tab);
        TabView.TabItems.Add(tab);
        TabView.SelectedItem = tab;
        SetActive(wv);
        return true;
    }

    /// <summary>在当前壳新建一个标签并导航到指定 URL（拖回/合并、独立窗口初始打开共用入口）。</summary>
    public Task<bool> OpenUrlInNewTabAsync(string url)
    {
        var normalized = NormalizeUrl(url?.Trim() ?? string.Empty);
        return CreateTabAsync(normalized);
    }

    /// <summary>为标签挂右键菜单：主壳"在新窗口打开此标签"；卫星壳"拖回主窗口（合并）"。</summary>
    private void AttachTabContextMenu(TabViewItem tab)
    {
        try
        {
            var menu = new MenuFlyout();
            var item = new MenuFlyoutItem
            {
                Text = IsSatellite ? "拖回主窗口（合并到主窗口）" : "在新窗口打开此标签",
                Icon = new FontIcon { Glyph = "\uE8A7" }
            };
            item.Click += (_, _) =>
            {
                if (IsSatellite)
                {
                    MergeBackToPrimary();
                }
                else
                {
                    TearOutTab(tab);
                }
            };
            menu.Items.Add(item);
            tab.ContextFlyout = menu;
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "挂载标签右键菜单失败");
        }
    }

    private void SetActive(WebView2 wv)
    {
        _active = wv;
        // 关键修复：将选中的 WebView2 显式挂入独立 WebViewHost Grid，保证获得 Stretch 尺寸并真实渲染
        if (!WebViewHost.Children.Contains(wv))
        {
            // 先移除旧的（如果有）
            for (int i = WebViewHost.Children.Count - 1; i >= 0; i--)
            {
                WebViewHost.Children.RemoveAt(i);
            }
            wv.HorizontalAlignment = HorizontalAlignment.Stretch;
            wv.VerticalAlignment = VerticalAlignment.Stretch;
            WebViewHost.Children.Add(wv);
        }
        if (wv.CoreWebView2 is not null)
        {
            AddressBox.Text = wv.CoreWebView2.Source;
        }
        // 通知 IWebShellService 订阅者（如嗅探器重新挂钩 WebResourceRequested）——仅主壳，卫星壳不干扰主会话状态
        if (!IsSatellite && App.Services.GetService(typeof(IWebShellService)) is IWebShellService svc && svc is WebShellService wss)
        {
            wss.RaiseActivePageChanged();
        }
        RaiseActiveTitleChanged();
    }

    /// <summary>向订阅者（独立窗口标题栏）广播当前活动标签标题。</summary>
    private void RaiseActiveTitleChanged()
    {
        try
        {
            var title = _active?.CoreWebView2?.DocumentTitle;
            if (string.IsNullOrWhiteSpace(title))
            {
                title = _active?.CoreWebView2?.Source ?? "新标签页";
            }
            ActiveTitleChanged?.Invoke(this, title);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "广播活动标题失败");
        }
    }

    /// <summary>标签切换时更新 active 及地址栏。</summary>
    private void OnTabSelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (TabView.SelectedItem is TabViewItem tab && tab.Tag is WebView2 wv)
        {
            SetActive(wv);
        }
    }

    private void OnTabSourceChanged(WebView2 wv)
    {
        if (wv == _active && wv.CoreWebView2 is not null)
        {
            AddressBox.Text = wv.CoreWebView2.Source;
        }
        UpdateTabHeader(wv);
    }

    private void OnTabNavCompleted(WebView2 wv, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (wv == _active)
        {
            LoadingBar.Visibility = Visibility.Collapsed;
            if (!e.IsSuccess)
            {
                ShowError($"加载失败：{wv.CoreWebView2?.Source ?? "未知地址"}");
            }
            else
            {
                ErrorBar.IsOpen = false;
            }
        }
        UpdateTabHeader(wv);
        if (e.IsSuccess && wv.CoreWebView2 is not null)
        {
            _ = AddHistoryAsync(wv.CoreWebView2.Source, wv.CoreWebView2.DocumentTitle);
        }
    }

    private void UpdateTabHeader(WebView2 wv)
    {
        for (int i = 0; i < TabView.TabItems.Count; i++)
        {
            if (TabView.TabItems[i] is TabViewItem tab && tab.Tag is WebView2 t && t == wv)
            {
                tab.Header = string.IsNullOrEmpty(wv.CoreWebView2?.DocumentTitle)
                    ? (wv.CoreWebView2?.Source ?? "新标签")
                    : wv.CoreWebView2.DocumentTitle;
                // 活动标签标题变化 → 同步独立窗口标题栏
                if (wv == _active)
                {
                    RaiseActiveTitleChanged();
                }
                return;
            }
        }
    }

    private void OnDownloadStarting(WebView2 wv, CoreWebView2DownloadStartingEventArgs e)
    {
        try
        {
            var item = new DownloadItem
            {
                Url = wv.CoreWebView2?.Source ?? string.Empty,
                FileName = Path.GetFileName(e.ResultFilePath),
                LocalPath = e.ResultFilePath,
                State = DownloadState.InProgress,
                StartedAt = DateTime.UtcNow
            };
            _ = AddDownloadAsync(item);
            _logger.Information("下载开始：{File}", item.FileName);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "记录下载失败");
        }
    }

    private void CloseTabAt(int index)
    {
        if (_isShuttingDown)
        {
            return;
        }
        if (index < 0 || index >= _tabs.Count)
        {
            return;
        }
        var wv = _tabs[index];
        _tabs.RemoveAt(index);
        TabView.TabItems.RemoveAt(index);
        if (_active == wv)
        {
            // 移除可视元素，避免 WebViewHost 保留死引用
            if (WebViewHost.Children.Contains(wv))
            {
                WebViewHost.Children.Remove(wv);
            }
            // 尝试关闭 WebView2，释放底层资源
            try { wv.Close(); } catch { }
            // 切换到相邻标签
            _active = _tabs.Count > 0 ? _tabs[Math.Max(0, index - 1)] : null;
            if (_active is not null)
            {
                var tab = TabView.TabItems.Cast<TabViewItem>().FirstOrDefault(t => ReferenceEquals(t.Tag, _active));
                if (tab is not null) TabView.SelectedItem = tab;
                SetActive(_active);
            }
        }

        // 兜底交互：卫星壳最后一个标签关闭 → 整窗关闭；主壳标签全部关闭 → 保留一个新建（空白）标签页
        if (_tabs.Count == 0)
        {
            if (IsSatellite)
            {
                var window = OwningWindow;
                OwningWindow = null;
                _logger.Information("卫星窗口最后一个标签已关闭，关闭独立窗口");
                try { window?.Close(); } catch (Exception ex) { _logger.Warning(ex, "关闭卫星窗口失败"); }
            }
            else
            {
                _logger.Information("主壳标签已全部关闭，自动新建空白标签页");
                _ = CreateTabAsync("about:blank");
            }
        }
    }

    #region 标签拖出 / 拖回（Tear-out）

    /// <summary>标签开始拖动：携带自定义格式 aitb-tab-tearout + URL 文本 + 源窗口 Id/标题/URL（Properties 同步可读）。</summary>
    private void OnTabDragStarting(TabView sender, TabViewTabDragStartingEventArgs args)
    {
        try
        {
            if (args.Tab is not TabViewItem tab || tab.Tag is not WebView2 wv)
            {
                args.Cancel = true;
                return;
            }
            var url = wv.CoreWebView2?.Source ?? "about:blank";
            var title = wv.CoreWebView2?.DocumentTitle ?? tab.Header?.ToString() ?? "新标签页";
            args.Data.SetText(url);
            args.Data.SetData(TearOutFormat, url);
            args.Data.Properties[TearOutFormat] = url;
            args.Data.Properties["aitb-url"] = url;
            args.Data.Properties["aitb-title"] = title;
            args.Data.Properties["aitb-window"] = _instanceId.ToString("N");
            args.Data.RequestedOperation = DataPackageOperation.Move;
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "标签拖动数据初始化失败");
        }
    }

    /// <summary>标签被拖到标签栏之外释放（窗口外空白/窗口内其他区域）→ tear-out 为独立窗口。
    /// 若刚被其他窗口的标签栏成功接收（拖回/合并），抑制本次 tear-out 避免重复开窗。</summary>
    private void OnTabDroppedOutside(TabView sender, TabViewTabDroppedOutsideEventArgs args)
    {
        if (_isShuttingDown || args.Tab is not TabViewItem tab || tab.Tag is not WebView2 wv)
        {
            return;
        }
        var index = _tabs.IndexOf(wv);
        if (index < 0)
        {
            return;
        }
        var url = wv.CoreWebView2?.Source ?? "about:blank";
        var title = wv.CoreWebView2?.DocumentTitle ?? tab.Header?.ToString();
        if (_dispatcher is null)
        {
            _logger.Warning("DispatcherQueue 不可用，跳过标签拖出处理：{Url}", url);
            return;
        }
        // 目标窗口的 TabStripDrop 先于源窗口的 TabDroppedOutside 执行；低优先级再等一拍后复查抑制标记
        if (!_dispatcher.TryEnqueue(DispatcherQueuePriority.Low, async () =>
        {
            try
            {
                await Task.Delay(80);
                if (_remoteDropHandled is { } handled &&
                    handled.SourceId == _instanceId &&
                    string.Equals(handled.Url, url, StringComparison.OrdinalIgnoreCase) &&
                    (DateTime.UtcNow - handled.At).TotalSeconds < 15)
                {
                    _remoteDropHandled = null;
                    _logger.Information("标签已被其他窗口标签栏接收（拖回/合并），跳过 tear-out：{Url}", url);
                    return;
                }
                TearOutToNewWindow(url, title);
                CloseTabAt(index);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "处理标签拖出（tear-out）失败");
            }
        }))
        {
            _logger.Warning("DispatcherQueue 排队失败，无法处理标签拖出");
        }
    }

    /// <summary>标签栏拖拽悬停：接收其他窗口的 tear-out 标签（自身窗口内重排交给 TabView 内置逻辑）。</summary>
    private void OnTabStripDragOver(object sender, DragEventArgs e)
    {
        try
        {
            if (!e.DataView.Contains(TearOutFormat))
            {
                return;
            }
            // 源是本壳自身（窗口内重排）：不接管，交由 TabView 内置 CanReorderTabs 处理
            if (TryGetDragSourceWindowId(e, out var sourceId) && sourceId == _instanceId)
            {
                return;
            }
            e.AcceptedOperation = DataPackageOperation.Move;
            e.DragUIOverride.IsCaptionVisible = true;
            e.DragUIOverride.IsContentVisible = true;
            e.DragUIOverride.Caption = IsSatellite ? "松开以在此窗口打开" : "松开以合并到主窗口";
            e.Handled = true;
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "标签拖拽 DragOver 处理失败");
        }
    }

    /// <summary>标签栏接收拖放：读取 URL → 本壳新开该标签 → 标记"已接收" → 关闭源卫星窗口（仅剩该标签时）。</summary>
    private async void OnTabStripDrop(object sender, DragEventArgs e)
    {
        try
        {
            var view = e.DataView;
            if (!view.Contains(TearOutFormat))
            {
                return;
            }
            Guid sourceId = Guid.Empty;
            string? url = null;
            if (view.Properties.TryGetValue("aitb-window", out var winObj) && winObj is string winStr)
            {
                _ = Guid.TryParseExact(winStr, "N", out sourceId);
            }
            if (view.Properties.TryGetValue("aitb-url", out var urlObj) && urlObj is string urlStr)
            {
                url = urlStr;
            }
            if (sourceId == _instanceId)
            {
                return; // 自身窗口内重排，交由 TabView 内置逻辑
            }
            if (string.IsNullOrWhiteSpace(url))
            {
                url = await view.GetTextAsync();
            }
            if (string.IsNullOrWhiteSpace(url))
            {
                return;
            }
            url = NormalizeUrl(url.Trim());

            // 先同步落抑制标记（本事件返回后源窗口才会触发 TabDroppedOutside），再异步开标签
            MarkRemoteDropHandled(sourceId, url);
            await OpenUrlInNewTabAsync(url);
            _logger.Information("已接收拖回标签：{Url}（源窗口：{SourceId}）", url, sourceId);
            TryCloseSatelliteAfterMergeBack(sourceId, url);
            e.Handled = true;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "处理标签拖回（拖入标签栏）失败");
        }
    }

    /// <summary>右键菜单兜底：主壳"在新窗口打开此标签"。</summary>
    private void TearOutTab(TabViewItem tab)
    {
        if (tab?.Tag is not WebView2 wv)
        {
            return;
        }
        var index = _tabs.IndexOf(wv);
        if (index < 0)
        {
            return;
        }
        var url = wv.CoreWebView2?.Source ?? "about:blank";
        var title = wv.CoreWebView2?.DocumentTitle ?? tab.Header?.ToString();
        TearOutToNewWindow(url, title);
        CloseTabAt(index);
    }

    /// <summary>右键菜单兜底：卫星壳"拖回主窗口（合并到主窗口）"。</summary>
    private void MergeBackToPrimary()
    {
        var primary = _primaryShell;
        if (primary is null || ReferenceEquals(primary, this))
        {
            _logger.Warning("主壳不可用，无法合并回主窗口");
            return;
        }
        var url = _active?.CoreWebView2?.Source ?? "about:blank";
        _logger.Information("右键合并回主窗口：{Url}", url);
        _ = primary.OpenUrlInNewTabAsync(url);
        // 关闭源标签（若是最后一个标签则整窗关闭）
        RemoveTabByUrl(url);
    }

    /// <summary>创建独立浏览器窗口（卫星窗口），由其内部用 URL 重建 WebShell 标签。</summary>
    private void TearOutToNewWindow(string url, string? title)
    {
        try
        {
            var window = new BrowserTearoutWindow(url, title);
            window.Activate();
            _logger.Information("已创建独立浏览器窗口：{Url}", url);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "创建独立浏览器窗口失败：{Url}", url);
        }
    }

    /// <summary>按 URL 关闭标签（拖回后源窗口移除被拖标签；若为最后一个标签会触发整窗关闭）。</summary>
    public void RemoveTabByUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return;
        }
        for (int i = _tabs.Count - 1; i >= 0; i--)
        {
            if (string.Equals(_tabs[i].CoreWebView2?.Source, url, StringComparison.OrdinalIgnoreCase))
            {
                CloseTabAt(i);
                return;
            }
        }
    }

    /// <summary>关闭所有标签并释放 WebView2 资源（独立窗口关闭时调用，避免内核/事件泄漏）。</summary>
    public void Shutdown()
    {
        if (_isShuttingDown)
        {
            return;
        }
        _isShuttingDown = true;
        try
        {
            for (int i = _tabs.Count - 1; i >= 0; i--)
            {
                var wv = _tabs[i];
                try
                {
                    if (WebViewHost.Children.Contains(wv))
                    {
                        WebViewHost.Children.Remove(wv);
                    }
                }
                catch { }
                try { wv.Close(); } catch { }
            }
            _tabs.Clear();
            TabView.TabItems.Clear();
            _active = null;
            _logger.Information("WebShell 已关闭并清理全部 WebView2 资源");
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "WebShell Shutdown 清理失败");
        }
    }

    /// <summary>同步读取拖拽数据中的源窗口 Id。</summary>
    private static bool TryGetDragSourceWindowId(DragEventArgs e, out Guid sourceId)
    {
        sourceId = Guid.Empty;
        if (e.DataView.Properties.TryGetValue("aitb-window", out var obj) && obj is string s)
        {
            return Guid.TryParseExact(s, "N", out sourceId);
        }
        return false;
    }

    /// <summary>标记"标签已被其他窗口标签栏接收"（供源窗口的 TabDroppedOutside 抑制判定）。</summary>
    private static void MarkRemoteDropHandled(Guid sourceId, string url)
        => _remoteDropHandled = (sourceId, url, DateTime.UtcNow);

    /// <summary>拖回完成后收尾：源卫星窗口仅剩被拖这一个标签 → 整窗关闭；否则仅移除该标签。</summary>
    private static void TryCloseSatelliteAfterMergeBack(Guid sourceId, string url)
    {
        try
        {
            if (sourceId == Guid.Empty || !SatelliteWindows.TryGetValue(sourceId, out var window))
            {
                // 兜底：按 URL 反查卫星窗口（Properties 传递失败时）
                window = SatelliteWindows.Values.FirstOrDefault(w =>
                    string.Equals(w.Shell?.CurrentUrl, url, StringComparison.OrdinalIgnoreCase));
                if (window is null)
                {
                    return;
                }
            }
            window.Shell?.RemoveTabByUrl(url);
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "拖回后关闭源卫星窗口失败");
        }
    }

    /// <summary>注册卫星窗口到全局注册表。</summary>
    internal static void RegisterSatellite(BrowserTearoutWindow window)
        => SatelliteWindows[window.Id] = window;

    /// <summary>从全局注册表移除卫星窗口。</summary>
    internal static void UnregisterSatellite(Guid id)
        => SatelliteWindows.Remove(id);

    #endregion

    private void OnTabCloseRequested(TabView sender, TabViewTabCloseRequestedEventArgs args)
    {
        if (args.Item is TabViewItem tab && tab.Tag is WebView2 wv)
        {
            CloseTabAt(_tabs.IndexOf(wv));
        }
    }

    private void OnAddTabClick(TabView sender, object args)
    {
        NewTab();
    }

    private void OnAddressKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            _ = NavigateAsync(AddressBox.Text);
        }
    }

    private void OnGoClick(object sender, RoutedEventArgs e)
    {
        _ = NavigateAsync(AddressBox.Text);
    }

    private void OnBackClick(object sender, RoutedEventArgs e)
    {
        try { _active?.CoreWebView2?.GoBack(); } catch (Exception ex) { _logger.Error(ex, "后退失败"); }
    }

    private void OnForwardClick(object sender, RoutedEventArgs e)
    {
        try { _active?.CoreWebView2?.GoForward(); } catch (Exception ex) { _logger.Error(ex, "前进失败"); }
    }

    private void OnAddBookmarkClick(object sender, RoutedEventArgs e)
    {
        _ = AddBookmarkAsync();
    }

    private void OnHoverBallEntered(object sender, PointerRoutedEventArgs e)
    {
        HoverMenu.Visibility = Visibility.Visible;
    }

    private void OnHoverBallExited(object sender, PointerRoutedEventArgs e)
    {
        // 延迟隐藏，允许鼠标移动到菜单
    }

    private void OnHoverMenuEntered(object sender, PointerRoutedEventArgs e)
    {
        HoverMenu.Visibility = Visibility.Visible;
    }

    private void OnHoverMenuExited(object sender, PointerRoutedEventArgs e)
    {
        HoverMenu.Visibility = Visibility.Collapsed;
    }

    private void OnShowBookmarksClick(object sender, RoutedEventArgs e)
    {
        HoverMenu.Visibility = Visibility.Collapsed;
        _ = ShowFlyoutAsync("书签管理", async () =>
        {
            var items = await LoadBookmarksAsync();
            return items.Select(b => new WebListItem { Title = b.Title, Subtitle = b.Url, Url = b.Url, Tag = b }).ToList();
        }, async selected =>
        {
            await RemoveBookmarksAsync(selected.Select(i => i.Url));
            _logger.Information("已删除 {Count} 条书签", selected.Count);
        });
    }

    private void OnShowHistoryClick(object sender, RoutedEventArgs e)
    {
        HoverMenu.Visibility = Visibility.Collapsed;
        _ = ShowFlyoutAsync("历史记录", async () =>
        {
            var items = await LoadHistoryAsync();
            return items.Select(h => new WebListItem { Title = h.Title, Subtitle = $"{h.VisitedAt:yyyy-MM-dd HH:mm}  {h.Url}", Url = h.Url, Tag = h }).ToList();
        }, async selected =>
        {
            await RemoveHistoryAsync(selected.Select(i => (HistoryItem)i.Tag!));
            _logger.Information("已删除 {Count} 条历史记录", selected.Count);
        });
    }

    private void OnShowDownloadsClick(object sender, RoutedEventArgs e)
    {
        HoverMenu.Visibility = Visibility.Collapsed;
        _ = ShowFlyoutAsync("下载记录", async () =>
        {
            var items = await LoadDownloadsAsync();
            return items.Select(d => new WebListItem { Title = d.FileName, Subtitle = d.LocalPath, Url = d.Url, Tag = d }).ToList();
        });
    }

    /// <param name="title">对话框标题。</param>
    /// <param name="loader">加载列表项。</param>
    /// <param name="bulkDelete">传入时启用多选删除（"删除该条数据"支持多选）；为空时保持点击即导航。</param>
    private async Task ShowFlyoutAsync(string title, Func<Task<List<WebListItem>>> loader,
        Func<IReadOnlyList<WebListItem>, Task>? bulkDelete = null)
    {
        try
        {
            var list = await loader();
            var multi = bulkDelete is not null;
            // 依据当前窗口高度自适应列表高度：窗口越矮，列表越矮，保证对话框自带的按钮条（删除选中/打开网址/关闭）
            // 始终位于可视区内，不会被列表条目覆盖或挤出。
            double rootH = XamlRoot?.Size.Height ?? 720.0;
            var lv = new ListView
            {
                // 条目过多时由列表内部滚动，绝不把下方按钮挤出对话框可视区
                MaxHeight = Math.Max(140.0, Math.Min(340.0, rootH - 300.0)),
                MinWidth = 420,
                ItemsSource = list,
                SelectionMode = multi ? ListViewSelectionMode.Multiple : ListViewSelectionMode.None,
                IsItemClickEnabled = !multi,
                ItemTemplate = CreateWebListItemTemplate()
            };

            var dlg = new ContentDialog
            {
                Title = title,
                Content = lv,
                CloseButtonText = "关闭",
                XamlRoot = XamlRoot
            };

            if (multi)
            {
                dlg.PrimaryButtonText = "删除选中";
                // 「打开网址」放在对话框自带按钮条里（不随列表长度被裁切），书签/历史记录行为一致
                dlg.SecondaryButtonText = "打开网址";
                var panel = new StackPanel { Spacing = 8 };
                panel.Children.Add(new TextBlock
                {
                    Text = "勾选多条后点「删除选中」批量删除；选中一条后点「打开网址」访问。",
                    FontSize = 12,
                    Opacity = 0.7,
                    TextWrapping = TextWrapping.Wrap
                });
                panel.Children.Add(lv);
                dlg.Content = panel;

                dlg.SecondaryButtonClick += (s, args) =>
                {
                    if (lv.SelectedItems.FirstOrDefault() is WebListItem item && !string.IsNullOrEmpty(item.Url))
                    {
                        _ = NavigateAsync(item.Url);
                    }
                    else
                    {
                        args.Cancel = true; // 未选择则保持打开
                    }
                };

                dlg.PrimaryButtonClick += async (_, args) =>
                {
                    var selected = lv.SelectedItems.OfType<WebListItem>().ToList();
                    if (selected.Count == 0)
                    {
                        args.Cancel = true; // 未选择则不关闭
                        return;
                    }
                    var deferral = args.GetDeferral();
                    try
                    {
                        await bulkDelete!(selected);
                    }
                    catch (Exception ex)
                    {
                        _logger.Error(ex, "批量删除失败：{Title}", title);
                    }
                    finally
                    {
                        deferral.Complete();
                    }
                };
            }
            else
            {
                lv.ItemClick += (_, e) =>
                {
                    if (e.ClickedItem is WebListItem item && !string.IsNullOrEmpty(item.Url))
                    {
                        _ = NavigateAsync(item.Url);
                    }
                };
            }

            await dlg.ShowAsync();
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "加载悬浮球列表失败");
        }
    }

    private DataTemplate CreateWebListItemTemplate()
    {
        const string xaml = @"
<DataTemplate xmlns=""http://schemas.microsoft.com/winfx/2006/xaml/presentation"">
    <StackPanel Padding=""8,4"" Spacing=""2"">
        <TextBlock Text=""{Binding Title}"" FontWeight=""SemiBold"" TextTrimming=""CharacterEllipsis""/>
        <TextBlock Text=""{Binding Subtitle}"" FontSize=""12"" Opacity=""0.7"" TextTrimming=""CharacterEllipsis""/>
    </StackPanel>
</DataTemplate>";
        return (DataTemplate)Microsoft.UI.Xaml.Markup.XamlReader.Load(xaml);
    }

    private void ShowError(string message)
    {
        ErrorBar.Message = message;
        ErrorBar.IsOpen = true;
    }

    private static string NormalizeUrl(string url)
    {
        if (string.IsNullOrEmpty(url))
        {
            return "about:blank";
        }
        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return "https://" + url;
        }
        return url;
    }

    private async Task<IReadOnlyList<T>> LoadJsonAsync<T>(string file, int limit = 0)
    {
        var path = Path.Combine(DataDir, file);
        if (!File.Exists(path))
        {
            return Array.Empty<T>();
        }
        try
        {
            using var stream = File.OpenRead(path);
            var list = await JsonSerializer.DeserializeAsync<List<T>>(stream) ?? new List<T>();
            if (limit > 0 && list.Count > limit)
            {
                list = list.Take(limit).ToList();
            }
            return list;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "读取 {File} 失败", file);
            return Array.Empty<T>();
        }
    }

    private async Task SaveJsonAsync<T>(string file, IReadOnlyList<T> items)
    {
        var path = Path.Combine(DataDir, file);
        // 主壳与卫星窗口可能并发写同一 JSON（历史/下载），串行化避免文件共享冲突
        await JsonWriteGate.WaitAsync();
        try
        {
            using var stream = File.Create(path);
            await JsonSerializer.SerializeAsync(stream, items);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "写入 {File} 失败", file);
        }
        finally
        {
            JsonWriteGate.Release();
        }
    }

    /// <summary>悬浮球列表项。</summary>
    public sealed class WebListItem
    {
        /// <summary>标题。</summary>
        public string Title { get; set; } = string.Empty;
        /// <summary>副标题。</summary>
        public string Subtitle { get; set; } = string.Empty;
        /// <summary>URL。</summary>
        public string Url { get; set; } = string.Empty;

        /// <summary>对应的原始数据项（BookmarkItem / HistoryItem / DownloadItem），供批量删除使用。</summary>
        public object? Tag { get; set; }
    }
}
