using AI_Tool_Box.Services;
using AI_Tool_Box.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Serilog;

namespace AI_Tool_Box.Views;

/// <summary>设置页（集中控制面板）：用量统计 / 线程配置 / FFmpeg 路径 / API Key / 数据管理。
/// 各功能页的并发参数由 AppSettingsService 统一持久化；用量统计由 UsageTracker 实时记录。</summary>
public sealed partial class SettingsPage : Page
{
    private readonly ILogger _logger;
    public SettingsViewModel ViewModel { get; }

    public SettingsPage()
    {
        InitializeComponent();
        _logger = App.Services.GetService(typeof(ILogger)) as ILogger
                  ?? Serilog.Log.Logger;
        ViewModel = App.Services.GetRequiredService<SettingsViewModel>();
        DataContext = ViewModel;

        // 关闭窗口行为：恢复已保存选择（默认缩小至托盘）
        try
        {
            if (App.Services.GetService(typeof(IAppSettingsService)) is IAppSettingsService appSettings)
            {
                (appSettings.CloseBehavior == "exit" ? CloseToExitRadio : CloseToTrayRadio).IsChecked = true;
            }
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "恢复关闭窗口行为设置失败");
        }
    }

    /// <summary>关闭窗口行为变更：缩小至托盘 / 直接退出（持久化到 app_settings.json）。</summary>
    private void OnCloseBehaviorChecked(object sender, RoutedEventArgs e)
    {
        try
        {
            if (App.Services.GetService(typeof(IAppSettingsService)) is IAppSettingsService appSettings)
            {
                appSettings.CloseBehavior = ReferenceEquals(sender, CloseToExitRadio) ? "exit" : "tray";
            }
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "保存关闭窗口行为失败");
        }
    }

    // 用量展开时刷新一次快照（避免长时间不打开页面导致数据陈旧）
    private void OnRefreshUsageClick(object sender, RoutedEventArgs e) => ViewModel.RefreshUsage();
    private void OnResetUsageClick(object sender, RoutedEventArgs e) => ViewModel.ResetUsage();

    // FFmpeg 路径选择
    private async void OnPickFfmpegClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new Windows.Storage.Pickers.FileOpenPicker
            {
                SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.ComputerFolder
            };
            picker.FileTypeFilter.Add(".exe");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow));
            var file = await picker.PickSingleFileAsync();
            if (file is null) return;
            ViewModel.SetFfmpegPath(file.Path);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "选择 ffmpeg.exe 失败");
        }
    }

    private void OnClearFfmpegClick(object sender, RoutedEventArgs e) => ViewModel.ClearFfmpegPathCommand.Execute(null);

    // 右侧栏AI辅助：保存当前档案的 API-Key
    private void OnSaveAssistKeyClick(object sender, RoutedEventArgs e)
        => ViewModel.SaveAssistKey(ViewModel.AssistApiKeyInput);

    // ===== 已保存的 API 信息：连通性测试 / 刷新 =====

    // ItemsRepeater 不会像 ListView 那样给模板元素设置 DataContext，需在此显式挂上，
    // 否则按钮的 Click 处理拿不到 ApiProfileRow，表现为“点了没反应”。
    private void OnApiRowPrepared(ItemsRepeater sender, ItemsRepeaterElementPreparedEventArgs args)
    {
        if (args.Element is FrameworkElement element)
        {
            element.DataContext = ViewModel.ApiProfileRows.ElementAtOrDefault(args.Index);
        }
    }

    private async void OnTestApiClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.DataContext is not ApiProfileRow row)
        {
            ViewModel.Status = "未能定位该行 API 配置，请点击「刷新列表」后重试。";
            return;
        }

        // 立即给出可见反馈（按钮文字 + 禁用），避免用户不知道是否正在测试
        btn.IsEnabled = false;
        btn.Content = "测试中…";
        try
        {
            await ViewModel.TestConnectivityAsync(row);
        }
        finally
        {
            btn.Content = "重新测试";
            btn.IsEnabled = true;
        }
    }

    private void OnRefreshApiRowsClick(object sender, RoutedEventArgs e)
    {
        ViewModel.RefreshApiProfileRows();
        ViewModel.Status = "已刷新 API 配置列表。";
    }

    // ===== 内测人员（Edge 扩展） =====

    // x:Bind 可见性函数：按解锁状态切换口令区 / 管理区
    private Visibility TesterLockedVis(bool unlocked) => unlocked ? Visibility.Collapsed : Visibility.Visible;
    private Visibility TesterUnlockedVis(bool unlocked) => unlocked ? Visibility.Visible : Visibility.Collapsed;
    private Visibility EdgeEmptyVis(int count) => count == 0 ? Visibility.Visible : Visibility.Collapsed;

    // 导入扩展文件夹（UWP FolderPicker，与全局其它选择器保持一致）
    private async void OnImportExtensionClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new Windows.Storage.Pickers.FolderPicker();
            picker.FileTypeFilter.Add("*");
            picker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.ComputerFolder;
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow));
            var folder = await picker.PickSingleFolderAsync();
            if (folder is null) return;
            ViewModel.ImportExtension(folder.Path);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "导入 Edge 扩展失败");
            ViewModel.Status = $"导入失败：{ex.Message}";
        }
    }

    // 扩展开关：启用/禁用随 Edge 启动加载
    private void OnExtensionToggle(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleSwitch ts && ts.DataContext is EdgeExtensionRow row)
        {
            ViewModel.SetExtensionEnabled(row, ts.IsOn);
        }
    }

    // 删除扩展
    private void OnRemoveExtensionClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.DataContext is EdgeExtensionRow row)
        {
            ViewModel.RemoveExtension(row);
        }
    }

    // 日志目录：选择 / 恢复默认 / 打开
    private async void OnPickLogDirClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new Windows.Storage.Pickers.FolderPicker();
            picker.FileTypeFilter.Add("*");
            picker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary;
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow));
            var folder = await picker.PickSingleFolderAsync();
            if (folder is null) return;
            ViewModel.SetLogDirectory(folder.Path);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "选择日志目录失败");
        }
    }

    private void OnClearLogDirClick(object sender, RoutedEventArgs e) => ViewModel.ClearLogDirectoryCommand.Execute(null);

    private void OnOpenLogDirClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var dir = ViewModel.LogDirectory;
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{dir}\"",
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "打开日志目录失败");
        }
    }

    // 打开数据目录
    private void OnOpenDataDirClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AI-Tool-Box");
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{dir}\"",
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "打开数据目录失败");
        }
    }
}
