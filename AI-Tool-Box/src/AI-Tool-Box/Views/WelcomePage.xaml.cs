using AI_Tool_Box.Controls;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.Web.WebView2.Core;
using Serilog;

namespace AI_Tool_Box.Views;

/// <summary>欢迎页：顶部欢迎文字 + 下方蜂巢苯环绘制 + 代码雨背景动画。
/// 动画由 Assets/Web/welcome.html 的 Canvas 实现：6 个苯环以蜂巢排列逐边顺时针绘制→保持→淡出→循环；
/// 背景叠加青色高透明度代码雨（Matrix 风格滚动）。</summary>
public sealed partial class WelcomePage : Page
{
    private readonly ILogger _logger;

    public WelcomePage()
    {
        InitializeComponent();
        _logger = App.Services.GetService(typeof(ILogger)) as ILogger
                  ?? Serilog.Log.Logger;
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        try
        {
            // 复用 WebShell 共享 Environment，避免另建环境与用户数据目录冲突
            await WelcomeWeb.EnsureCoreWebView2Async(await WebShell.GetSharedEnvironmentAsync());
            WelcomeWeb.CoreWebView2.SetVirtualHostNameToFolderMapping(
                "welcome.app",
                System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "Web"),
                CoreWebView2HostResourceAccessKind.Allow);
            WelcomeWeb.CoreWebView2.Navigate("https://welcome.app/welcome.html");
            _logger.Information("欢迎页动画已加载");
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "欢迎页 WebView2 初始化失败");
        }
    }
}
