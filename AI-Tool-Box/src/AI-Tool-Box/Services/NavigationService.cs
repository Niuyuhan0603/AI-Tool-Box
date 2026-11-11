using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Serilog;

namespace AI_Tool_Box.Services;

/// <summary>基于 WinUI 3 <see cref="Frame"/> 的默认导航服务实现。</summary>
public sealed class NavigationService : INavigationService
{
    private readonly ILogger _logger;
    private Frame? _frame;

    /// <summary>初始化导航服务实例。</summary>
    /// <param name="logger">注入的 <see cref="ILogger"/>。</param>
    public NavigationService(ILogger logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc/>
    public bool CanGoBack => _frame?.CanGoBack ?? false;

    /// <inheritdoc/>
    public bool CanGoForward => _frame?.CanGoForward ?? false;

    /// <inheritdoc/>
    public void Initialize(Frame frame)
    {
        _frame = frame ?? throw new ArgumentNullException(nameof(frame));
        // 页面状态保持：每个功能页都缓存实例，切走时只是从可视树移除（内容/滚动位置/输入全部保留），
        // 再次进入时复用同一实例，不会重建页面。
        _frame.CacheSize = 60;
        _frame.Navigating += OnFrameNavigating;
        _frame.Navigated += OnFrameNavigated;
        _logger.Information("导航服务已绑定 Frame 实例（已启用页面实例缓存，CacheSize={CacheSize}）", _frame.CacheSize);
    }

    /// <summary>
    /// 不缓存的功能页：它们的网页内容保存在共享 WebShell 中（切走只是归还 WebShell），
    /// 且 Unloaded 里会注销 Loaded/Unloaded 事件，缓存实例会导致返回后 WebShell 无法重新挂载。
    /// </summary>
    private static readonly HashSet<string> NonCachedPageNames = new(StringComparer.Ordinal)
    {
        "BrowserPage",
        "SnifferPage",
        "AutoAnswerPage",
    };

    /// <summary>已出现过的页面实例，仅用于日志诊断“是否复用缓存实例”。</summary>
    private readonly HashSet<Page> _seenPages = new();

    /// <summary>功能页实例缓存：离开页面时保留实例，再次进入直接复用（内容/输入/滚动位置不丢失）。</summary>
    private readonly Dictionary<Type, Page> _pageCache = new();

    /// <summary>
    /// 关键：在“离开当前页之前”把旧页标记为 Required。
    /// 只在 Navigated 里给新页设置缓存在 WinUI 3 上不可靠（旧页可能已被判定为 Disabled 而不入缓存），
    /// 这里在 Navigating 阶段（Frame.Content 仍是旧页）再兜底设置一次。
    /// </summary>
    private void OnFrameNavigating(object sender, NavigatingCancelEventArgs e)
    {
        if (_frame?.Content is not Page current) return;
        if (NonCachedPageNames.Contains(current.GetType().Name)) return;
        if (current.NavigationCacheMode != NavigationCacheMode.Required)
        {
            current.NavigationCacheMode = NavigationCacheMode.Required;
        }
    }

    private void OnFrameNavigated(object sender, NavigationEventArgs e)
    {
        if (e.Content is not Page page) return;
        if (NonCachedPageNames.Contains(page.GetType().Name))
        {
            page.NavigationCacheMode = NavigationCacheMode.Disabled;
            _seenPages.Remove(page);
            return;
        }
        if (page.NavigationCacheMode != NavigationCacheMode.Required)
        {
            // 页面实例缓存：切换功能时旧页只从可视树移除，实例与输入/滚动/内容全部保留
            page.NavigationCacheMode = NavigationCacheMode.Required;
        }
        var isNew = _seenPages.Add(page);
        _logger.Information("页面缓存：{Page}（{Kind}）", page.GetType().Name, isNew ? "新建实例" : "复用缓存实例");
    }

    /// <inheritdoc/>
    public bool Navigate<TView>(object? parameter = null) where TView : Page
        => Navigate(typeof(TView), parameter);

    /// <inheritdoc/>
    public bool Navigate(Type viewType, object? parameter = null)
    {
        if (viewType is null)
        {
            throw new ArgumentNullException(nameof(viewType));
        }
        if (_frame is null)
        {
            _logger.Warning("导航失败：Frame 尚未初始化。请求目标：{ViewType}", viewType.FullName);
            return false;
        }
        if (!typeof(Page).IsAssignableFrom(viewType))
        {
            _logger.Error("导航失败：目标类型 {ViewType} 不是 Page 派生类", viewType.FullName);
            return false;
        }

        try
        {
            var cacheable = !NonCachedPageNames.Contains(viewType.Name);

            // 关键修复：若当前页与目标页类型相同，Frame.Navigate 会"静默跳过"（返回 true 但不重加载），
            // 导致「网址导航」和「右侧栏AI辅助」都用 BrowserPage 时切换模式无效（AI 侧栏残留/关闭）。
            // 解决：同类型导航时先清空 Content（强制造新一轮 OnNavigatedFrom + 卸载），清栈再导航。
            if (_frame.Content is Page current && current.GetType() == viewType)
            {
                _logger.Information("强制重建同类型页面：{ViewType}（Parameter={Param}）",
                    viewType.FullName, parameter ?? "null");
                _pageCache.Remove(viewType);
                _frame.BackStack.Clear();
                _frame.ForwardStack.Clear();
                _frame.Content = null;
                GC.Collect(2, GCCollectionMode.Forced, blocking: false);
            }

            // 页面状态保持（不依赖 Frame 的缓存实现）：首次进入用 Frame.Navigate 建立页面，
            // 之后再次进入直接把同一实例塞回 Frame.Content —— 页面内容/输入/滚动位置原样保留。
            if (cacheable && _pageCache.TryGetValue(viewType, out var cached))
            {
                _frame.Content = cached;
                _logger.Information("复用缓存页面：{ViewType}", viewType.Name);
                return true;
            }

            // SuppressNavigationTransitionInfo：禁用页面切换过渡动画。
            // 默认过渡期间新旧页面会同时在可视树共存约 0.7s（新页 Loaded 早于旧页 Unloaded），
            // 是"页面 UI 残留/叠影"问题源之一；禁用后旧页立即卸载，杜绝叠影窗口。
            var ok = _frame.Navigate(viewType, parameter, new Microsoft.UI.Xaml.Media.Animation.SuppressNavigationTransitionInfo());
            if (ok && cacheable && _frame.Content is Page created)
            {
                _pageCache[viewType] = created;
            }
            _logger.Information("导航到 {ViewType}（结果：{Result}, Param={Param}）",
                viewType.FullName, ok, parameter ?? "null");
            return ok;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "导航异常：目标 {ViewType}", viewType.FullName);
            return false;
        }
    }

    /// <inheritdoc/>
    public void GoBack()
    {
        if (_frame is null || !_frame.CanGoBack)
        {
            _logger.Warning("无法向后导航：Frame 为空或历史栈已空");
            return;
        }
        try
        {
            _frame.GoBack();
            _logger.Information("向后导航完成");
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "向后导航异常");
        }
    }

    /// <inheritdoc/>
    public void GoForward()
    {
        if (_frame is null || !_frame.CanGoForward)
        {
            _logger.Warning("无法向前导航：Frame 为空或前向历史已空");
            return;
        }
        try
        {
            _frame.GoForward();
            _logger.Information("向前导航完成");
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "向前导航异常");
        }
    }
}
