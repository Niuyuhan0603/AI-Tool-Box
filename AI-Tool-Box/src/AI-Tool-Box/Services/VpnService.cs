using System.Net;
using System.Net.Http;
using Microsoft.Win32;
using Serilog;

namespace AI_Tool_Box.Services;

/// <summary>VPN 代理服务：一键把选定加密节点应用为全局网络出口。
/// 实现链路：本机 → 本地回环转发代理（LocalProxyServer，自动携带认证）→ 上游节点 → 目标站点。
/// 连接期间每 2 秒采样网卡字节增量 → UsageTracker（设置页"VPN 流量 ↑↓"展示）。</summary>
public sealed class VpnService : IDisposable
{
    private const string InternetSettingsPath = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";

    private readonly ILogger _logger;
    private readonly IUsageTracker _usage;
    private readonly LocalProxyServer _proxy;
    private readonly IVpnGhAccountClient _ghAccount;
    private readonly IV2rayCoreService _v2ray;
    private readonly System.Timers.Timer _trafficTimer;
    private long _lastSent;
    private long _lastRecv;
    private bool _connected;
    private string _connectedNode = string.Empty;

    public VpnService(ILogger logger, IUsageTracker usage, IVpnGhAccountClient ghAccount, IV2rayCoreService v2ray)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _usage = usage ?? throw new ArgumentNullException(nameof(usage));
        _ghAccount = ghAccount ?? throw new ArgumentNullException(nameof(ghAccount));
        _v2ray = v2ray ?? throw new ArgumentNullException(nameof(v2ray));
        _proxy = new LocalProxyServer(logger);
        // 连接期间流量采样
        _trafficTimer = new System.Timers.Timer(2000) { AutoReset = true };
        _trafficTimer.Elapsed += (_, _) => SampleTraffic();
    }

    /// <summary>当前是否处于已连接状态。</summary>
    public bool IsConnected
    {
        get => _connected;
        private set => _connected = value;
    }

    /// <summary>当前连接的节点描述（host，供 UI 显示）。</summary>
    public string ConnectedNode => _connectedNode;

    /// <summary>读取当前代理生效状态（注册表 ProxyEnable 与 ProxyServer）。</summary>
    public (bool Enabled, string? Server) GetSystemProxyState()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(InternetSettingsPath);
            var enabled = (key?.GetValue("ProxyEnable") as int?) == 1;
            var server = key?.GetValue("ProxyServer") as string;
            return (enabled, server);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "读取代理生效状态失败");
            return (false, null);
        }
    }

    /// <summary>一键连接：按节点协议分流（VMess → v2ray 核心；HTTP(S) → 本地回环转发代理），最后设置系统代理。</summary>
    public async Task ConnectAsync(VpnNode node)
    {
        try
        {
            if (string.Equals(node.Protocol, "vmess", StringComparison.OrdinalIgnoreCase))
            {
                await ConnectVmessAsync(node);
                return;
            }

            var probe = await VpnNodeProbe.ProbeAsync(node);
            switch (probe.State)
            {
                case VpnNodeState.ProxyReady:
                    break;
                case VpnNodeState.AuthRequired:
                    throw new InvalidOperationException(
                        $"节点 {node.Host} 需要代理认证（HTTP 407）。请在 Assets/VPN-config.json 中补充该节点的 proxyUser / proxyPass 后重试。");
                case VpnNodeState.NotProxy:
                    throw new InvalidOperationException(
                        $"节点 {node.Host} 在线，但不是标准 HTTP(S) 代理（{probe.Detail}）。"
                        + "这类节点（如 GreenHub 配置里的节点）只提供官方客户端专用协议/状态接口，"
                        + "无法作为系统代理使用；请改用支持标准 CONNECT 的代理节点，或使用其官方客户端。");
                default:
                    throw new InvalidOperationException($"节点 {node.Host} 不可达（{probe.Detail}）。");
            }

            var localPort = _proxy.Start(node);
            ApplySystemProxy(localPort);
            FinishConnect(node.Host);
            _logger.Information("已一键连接（HTTP 代理）：本机 → 127.0.0.1:{Local} → 节点:{Node}:{Port}", localPort, node.Host, node.Port);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "VPN 一键连接失败：{Host}:{Port}", node.Host, node.Port);
            try { _proxy.Stop(); _v2ray.Stop(); } catch { }
            throw;
        }
    }

    /// <summary>VMess(WS+TLS) 连接：取节点账号 → 启动 v2ray 核心 → 本地 HTTP 入站 → 系统代理。</summary>
    private async Task ConnectVmessAsync(VpnNode node)
    {
        var account = await _ghAccount.GetAccountAsync(node.Host);
        var localPort = await _v2ray.StartAsync(account);
        ApplySystemProxy(localPort);

        if (!await VerifyProxyAsync(localPort))
        {
            _v2ray.Stop();
            RestoreDirect();
            throw new InvalidOperationException(
                $"节点 {node.Host} 账号已获取，但代理链路不通（v2ray 已启动但请求失败）。请换一个节点重试。");
        }

        FinishConnect(node.Host);
        _logger.Information("已一键连接（VMess）：本机 → 127.0.0.1:{Local} → {Address}:{Port}{Path}",
            localPort, account.Address, account.Port, account.Path);
    }

    /// <summary>写入系统代理（WinINET）并刷新。</summary>
    private void ApplySystemProxy(int localPort)
    {
        using var key = Registry.CurrentUser.OpenSubKey(InternetSettingsPath, writable: true)
            ?? throw new InvalidOperationException("无法打开 Internet Settings 注册表项");
        key.SetValue("ProxyEnable", 1, RegistryValueKind.DWord);
        key.SetValue("ProxyServer", $"http=127.0.0.1:{localPort};https=127.0.0.1:{localPort}", RegistryValueKind.String);
        key.SetValue("ProxyOverride", VpnCatalog.ProxyOverride, RegistryValueKind.String);
        NotifyProxyChanged();
    }

    /// <summary>恢复直连（不改动本地转发/核心进程）。</summary>
    private void RestoreDirect()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(InternetSettingsPath, writable: true);
            if (key is not null) key.SetValue("ProxyEnable", 0, RegistryValueKind.DWord);
            NotifyProxyChanged();
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "恢复直连失败");
        }
    }

    /// <summary>标记连接成功并开始流量采样。</summary>
    private void FinishConnect(string nodeHost)
    {
        IsConnected = true;
        _connectedNode = nodeHost;
        StartTrafficSampling();
    }

    /// <summary>经本地代理请求测试地址，验证链路（最多 3 次重试，两个测试目标）。</summary>
    private async Task<bool> VerifyProxyAsync(int localPort)
    {
        var targets = new[] { "https://www.gstatic.com/generate_204", "https://www.bing.com/robots.txt" };
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            foreach (var target in targets)
            {
                try
                {
                    var handler = new SocketsHttpHandler
                    {
                        Proxy = new WebProxy("127.0.0.1", localPort),
                        UseProxy = true,
                        ConnectTimeout = TimeSpan.FromSeconds(8),
                    };
                    using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(12) };
                    using var resp = await client.GetAsync(target);
                    if (resp.IsSuccessStatusCode) return true;
                    _logger.Warning("代理链路验证第 {Attempt} 次返回 HTTP {Status}", attempt, (int)resp.StatusCode);
                }
                catch (Exception ex)
                {
                    _logger.Warning(ex, "代理链路验证第 {Attempt} 次失败（{Target}）", attempt, target);
                }
            }
            await Task.Delay(700);
        }
        return false;
    }

    /// <summary>
    /// 启动时清理残留代理：若系统代理指向本应用的本地回环端口（127.0.0.1:95xx）但转发代理并未运行
    /// （上次异常退出/断电），则恢复直连，避免整个系统的网络请求卡死（表现为双击文件/快捷方式无响应）。
    /// </summary>
    public void CleanupStaleSystemProxy()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(InternetSettingsPath, writable: true);
            if (key is null) return;
            if ((key.GetValue("ProxyEnable") as int?) != 1) return;
            var server = key.GetValue("ProxyServer") as string ?? string.Empty;
            // 本应用的两个本地端口段：9533+（HTTP 转发代理）/ 10810+（v2ray 核心）
            var isOwnLocalProxy = server.Contains("127.0.0.1:953", StringComparison.Ordinal)
                                  || server.Contains("127.0.0.1:108", StringComparison.Ordinal);
            if (!isOwnLocalProxy) return;
            if (_proxy.IsRunning || _v2ray.IsRunning) return;

            key.SetValue("ProxyEnable", 0, RegistryValueKind.DWord);
            NotifyProxyChanged();
            _logger.Information("检测到残留的本地代理设置（{Server}），已自动恢复直连", server);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "清理残留代理设置失败");
        }
    }

    /// <summary>一键断开：恢复系统直连，停止本地转发代理与 v2ray 核心。</summary>
    public void Disconnect()
    {
        try
        {
            RestoreDirect();
            IsConnected = false;
            _connectedNode = string.Empty;
            _proxy.Stop();
            _v2ray.Stop();
            StopTrafficSampling();
            _logger.Information("已断开，恢复系统直连");
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "断开失败");
            throw;
        }
    }

    /// <summary>节点可用性测试：VMess 走账号 API；HTTP(S) 节点走 CONNECT 探测。成功返回耗时毫秒，失败抛出带说明的异常。</summary>
    public async Task<int> TestNodeAsync(VpnNode node, CancellationToken ct = default)
    {
        if (string.Equals(node.Protocol, "vmess", StringComparison.OrdinalIgnoreCase))
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var account = await _ghAccount.GetAccountAsync(node.Host, ct);
            sw.Stop();
            _logger.Information("VMess 节点 {Host} 账号可用 → {Address}:{Port}{Path}，{Ms} ms",
                node.Host, account.Address, account.Port, account.Path, sw.ElapsedMilliseconds);
            return (int)sw.ElapsedMilliseconds;
        }

        var probe = await VpnNodeProbe.ProbeAsync(node, ct);
        switch (probe.State)
        {
            case VpnNodeState.ProxyReady:
                _logger.Information("节点测试 {Host}:{Port} → 可用，{Ms} ms", node.Host, node.Port, probe.LatencyMs);
                return probe.LatencyMs;
            case VpnNodeState.AuthRequired:
                throw new InvalidOperationException($"节点 {node.Host} 需要代理认证（HTTP 407），请在 VPN-config.json 中补充 proxyUser / proxyPass。");
            case VpnNodeState.NotProxy:
                throw new InvalidOperationException(
                    $"节点 {node.Host} 在线，但不是标准 HTTP(S) 代理（{probe.Detail}）："
                    + "这类节点需官方客户端专用协议，无法作为系统代理连接。");
            default:
                throw new InvalidOperationException($"节点 {node.Host} 不可达（{probe.Detail}）。");
        }
    }

    /// <summary>整链路测试：走当前本地代理（HTTP 转发代理或 v2ray 核心）请求 generate_204。</summary>
    public async Task<int> TestConnectionAsync(CancellationToken ct = default)
    {
        var localPort = _proxy.IsRunning ? _proxy.Port : (_v2ray.IsRunning ? _v2ray.LocalPort : 0);
        if (localPort <= 0) throw new InvalidOperationException("本地代理未运行，请先连接。");
        var handler = new SocketsHttpHandler
        {
            Proxy = new WebProxy("127.0.0.1", localPort),
            UseProxy = true,
            ConnectTimeout = TimeSpan.FromSeconds(8)
        };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
        var sw = System.Diagnostics.Stopwatch.StartNew();
        using var resp = await client.GetAsync("https://www.gstatic.com/generate_204", HttpCompletionOption.ResponseHeadersRead, ct);
        sw.Stop();
        _logger.Information("整链路测试 → HTTP {Status}，{Ms} ms", (int)resp.StatusCode, sw.ElapsedMilliseconds);
        return (int)sw.ElapsedMilliseconds;
    }

    // ===== 流量采样 =====
    private void StartTrafficSampling()
    {
        _lastSent = ReadTotalBytes(sent: true);
        _lastRecv = ReadTotalBytes(sent: false);
        _trafficTimer.Start();
    }

    private void StopTrafficSampling() => _trafficTimer.Stop();

    private void SampleTraffic()
    {
        try
        {
            var sent = ReadTotalBytes(sent: true);
            var recv = ReadTotalBytes(sent: false);
            var dSent = Math.Max(0, sent - _lastSent);
            var dRecv = Math.Max(0, recv - _lastRecv);
            _lastSent = sent;
            _lastRecv = recv;
            if (dSent > 0 || dRecv > 0)
            {
                _usage.RecordVpnTraffic(dSent, dRecv);
            }
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "VPN 流量采样失败");
        }
    }

    private static long ReadTotalBytes(bool sent)
    {
        long total = 0;
        foreach (var nic in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up ||
                nic.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback)
            {
                continue;
            }
            var stats = nic.GetIPv4Statistics();
            total += sent ? stats.BytesSent : stats.BytesReceived;
        }
        return total;
    }

    /// <summary>通知代理设置已变更（WinINET 刷新，浏览器即时生效免重启）。</summary>
    private static void NotifyProxyChanged()
    {
        try
        {
            _ = InternetSetOption(IntPtr.Zero, 39, IntPtr.Zero, 0);   // INTERNET_OPTION_SETTINGS_CHANGED
            _ = InternetSetOption(IntPtr.Zero, 37, IntPtr.Zero, 0);   // INTERNET_OPTION_REFRESH
        }
        catch
        {
            // 刷新失败不影响注册表写入，浏览器重启后同样生效
        }
    }

    [System.Runtime.InteropServices.DllImport("wininet.dll", SetLastError = true)]
    private static extern bool InternetSetOption(IntPtr hInternet, int dwOption, IntPtr lpBuffer, int dwBufferLength);

    /// <summary>释放资源（若仍连接则先恢复直连，避免退出后系统代理指向已停止的本地端口）。</summary>
    public void Dispose()
    {
        try
        {
            if (_connected) Disconnect();
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "退出时恢复直连失败");
        }
        _trafficTimer.Dispose();
        _proxy.Dispose();
        _v2ray.Stop();
    }
}
