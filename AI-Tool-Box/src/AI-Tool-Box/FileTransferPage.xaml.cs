using AI_Tool_Box.Services;
using AI_Tool_Box.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Serilog;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace AI_Tool_Box.Views;

/// <summary>局域网传文件页：附近设备 + 发送队列（选择/拖放加入，先选文件后发送）+ 传输记录；接收请求弹窗确认。</summary>
public sealed partial class FileTransferPage : Page
{
    private readonly ILogger _logger;
    private readonly FileTransferViewModel _viewModel;
    private readonly ILanTransferService _transferService;

    /// <summary>供 XAML x:Bind 绑定。</summary>
    public FileTransferViewModel ViewModel => _viewModel;

    /// <summary>初始化 FileTransferPage。</summary>
    public FileTransferPage()
    {
        InitializeComponent();
        _logger = App.Services.GetService(typeof(ILogger)) as ILogger
                  ?? Serilog.Log.Logger;
        _viewModel = App.Services.GetRequiredService<FileTransferViewModel>();
        _transferService = App.Services.GetRequiredService<ILanTransferService>();
        DataContext = _viewModel;
        // 页面实例会被导航缓存复用：每次进入都重新挂接接收请求事件，离开时注销
        Loaded += OnPageLoaded;
        Unloaded += OnPageUnloaded;
        _logger.Information("FileTransferPage 已加载（首次需点击「开始监听」）");
    }

    private void OnPageLoaded(object sender, RoutedEventArgs e)
    {
        _transferService.IncomingTransferRequested -= OnIncomingTransferRequested;
        _transferService.IncomingTransferRequested += OnIncomingTransferRequested;
    }

    private void OnPageUnloaded(object sender, RoutedEventArgs e)
    {
        _transferService.IncomingTransferRequested -= OnIncomingTransferRequested;
    }

    /// <summary>收到传输请求：弹窗确认（服务端等待 Respond，60 秒超时自动拒绝）。</summary>
    private async void OnIncomingTransferRequested(LanIncomingRequest request)
    {
        try
        {
            var panel = new StackPanel { Spacing = 8 };
            panel.Children.Add(new TextBlock
            {
                Text = $"设备：{request.RequesterDisplay}",
                TextWrapping = TextWrapping.Wrap,
            });
            foreach (var f in request.Files.Take(8))
            {
                panel.Children.Add(new TextBlock
                {
                    Text = $"• {f.FileName}（{TransferProgress.FormatSize(f.Size)}）",
                    TextWrapping = TextWrapping.Wrap,
                });
            }
            if (request.Files.Count > 8)
            {
                panel.Children.Add(new TextBlock
                {
                    Text = $"… 共 {request.Files.Count} 个文件",
                    Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                        Microsoft.UI.Colors.Gray),
                });
            }

            var dialog = new ContentDialog
            {
                Title = "收到文件传输请求",
                Content = panel,
                PrimaryButtonText = "接收",
                CloseButtonText = "拒绝",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = Content.XamlRoot,
            };
            var result = await dialog.ShowAsync();
            request.Respond(result == ContentDialogResult.Primary);
            _logger.Information("用户{Action}来自 {Peer} 的传输请求（{Count} 个文件）",
                result == ContentDialogResult.Primary ? "接受" : "拒绝", request.RequesterDisplay, request.Files.Count);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "显示传输请求对话框失败，自动拒绝");
            request.Respond(false);
        }
    }

    /// <summary>拖放区 DragOver：数据含文件项时允许复制并高亮拖放区。</summary>
    private void OnDropZoneDragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            SetDropZoneHighlight(true);
        }
        else
        {
            e.AcceptedOperation = DataPackageOperation.None;
        }
    }

    /// <summary>拖放区 DragLeave：取消高亮。</summary>
    private void OnDropZoneDragLeave(object sender, DragEventArgs e) => SetDropZoneHighlight(false);

    /// <summary>拖放区 Drop：取 StorageItems 的文件路径加入发送队列。</summary>
    private async void OnDropZoneDrop(object sender, DragEventArgs e)
    {
        SetDropZoneHighlight(false);
        try
        {
            if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
            var items = await e.DataView.GetStorageItemsAsync();
            var paths = items.OfType<StorageFile>()
                             .Select(f => f.Path)
                             .Where(p => !string.IsNullOrWhiteSpace(p))
                             .ToList();
            if (paths.Count == 0) return;
            _viewModel.AddFiles(paths);
            _logger.Information("拖放加入待发送文件 {Count} 个", paths.Count);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "处理拖放文件失败");
        }
    }

    /// <summary>拖放区高亮开关：强调色淡底 + 虚线加粗。</summary>
    private void SetDropZoneHighlight(bool on)
    {
        DropHighlight.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        DropDashOuter.StrokeThickness = on ? 2 : 1;
        DropDashInner.StrokeThickness = on ? 2 : 1;
    }

    /// <summary>在资源管理器中打开日志目录（失败时可据此查看完整报错）。</summary>
    private void OnOpenLogDirClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var dir = LoggerSetup.CurrentLogRoot;
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = dir,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "打开日志目录失败");
            _viewModel.Status = $"打开日志目录失败：{ex.Message}（目录：{LoggerSetup.CurrentLogRoot}）";
        }
    }

    /// <summary>取消一条进行中的传输。</summary>
    private void OnCancelTransferClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is TransferProgress progress)
        {
            progress.TryCancel();
            _logger.Information("用户请求取消传输：{File}", progress.FileName);
        }
    }
}
