using AI_Tool_Box.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.Web.WebView2.Core;
using Serilog;

namespace AI_Tool_Box.Views;

/// <summary>
/// 极域教室控制页：不再启动/嵌入 JiYuTrainer.exe 窗口，改为纯 C# 功能面板。
/// 全部核心功能（破解密码 / 杀死重启学生端 / 解除控屏 / UDP 攻击）由
/// JiYuCoreService 直接调用（算法移植自 JiYuTrainer 源码）。
/// 背景保留开机界面同款代码雨（Assets/Web/welcome.html?rain=1）。
/// </summary>
public sealed partial class JiYuTrainerPage : Page
{
    private static readonly ILogger Log = Serilog.Log.ForContext<JiYuTrainerPage>();

    // 开机同款代码雨：静态保留 WebView2（页面进出不重初始化，雨效连续）
    // 注意：WinUI 3 WebView2 跨 Page 实例重用时必须每次从旧视觉树父节点断开，
    // 否则 0x800F1000 "没有检测到已安装的组件" 异常会中断整个 UI 线程。
    private static WebView2? _rainWeb;
    private static Task? _rainInitTask;

    public JiYuTrainerPage()
    {
        InitializeComponent();
        Loaded += OnPageLoaded;
        Unloaded += OnPageUnloaded;
    }

    private async void OnPageLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            // 代码雨（页面背景）先行挂载；失败不阻塞功能面板
            try
            {
                EnsureRainHosted();
            }
            catch (Exception rainEx)
            {
                Log.Error(rainEx, "代码雨挂接异常（已 catch，功能面板不受影响）");
            }
            await Task.Delay(200);
            RefreshStatus();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "极域功能面板初始化失败");
        }
    }

    private void OnPageUnloaded(object sender, RoutedEventArgs e)
    {
        // 离开页面：把代码雨 WebView2 从 RainHost 断开（防止下次重绑时 0x800F1000）
        try
        {
            if (_rainWeb is not null && RainHost.Children.Contains(_rainWeb))
            {
                RainHost.Children.Clear();
            }
        }
        catch (Exception ex)
        {
            Log.Verbose(ex, "OnPageUnloaded 断开代码雨 WebView2 异常（可忽略）");
        }
    }

    // ==================== 代码雨（复用开机界面实现） ====================

    private void EnsureRainHosted()
    {
        if (_rainWeb is null)
        {
            _rainWeb = new WebView2 { DefaultBackgroundColor = Microsoft.UI.Colors.Transparent };
        }

        // 先从旧宿主 Grid（可能已销毁的上一页实例）断开再加入新 RainHost，防 COMException 0x800F1000
        try
        {
            var oldParent = VisualTreeHelper.GetParent(_rainWeb);
            if (oldParent is Panel oldPanel && !ReferenceEquals(oldPanel, RainHost))
            {
                oldPanel.Children.Remove(_rainWeb);
                Log.Verbose("代码雨 WebView2 已从旧宿主 Panel 断开");
            }
            else if (oldParent is not null && oldParent is not Panel)
            {
                if (oldParent is Border border) border.Child = null;
                else if (oldParent is ContentControl cc) cc.Content = null;
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "断开代码雨 WebView2 的旧宿主失败，忽略继续");
        }

        try
        {
            if (!RainHost.Children.Contains(_rainWeb))
            {
                RainHost.Children.Clear();
                RainHost.Children.Add(_rainWeb);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "代码雨 WebView2 加入新 RainHost 失败");
        }

        _rainInitTask ??= InitRainAsync();
    }

    private static async Task InitRainAsync()
    {
        try
        {
            // 复用 WebShell 共享 Environment，避免另建环境与用户数据目录冲突
            await _rainWeb!.EnsureCoreWebView2Async(await Controls.WebShell.GetSharedEnvironmentAsync());
            _rainWeb.CoreWebView2.SetVirtualHostNameToFolderMapping(
                "welcome.app",
                Path.Combine(AppContext.BaseDirectory, "Assets", "Web"),
                CoreWebView2HostResourceAccessKind.Allow);
            _rainWeb.CoreWebView2.Navigate("https://welcome.app/welcome.html?rain=1");
            Log.Information("极域教室控制页：开机同款代码雨已加载");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "极域教室控制页代码雨加载失败");
        }
    }

    // ==================== 状态刷新 ====================

    private void RefreshStatus()
    {
        try
        {
            int pid = JiYuCoreService.FindStudentMainPid();
            string? path = JiYuCoreService.LocateStudentMainPath();
            StatusPid.Text = pid > 0 ? $"StudentMain PID：{pid}" : "StudentMain PID：未运行";
            StatusPath.Text = $"学生端路径：{path ?? "未找到"}";
            AppendLog(pid > 0 ? "已检测到极域学生端运行中。" : "未检测到极域学生端（StudentMain.exe 未运行）。");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "刷新极域状态失败");
            AppendLog($"刷新状态失败：{ex.Message}");
        }
    }

    // ==================== 核心操作 ====================

    private void OnBreakPwdClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var pwd = JiYuCoreService.ReadTopDomainPassword(forceKnock: false);
            if (string.IsNullOrEmpty(pwd))
            {
                pwd = JiYuCoreService.ReadTopDomainPassword(forceKnock: true);
            }
            if (!string.IsNullOrEmpty(pwd))
            {
                StatusPwd.Text = $"极域密码：{pwd}";
                AppendLog($"破解成功，极域密码：{pwd}");
            }
            else
            {
                StatusPwd.Text = "极域密码：未找到（本机未安装极域或已卸载）";
                AppendLog("破解失败：本机注册表中未找到 UninstallPasswd / Knock1。");
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "破解极域密码失败");
            AppendLog($"破解密码异常：{ex.Message}");
        }
    }

    private void OnKillClick(object sender, RoutedEventArgs e)
    {
        try
        {
            int pid = JiYuCoreService.FindStudentMainPid();
            if (pid <= 0)
            {
                AppendLog("杀死极域：未找到 StudentMain.exe 进程。");
                return;
            }
            bool ok = JiYuCoreService.KillProcessByPid(pid, force: true);
            AppendLog(ok ? $"已终止极域学生端（PID {pid}）。" : $"终止极域学生端失败（PID {pid}，可能权限不足）。");
            RefreshStatus();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "杀死极域失败");
            AppendLog($"杀死极域异常：{ex.Message}");
        }
    }

    private void OnRerunClick(object sender, RoutedEventArgs e)
    {
        try
        {
            int pid = JiYuCoreService.FindStudentMainPid();
            bool ok = JiYuCoreService.RerunStudentMain(pid);
            AppendLog(ok ? "已重启极域学生端。" : "重启极域失败（未定位到 StudentMain.exe）。");
            RefreshStatus();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "重启极域失败");
            AppendLog($"重启极域异常：{ex.Message}");
        }
    }

    private void OnBreakScreenClick(object sender, RoutedEventArgs e)
    {
        try
        {
            int count = JiYuCoreService.BreakScreenControl();
            AppendLog(count > 0
                ? $"已解除 {count} 个控屏窗口（黑屏/广播/提示）。"
                : "未发现需要解除的控屏窗口（或 StudentMain 未运行）。");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "解除控屏失败");
            AppendLog($"解除控屏异常：{ex.Message}");
        }
    }

    private void OnRefreshClick(object sender, RoutedEventArgs e) => RefreshStatus();

    // ==================== UDP 攻击 ====================

    private void OnUdpCmdClick(object sender, RoutedEventArgs e)
    {
        if (!TryGetUdpTarget(out var ip, out int port)) return;
        JiYuCoreService.SendUdpCommand(ip, port, UdpPayload.Text?.Trim() ?? string.Empty);
        AppendLog($"已发送远程命令到 {ip}:{port}（在目标机上执行 cmd）");
    }

    private void OnUdpMsgClick(object sender, RoutedEventArgs e)
    {
        if (!TryGetUdpTarget(out var ip, out int port)) return;
        JiYuCoreService.SendUdpMessage(ip, port, UdpPayload.Text?.Trim() ?? string.Empty);
        AppendLog($"已发送消息到 {ip}:{port}");
    }

    private void OnUdpShutdownClick(object sender, RoutedEventArgs e)
    {
        if (!TryGetUdpTarget(out var ip, out int port)) return;
        JiYuCoreService.SendUdpShutdown(ip, port);
        AppendLog($"已发送关机指令到 {ip}:{port}");
    }

    private void OnUdpRebootClick(object sender, RoutedEventArgs e)
    {
        if (!TryGetUdpTarget(out var ip, out int port)) return;
        JiYuCoreService.SendUdpReboot(ip, port);
        AppendLog($"已发送重启指令到 {ip}:{port}");
    }

    private bool TryGetUdpTarget(out string ip, out int port)
    {
        ip = UdpIp.Text?.Trim() ?? string.Empty;
        port = 0;
        if (string.IsNullOrEmpty(ip))
        {
            AppendLog("UDP 攻击：请先填写目标 IP。");
            return false;
        }
        if (!int.TryParse(UdpPort.Text?.Trim(), out port) || port <= 0 || port > 65535)
        {
            AppendLog("UDP 攻击：端口无效，请输入 1-65535。");
            return false;
        }
        return true;
    }

    // ==================== 工具 ====================

    private void AppendLog(string text)
    {
        ResultText.Text = $"[{DateTime.Now:HH:mm:ss}] {text}";
    }

    /// <summary>应用退出时调用（MainWindow.Closed）。旧版用于终止嵌入进程，现在仅清理代码雨。</summary>
    public static void ShutdownForAppExit()
    {
        try
        {
            _rainWeb = null;
            _rainInitTask = null;
        }
        catch { /* 忽略 */ }
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        OnPageUnloaded(this, new RoutedEventArgs());
    }
}
