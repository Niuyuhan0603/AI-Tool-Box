using System.Collections.ObjectModel;
using AI_Tool_Box.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI;
using Microsoft.UI.Xaml.Media;
using Serilog;
using Color = Windows.UI.Color;

namespace AI_Tool_Box.ViewModels;

/// <summary>VPN 节点位置行（UI 绑定）。</summary>
public sealed partial class VpnLocationRow : ObservableObject
{
    public string CityCode { get; init; } = string.Empty;
    public string City { get; init; } = string.Empty;
    public string Country { get; init; } = string.Empty;
    /// <summary>该位置全部 host（ComboBox 绑定，含可用状态）。</summary>
    public ObservableCollection<VpnHostRow> Hosts { get; } = new();

    [ObservableProperty] private VpnHostRow? _selectedRow;

    public string Display => $"{City}, {Country}（{Hosts.Count} 节点）";
}

/// <summary>单个节点行：State 为扫描结果（圆点：青=标准代理可用 / 琥珀=在线但非标准代理 / 白=不可达 / 灰=未扫描）。</summary>
public sealed partial class VpnHostRow : ObservableObject
{
    public string Host { get; init; } = string.Empty;

    /// <summary>节点连接参数（host/port/scheme/认证）。</summary>
    public VpnNode Node { get; init; } = new() { Host = string.Empty };

    /// <summary>扫描状态。</summary>
    [ObservableProperty] private VpnNodeState _state = VpnNodeState.Unknown;

    /// <summary>状态说明（探测详情，供提示显示）。</summary>
    [ObservableProperty] private string _stateDetail = string.Empty;

    /// <summary>状态圆点画刷（选项框与节点清单共用；State 变化时自动刷新）。</summary>
    public Brush StatusBrush => State switch
    {
        VpnNodeState.ProxyReady => new SolidColorBrush(Color.FromArgb(255, 0, 229, 255)),      // 青色：标准代理可用
        VpnNodeState.AuthRequired => new SolidColorBrush(Color.FromArgb(255, 255, 193, 7)),    // 琥珀：需要认证
        VpnNodeState.NotProxy => new SolidColorBrush(Color.FromArgb(255, 255, 145, 0)),        // 橙色：在线但非标准代理
        VpnNodeState.Unreachable => new SolidColorBrush(Colors.White),                        // 白色：不可达
        _ => new SolidColorBrush(Color.FromArgb(255, 150, 150, 150))                          // 灰色：未扫描
    };

    partial void OnStateChanged(VpnNodeState value) => OnPropertyChanged(nameof(StatusBrush));

    public override string ToString() => Host;
}

/// <summary>VPN 页视图模型：节点选择、一键连接/断开、连通性测试、一键扫描所有节点。流量统计由 UsageTracker 记录并在设置页展示。</summary>
public sealed partial class VpnViewModel : ObservableObject
{
    private readonly ILogger _logger;
    private readonly VpnService _vpn;
    private readonly IV2rayCoreService _v2ray;
    private readonly Microsoft.UI.Dispatching.DispatcherQueue? _dispatcher;

    public ObservableCollection<VpnLocationRow> Locations { get; } = new();

    /// <summary>全部节点扁平清单（与各位置 Hosts 共享同一行对象，扫描状态实时联动）。</summary>
    public ObservableCollection<VpnHostRow> AllHosts { get; } = new();

    [ObservableProperty] private VpnLocationRow? _selectedLocation;
    [ObservableProperty] private bool _isBusy;

    /// <summary>空闲状态（操作按钮可用性绑定：空闲时可点，忙时禁用防重复提交）。</summary>
    public bool IsIdle => !IsBusy;

    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(IsIdle));

    [ObservableProperty] private string _status = string.Empty;
    [ObservableProperty] private string _testResult = string.Empty;
    [ObservableProperty] private string _connectionInfo = string.Empty;
    [ObservableProperty] private string _coreInfo = string.Empty;

    /// <summary>连接/断开按钮文字（随连接状态切换）。</summary>
    public string ConnectButtonText => _vpn.IsConnected ? "断开连接" : "一键连接";

    /// <summary>节点清单摘要（位置简写 + 协议/圆点图例）。</summary>
    public string NodeSummary
    {
        get
        {
            var locations = string.Join(" · ", Locations.Select(l => $"{l.CityCode} = {l.City}（{l.Hosts.Count} 节点）"));
            var protocol = VpnCatalog.IsFileConfig ? "VMess(WS+TLS，经 v2ray 核心转发)" : "HTTP(S) CONNECT 代理";
            return locations
                   + $"\n协议：{protocol} · 圆点：青色=可连接（账号/代理可用），橙色=在线但不可用，黄色=需认证，白色=不可达，灰色=未扫描";
        }
    }

    public VpnViewModel(ILogger logger, VpnService vpn, IV2rayCoreService v2ray)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _vpn = vpn ?? throw new ArgumentNullException(nameof(vpn));
        _v2ray = v2ray ?? throw new ArgumentNullException(nameof(v2ray));
        _dispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();

        // 加载内置节点表
        foreach (var loc in VpnCatalog.Locations)
        {
            var row = new VpnLocationRow
            {
                CityCode = loc.CityCode,
                City = loc.City,
                Country = loc.Country
            };
            foreach (var n in loc.Nodes)
            {
                var hostRow = new VpnHostRow { Host = n.Host, Node = n };
                row.Hosts.Add(hostRow);
                AllHosts.Add(hostRow);
            }
            row.SelectedRow = row.Hosts[0];
            Locations.Add(row);
        }
        SelectedLocation = Locations[0];
        RefreshConnectionState();
        RefreshCoreInfo();
    }

    /// <summary>刷新 v2ray 核心状态文本。</summary>
    public void RefreshCoreInfo()
    {
        var core = _v2ray.DetectCore();
        CoreInfo = core is null
            ? "v2ray 核心：未检测到（VMess 节点需要 v2ray.exe；可点击「浏览…」指定，或使用 GreenHub 自带 libs\\v2ray.exe）"
            : $"v2ray 核心：{core}";
    }

    /// <summary>选择自定义 v2ray.exe。</summary>
    [RelayCommand]
    private async Task BrowseCoreAsync()
    {
        try
        {
            var picker = new Windows.Storage.Pickers.FileOpenPicker();
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
            picker.FileTypeFilter.Add(".exe");
            picker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.ComputerFolder;
            var file = await picker.PickSingleFileAsync();
            if (file is null) return;
            _v2ray.SetCorePath(file.Path);
            RefreshCoreInfo();
            Status = $"已指定 v2ray 核心：{file.Path}";
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "选择 v2ray 核心失败");
            Status = $"选择失败：{ex.Message}";
        }
    }

    /// <summary>刷新连接状态显示。</summary>
    public void RefreshConnectionState()
    {
        var (enabled, server) = _vpn.GetSystemProxyState();
        ConnectionInfo = _vpn.IsConnected && enabled
            ? $"已连接：{_vpn.ConnectedNode}（本地回环 {server}）"
            : "未连接（直连）";
        OnPropertyChanged(nameof(ConnectButtonText));
    }

    /// <summary>一键连接：先验证节点可达再应用；再次点击断开。</summary>
    [RelayCommand]
    private async Task ConnectAsync()
    {
        try
        {
            IsBusy = true;
            if (_vpn.IsConnected)
            {
                _vpn.Disconnect();
                Status = "已断开，恢复直连。";
            }
            else
            {
                var node = SelectedLocation?.SelectedRow?.Node;
                if (node is null || string.IsNullOrEmpty(node.Host)) return;
                Status = $"正在连接 {node.Host}（先探测节点是否支持标准代理…）";
                await _vpn.ConnectAsync(node);
                Status = $"已连接 {node.Host}（浏览器即时生效，无需重启）。";
            }
            RefreshConnectionState();
        }
        catch (Exception ex)
        {
            Status = $"操作失败：{ex.Message}";
            RefreshConnectionState();
            _logger.Error(ex, "VPN 一键连接/断开失败");
        }
        finally { IsBusy = false; }
    }

    /// <summary>连通性测试：VMess 节点校验账号 API；HTTP(S) 节点做 CONNECT 探测。</summary>
    [RelayCommand]
    private async Task TestAsync()
    {
        var row = SelectedLocation?.SelectedRow;
        if (row is null) return;
        try
        {
            IsBusy = true;
            TestResult = $"正在探测 {row.Host} …";
            var ms = await _vpn.TestNodeAsync(row.Node);
            row.State = VpnNodeState.ProxyReady;
            row.StateDetail = "可用";
            TestResult = $"✔ {row.Host} 可连接，延迟 {ms} ms";
        }
        catch (Exception ex)
        {
            row.State = VpnNodeState.Unreachable;
            row.StateDetail = ex.Message;
            TestResult = $"✘ {row.Host}：{ex.Message}";
            _logger.Warning("VPN 节点测试失败：{Host}（{Message}）", row.Host, ex.Message);
        }
        finally { IsBusy = false; }
    }

    /// <summary>一键扫描：并发校验每个节点（VMess 走账号 API，HTTP(S) 走 CONNECT 探测）并按结果更新圆点。</summary>
    [RelayCommand]
    private async Task ScanAllAsync()
    {
        var rows = AllHosts.ToList();
        if (rows.Count == 0) return;
        try
        {
            IsBusy = true;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            TestResult = $"正在探测 {rows.Count} 个节点（VMess 账号校验 / HTTP CONNECT 探测）…";

            // VMess 需调用节点账号 API：并发过高会被限流，统一用 5
            using var gate = new SemaphoreSlim(5);
            var tasks = rows.Select(async r =>
            {
                await gate.WaitAsync();
                try
                {
                    var ms = await _vpn.TestNodeAsync(r.Node);
                    _dispatcher?.TryEnqueue(() =>
                    {
                        r.State = VpnNodeState.ProxyReady;
                        r.StateDetail = $"可用（{ms} ms）";
                    });
                }
                catch (Exception ex)
                {
                    var detail = ex.Message;
                    _logger.Warning("节点探测失败：{Host}（{Message}）", r.Host, detail);
                    _dispatcher?.TryEnqueue(() =>
                    {
                        r.State = VpnNodeState.Unreachable;
                        r.StateDetail = detail;
                    });
                }
                finally { gate.Release(); }
            });
            await Task.WhenAll(tasks);

            // 等待 UI 队列把最后一次状态刷新完
            await Task.Delay(120);

            var ok = rows.Count(r => r.State == VpnNodeState.ProxyReady);
            var down = rows.Count - ok;
            sw.Stop();

            TestResult = $"扫描完成（{sw.Elapsed.TotalSeconds:0.0}s）：可连接 {ok} · 不可用 {down}。";
            if (ok == 0 && rows.Count > 0)
            {
                TestResult += "\n提示：所有节点账号校验均失败，可能是 uid/版本与服务器不匹配（检查 VPN-config.json 的 uid、cur-version），"
                              + "或当前网络无法访问节点接口。";
            }
            _logger.Information("VPN 节点扫描完成：ok={Ok} down={Down}", ok, down);
        }
        catch (Exception ex)
        {
            TestResult = $"扫描失败：{ex.Message}";
            _logger.Error(ex, "VPN 节点批量扫描失败");
        }
        finally { IsBusy = false; }
    }
}
