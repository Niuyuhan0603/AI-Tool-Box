using AI_Tool_Box.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;

namespace AI_Tool_Box.Views;

/// <summary>流媒体 / 直链下载页（从「文件转换」页拆分出来的独立侧栏功能）。</summary>
/// <remarks>
/// 复用单例 <see cref="TransformViewModel"/> 作为 DataContext，仅使用其下载相关能力
/// （DownloadUrl / HttpConcurrency / M3u8Concurrency / OutputPath / RunCommand / IsBusy / Progress / Status）。
/// 页面加载时把 <see cref="TransformViewModel.Mode"/> 置为 <see cref="TransformMode.LinkDownload"/>。
/// </remarks>
public sealed partial class StreamDownloadPage : Page
{
    public TransformViewModel ViewModel { get; }

    public StreamDownloadPage()
    {
        InitializeComponent();
        ViewModel = App.Services.GetRequiredService<TransformViewModel>();
        DataContext = ViewModel;

        // 该页面专职下载：进入即切换到直链/m3u8 下载模式
        ViewModel.Mode = TransformMode.LinkDownload;
    }
}
