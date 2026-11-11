using AI_Tool_Box.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using Serilog;

namespace AI_Tool_Box.Views;

/// <summary>Edge 扩展引入测试页：内测人员解锁后可用。以独立 WebView2 环境
/// （独立用户数据目录 + AreBrowserExtensionsEnabled=true）承载浏览器，把"设置→内测人员"
/// 中已启用的扩展通过 Profile.TryAddBrowserExtensionAsync 装入该浏览器。</summary>
public sealed partial class EdgeExtensionTestPage : Page
{
    private static readonly ILogger Log = Serilog.Log.ForContext<EdgeExtensionTestPage>();

    /// <summary>静态保留 WebView2：页面反复进出不重初始化，扩展安装状态与浏览会话保持。</summary>
    private static WebView2? _web;

    public EdgeExtensionTestPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_web is null)
        {
            _web = new WebView2
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch
            };
            _ = InitAsync();
        }
        if (!ReferenceEquals(WebHost.Child, _web))
        {
            WebHost.Child = _web;
        }
    }

    /// <summary>初始化独立环境（AreBrowserExtensionsEnabled）+ 装载已启用扩展 + 打开主页。</summary>
    private static async Task InitAsync()
    {
        try
        {
            var userData = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AI-Tool-Box", "WebView2_ExtTest");
            var options = new CoreWebView2EnvironmentOptions
            {
                // 必须在环境创建前开启：Profile.TryAddBrowserExtensionAsync 依赖此开关
                AreBrowserExtensionsEnabled = true
            };
            // 独立用户数据目录：与 WebShell 共享环境（默认目录）隔离，互不占用
            var env = await CoreWebView2Environment.CreateWithOptionsAsync(null, userData, options);
            await _web!.EnsureCoreWebView2Async(env);

            _web.CoreWebView2.SourceChanged += (_, e) =>
            {
                if (!e.IsNewDocument) return;
                SetUrlBox(_web.CoreWebView2.Source);
            };

            await AddEnabledExtensionsAsync();
            _web.CoreWebView2.Navigate("https://www.bing.com");
            Log.Information("Edge扩展引入测试页：WebView2 已就绪（独立环境 {Dir}）", userData);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Edge扩展引入测试页初始化失败");
            SetStatus($"初始化失败：{ex.Message}");
        }
    }

    /// <summary>把"设置→内测人员"中全部已启用扩展装入 WebView2 Profile（已安装的直接沿用）。</summary>
    private static async Task AddEnabledExtensionsAsync()
    {
        if (_web?.CoreWebView2 is null) return;
        try
        {
            var edge = App.Services.GetRequiredService<EdgeExtensionService>();
            var enabled = edge.Store.Extensions
                .Where(e => e.Enabled && Directory.Exists(e.InstallPath))
                .ToList();

            SetStatus(enabled.Count == 0
                ? "没有已启用的扩展：请在「设置 → 内测人员」导入扩展并打开开关。"
                : $"正在装载 {enabled.Count} 个已启用扩展…");

            // 已安装清单：避免重复添加（AddBrowserExtensionAsync 对重复扩展会抛异常）
            var installed = (await _web.CoreWebView2.Profile.GetBrowserExtensionsAsync())?.ToList()
                            ?? new List<CoreWebView2BrowserExtension>();
            var ok = 0;
            var skip = 0;
            foreach (var ext in enabled)
            {
                try
                {
                    var extension = await _web.CoreWebView2.Profile.AddBrowserExtensionAsync(ext.InstallPath);
                    if (extension is not null && !extension.IsEnabled)
                    {
                        await extension.EnableAsync(true);
                    }
                    ok++;
                }
                catch (Exception ex)
                {
                    skip++;
                    Log.Warning(ex, "扩展装载失败（可能已安装）：{Name} {Path}", ext.Name, ext.InstallPath);
                }
            }

            SetStatus(enabled.Count == 0
                ? "没有已启用的扩展：请在「设置 → 内测人员」导入扩展并打开开关。"
                : $"扩展就绪：本次装入 {ok} 个{(skip > 0 ? $"，沿用/跳过 {skip} 个" : string.Empty)}。" +
                  $"扩展随本页浏览器（独立配置目录）生效，可在「设置 → 内测人员」调整开关后点击「重载扩展」。");
            Log.Information("Edge扩展引入测试页：扩展装载完成（装入 {Ok}，沿用/跳过 {Skip}，已装 {Total}）",
                ok, skip, installed.Count);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "装载 Edge 扩展失败");
            SetStatus($"装载扩展失败：{ex.Message}");
        }
    }

    private void OnGoClick(object sender, RoutedEventArgs e) => NavigateFromBox();

    private void OnUrlKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            NavigateFromBox();
        }
    }

    private void OnRefreshClick(object sender, RoutedEventArgs e)
    {
        try { _web?.CoreWebView2?.Reload(); } catch (Exception ex) { Log.Warning(ex, "刷新失败"); }
    }

    /// <summary>「重载扩展」：设置页调整开关后手动触发，重新按已启用列表装载。</summary>
    private void OnReloadExtensionsClick(object sender, RoutedEventArgs e)
    {
        _ = AddEnabledExtensionsAsync();
    }

    private void NavigateFromBox()
    {
        if (_web?.CoreWebView2 is null) return;
        var raw = UrlBox.Text?.Trim() ?? string.Empty;
        if (raw.Length == 0) return;
        if (!raw.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !raw.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            raw = "https://" + raw;
        }
        try
        {
            _web.CoreWebView2.Navigate(raw);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "导航失败：{Url}", raw);
            SetStatus($"无法打开：{raw}");
        }
    }

    /// <summary>URL 框仅在非焦点时同步（避免输入被覆盖）。</summary>
    private static void SetUrlBox(string? url)
    {
        RunOnPage(p => p.UrlBox.Text = url ?? string.Empty);
    }

    private static void SetStatus(string text) => RunOnPage(p => p.ExtStatusText.Text = text);

    /// <summary>静态方法需触达页面实例：沿视觉树找当前 EdgeExtensionTestPage（单实例导航，安全）。</summary>
    private static void RunOnPage(Action<EdgeExtensionTestPage> action)
    {
        try
        {
            if (App.MainWindow?.Content is not FrameworkElement root) return;
            var page = FindPage(root);
            if (page is not null) action(page);
        }
        catch { /* 页面可能已卸载，忽略 */ }
    }

    private static EdgeExtensionTestPage? FindPage(DependencyObject node)
    {
        if (node is EdgeExtensionTestPage p) return p;
        int count = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(node);
        for (int i = 0; i < count; i++)
        {
            var found = FindPage(Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(node, i));
            if (found is not null) return found;
        }
        return null;
    }
}
