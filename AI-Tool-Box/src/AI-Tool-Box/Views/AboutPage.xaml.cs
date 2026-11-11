using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Serilog;

namespace AI_Tool_Box.Views;

/// <summary>关于软件页：版本信息 / 功能特性 / 赞助码（微信 + 支付宝，图片随 Assets\Web 复制到输出目录）。</summary>
public sealed partial class AboutPage : Page
{
    private static readonly ILogger Log = Serilog.Log.ForContext<AboutPage>();

    public AboutPage()
    {
        InitializeComponent();
        VersionText.Text = $"版本 1.0.0 · Windows 10 / 11 (x64)";

        // Logo 与赞助码使用 file:// 绝对路径加载（unpackaged 应用无 ms-appx 协议）
        try
        {
            var baseDir = AppContext.BaseDirectory;
            LogoImage.Source = new BitmapImage(new Uri(System.IO.Path.Combine(baseDir, "Assets", "H+_logo-transparent.ico")));
            WechatImage.Source = new BitmapImage(new Uri(System.IO.Path.Combine(baseDir, "Assets", "Web", "about", "wechat_pay.png")));
            AlipayImage.Source = new BitmapImage(new Uri(System.IO.Path.Combine(baseDir, "Assets", "Web", "about", "alipay.png")));
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "关于页资源加载失败");
        }
    }
}
