using AI_Tool_Box.Controls;
using AI_Tool_Box.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;

namespace AI_Tool_Box.Views;

public sealed partial class TransformPage : Page
{
    public TransformViewModel ViewModel { get; }

    public TransformPage()
    {
        InitializeComponent();
        ViewModel = App.Services.GetRequiredService<TransformViewModel>();
        DataContext = ViewModel;

        // 初始选中项与当前模式对齐（若当前为下载模式/未知，则回落到默认的 DOCX→PDF）
        SyncSelectorsFromMode();

        // 隐藏 WebView2 工具页：PDF 工具（构建后随 Assets\Web 内容复制到输出目录）
        PdfWeb.Source = new Uri(AppContext.BaseDirectory + "Assets/Web/pdf.html");
        // 关键修复：复用 WebShell 共享 Environment，避免无参 EnsureCoreWebView2Async 另建环境
        // 与用户数据目录冲突（NRE 根因）。WinUI 3 版 WebView2 无 CoreWebView2InitializationCompleted 事件。
        PdfWeb.Loaded += async (_, _) =>
        {
            try
            {
                await PdfWeb.EnsureCoreWebView2Async(await WebShell.GetSharedEnvironmentAsync());
                PdfWeb.CoreWebView2.WebMessageReceived += (_, e) => ViewModel.OnPdfWebMessage(e.WebMessageAsJson);
                ViewModel.AttachPdfWeb(PdfWeb.CoreWebView2);
            }
            catch (Exception ex)
            {
                ViewModel.OnPdfWebInitFailed(ex.Message);
            }
        };

        // 隐藏 WebView2：专用于 DOCX → PDF 的打印渲染（打印前会 Navigate 到临时 HTML）
        PrintWeb.Loaded += async (_, _) =>
        {
            try
            {
                await PrintWeb.EnsureCoreWebView2Async(await WebShell.GetSharedEnvironmentAsync());
                ViewModel.AttachPrintWeb(PrintWeb.CoreWebView2);
            }
            catch (Exception ex)
            {
                ViewModel.OnPdfWebInitFailed(ex.Message);
            }
        };
    }

    // ==================== 列内选项 ↔ 明细面板 ====================

    /// <summary>
    /// 左列（办公文件）选中项变化：仅展开被选中项的参数明细；
    /// 对"模式类"选项同步 <see cref="TransformViewModel.Mode"/>（图片/PDF 工具类选项不改动 Mode）。
    /// </summary>
    private void OnOfficeSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var idx = OfficeSelector.SelectedIndex;
        OfficeDocxToPdfPanel.Visibility = idx == 0 ? Visibility.Visible : Visibility.Collapsed;
        OfficePdfToDocxPanel.Visibility = idx == 1 ? Visibility.Visible : Visibility.Collapsed;
        OfficeImagesToPdfPanel.Visibility = idx == 2 ? Visibility.Visible : Visibility.Collapsed;
        OfficeMergePdfPanel.Visibility = idx == 3 ? Visibility.Visible : Visibility.Collapsed;
        OfficePdfToPngPanel.Visibility = idx == 4 ? Visibility.Visible : Visibility.Collapsed;

        if (idx == 0) ViewModel.Mode = TransformMode.DocxToPdf;
        else if (idx == 1) ViewModel.Mode = TransformMode.PdfToDocx;
    }

    /// <summary>
    /// 右列（音视频处理）选中项变化：仅展开被选中项的参数明细；
    /// 对"模式类"选项同步 <see cref="TransformViewModel.Mode"/>（NCM 为命令类选项，不改动 Mode）。
    /// </summary>
    private void OnMediaSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var idx = MediaSelector.SelectedIndex;
        MediaVideoToMp4Panel.Visibility = idx == 0 ? Visibility.Visible : Visibility.Collapsed;
        MediaConvertPanel.Visibility = idx == 1 ? Visibility.Visible : Visibility.Collapsed;
        NcmPanel.Visibility = idx == 2 ? Visibility.Visible : Visibility.Collapsed;

        if (idx == 0) ViewModel.Mode = TransformMode.VideoToMp4;
        else if (idx == 1) ViewModel.Mode = TransformMode.MediaConvert;
    }

    // ==================== 执行按钮 ====================

    /// <summary>运行前先锁定本按钮对应的模式，再交给统一的 RunCommand，避免左右两列模式互相覆盖。</summary>
    private void OnRunDocxToPdf(object sender, RoutedEventArgs e) => RunMode(TransformMode.DocxToPdf);
    private void OnRunPdfToDocx(object sender, RoutedEventArgs e) => RunMode(TransformMode.PdfToDocx);
    private void OnRunVideoToMp4(object sender, RoutedEventArgs e) => RunMode(TransformMode.VideoToMp4);
    private void OnRunMediaConvert(object sender, RoutedEventArgs e) => RunMode(TransformMode.MediaConvert);

    private void RunMode(TransformMode mode)
    {
        ViewModel.Mode = mode;
        if (ViewModel.RunCommand.CanExecute(null)) ViewModel.RunCommand.Execute(null);
    }

    /// <summary>把当前 <see cref="TransformViewModel.Mode"/> 反映到对应列的选中项（触发 SelectionChanged 展开明细）。</summary>
    private void SyncSelectorsFromMode()
    {
        switch (ViewModel.Mode)
        {
            case TransformMode.PdfToDocx:
                OfficeSelector.SelectedIndex = 1;
                break;
            case TransformMode.VideoToMp4:
                MediaSelector.SelectedIndex = 0;
                break;
            case TransformMode.MediaConvert:
                MediaSelector.SelectedIndex = 1;
                break;
            case TransformMode.DocxToPdf:
            default:
                // 非本页可编辑模式（如 LinkDownload）：回落到默认的 DOCX→PDF
                ViewModel.Mode = TransformMode.DocxToPdf;
                OfficeSelector.SelectedIndex = 0;
                break;
        }
    }
}
