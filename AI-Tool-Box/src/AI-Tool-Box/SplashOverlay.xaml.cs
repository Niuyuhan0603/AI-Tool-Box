using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media.Imaging;
using Serilog;

namespace AI_Tool_Box;

/// <summary>启动覆盖层控件：展示 SplashScreen.png + 苯环绘制动画，播放完毕后淡出，由宿主窗口控制可见性。</summary>
public sealed partial class SplashOverlay : UserControl
{
    // 覆盖层停留时长需覆盖 Storyboard 全程（约 1.75s）+ 约 0.85s 停顿
    private static readonly TimeSpan LogoDisplayDuration = TimeSpan.FromMilliseconds(2600);
    private static readonly TimeSpan FadeOutDuration = TimeSpan.FromMilliseconds(500);

    private readonly ILogger _logger;

    /// <summary>初始化 SplashOverlay：加载 Splash 图像并开始苯环绘制动画。</summary>
    public SplashOverlay()
    {
        InitializeComponent();

        _logger = App.Services.GetService(typeof(ILogger)) as ILogger
                  ?? Serilog.Log.Logger;

        LoadLogo();
        SplashStoryboard.Begin();
        _logger.Information("SplashOverlay 已加载，苯环绘制动画开始");
    }

    /// <summary>启动动画：覆盖层停留至动画播放完毕后淡出 0.5s，完成后将自身置为 Collapsed。</summary>
    /// <returns>表示启动动画完成的任务。</returns>
    public async Task PlayStartupAsync()
    {
        // 1. 停留阶段（不阻塞 UI 线程，Storyboard 同步进行）
        await Task.Delay(LogoDisplayDuration);

        // 2. 淡出阶段（Composition 动画作用于根 Grid 的 Visual.Opacity）
        await FadeOutAsync(FadeOutDuration);

        // 3. 动画完成后隐藏覆盖层，露出下方主界面
        Visibility = Visibility.Collapsed;
        _logger.Information("SplashOverlay 已隐藏，主界面可见");
    }

    /// <summary>加载 SplashScreen.png；缺失时记录日志不抛异常（保留 XAML 相对路径兜底）。</summary>
    private void LoadLogo()
    {
        try
        {
            var splashPath = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "SplashScreen.png");
            if (System.IO.File.Exists(splashPath))
            {
                LogoImage.Source = new BitmapImage(new System.Uri(splashPath));
                _logger.Information("Splash 图像已加载：{Path}", splashPath);
            }
            else
            {
                _logger.Warning("Splash 图像不存在：{Path}（依赖 XAML 相对路径兜底）", splashPath);
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Splash 图像加载失败");
        }
    }

    /// <summary>对根 Grid 应用 Opacity 淡出动画；动画完成或失败均返回。</summary>
    /// <param name="duration">动画时长。</param>
    private async Task FadeOutAsync(TimeSpan duration)
    {
        try
        {
            var visual = ElementCompositionPreview.GetElementVisual(RootGrid);
            if (visual is null)
            {
                _logger.Warning("无法获取 RootGrid Visual，跳过淡出动画");
                await Task.Delay(duration);
                return;
            }

            var compositor = visual.Compositor;
            var animation = compositor.CreateScalarKeyFrameAnimation();
            animation.InsertKeyFrame(0.0f, 1.0f);
            animation.InsertKeyFrame(1.0f, 0.0f);
            animation.Duration = duration;

            visual.StartAnimation("Opacity", animation);
            await Task.Delay(duration);

            _logger.Information("淡出动画完成");
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "淡出动画异常");
            await Task.Delay(duration);
        }
    }
}
