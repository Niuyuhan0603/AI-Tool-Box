using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using Serilog;

namespace AI_Tool_Box.Services;

/// <summary>本地回环转发代理：系统/浏览器 → 127.0.0.1:Port → 上游加密代理节点。
/// Windows 系统代理（WinINET）无法预置认证凭据（扩展靠 onAuthRequired 钩子补齐，桌面端无此机制），
/// 故由本地回环代理代为携带认证头与上游通信；对系统侧完全透明。</summary>
public sealed class LocalProxyServer : IDisposable
{
    private const int PortStart = 9533;
    private const int UpstreamConnectTimeoutMs = 8000;

    private readonly ILogger _logger;
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;

    /// <summary>本地监听端口（0 = 未启动）。</summary>
    public int Port { get; private set; }

    /// <summary>是否运行中。</summary>
    public bool IsRunning { get; private set; }

    /// <summary>初始化本地转发代理。</summary>
    public LocalProxyServer(ILogger logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>启动监听并开始向上游转发。返回本地端口；端口被占用自动 +1 重试。</summary>
    public int Start(VpnNode node)
    {
        Stop();
        _cts = new CancellationTokenSource();
        for (int i = 0; i < 32; i++)
        {
            var port = PortStart + i;
            try
            {
                _listener = new TcpListener(IPAddress.Loopback, port);
                _listener.Start(64);
                Port = port;
                IsRunning = true;
                _ = AcceptLoopAsync(node, _cts.Token);
                _logger.Information("本地转发代理已启动：127.0.0.1:{Port} → 上游节点:{UpPort}（{Scheme}{Auth}）",
                    port, node.Port, node.Scheme, node.HasAuth ? " + 认证" : string.Empty);
                return port;
            }
            catch (SocketException)
            {
                // 端口占用，尝试下一个
            }
        }
        IsRunning = false;
        throw new InvalidOperationException($"无法找到可用本地端口（{PortStart} 起）");
    }

    /// <summary>停止监听并断开全部转发连接。</summary>
    public void Stop()
    {
        IsRunning = false;
        Port = 0;
        try { _cts?.Cancel(); } catch { }
        try { _listener?.Stop(); } catch { }
        _listener = null;
        _cts = null;
    }

    private async Task AcceptLoopAsync(VpnNode node, CancellationToken ct)
    {
        var listener = _listener!;
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception)
            {
                // listener 已停止或异常，退出循环
                break;
            }
            _ = Task.Run(() => HandleClientAsync(client, node, ct), ct);
        }
    }

    private async Task HandleClientAsync(TcpClient client, VpnNode node, CancellationToken ct)
    {
        try
        {
            using (client)
            {
                using var upstreamCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                upstreamCts.CancelAfter(UpstreamConnectTimeoutMs);
                using var upstream = new TcpClient();
                await upstream.ConnectAsync(node.Host, node.Port, upstreamCts.Token);
                upstreamCts.CancelAfter(Timeout.Infinite);

                var clientStream = client.GetStream();
                Stream upStream = upstream.GetStream();
                SslStream? upTls = null;

                // 上游为 https 代理：先 TLS 再发 CONNECT
                if (string.Equals(node.Scheme, "https", StringComparison.OrdinalIgnoreCase))
                {
                    upTls = new SslStream(upStream, leaveInnerStreamOpen: false, (_, _, _, _) => true);
                    using var tlsCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    tlsCts.CancelAfter(UpstreamConnectTimeoutMs);
                    await upTls.AuthenticateAsClientAsync(node.Host).WaitAsync(tlsCts.Token);
                    upStream = upTls;
                }

                try
                {
                    // 1. 读客户端完整请求头（CONNECT h:p HTTP/1.1 或绝对 URI 请求）
                    var head = await ReadHeadAsync(clientStream, ct);
                    if (head is null || head.Length < 16) return;

                    // 2. 透传请求头并注入 Proxy-Authorization（仅在配置提供认证时；插入到结尾空行之前）
                    var body = head.AsMemory(0, head.Length - 2); // 去掉结尾空行的 \r\n
                    await upStream.WriteAsync(body, ct);
                    if (node.HasAuth)
                    {
                        var basic = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{node.Username}:{node.Password}"));
                        var auth = Encoding.ASCII.GetBytes("Proxy-Authorization: Basic " + basic + "\r\n");
                        await upStream.WriteAsync(auth, ct);
                    }
                    await upStream.WriteAsync(Encoding.ASCII.GetBytes("\r\n"), ct);
                    await upStream.FlushAsync(ct);

                    var isConnect = Encoding.ASCII.GetString(head).StartsWith("CONNECT", StringComparison.OrdinalIgnoreCase);

                    if (isConnect)
                    {
                        // 3a. CONNECT：等上游响应首段（应含 " 200"），原样回传客户端
                        var first = await ReadAvailableAsync(upStream, ct);
                        if (first is null || !Encoding.ASCII.GetString(first).Contains(' '))
                        {
                            await clientStream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 502 Bad Gateway\r\n\r\n"), ct);
                            return;
                        }
                        await clientStream.WriteAsync(first, ct);
                        await clientStream.FlushAsync(ct);
                    }

                    // 4. 双向转发直到任一侧关闭
                    var c2u = PumpAsync(clientStream, upStream, ct);
                    var u2c = PumpAsync(upStream, clientStream, ct);
                    await Task.WhenAny(c2u, u2c);
                }
                finally
                {
                    upTls?.Dispose();
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.Warning(ex, "本地转发代理：处理连接失败（上游 {Host}:{Port}）", node.Host, node.Port);
        }
    }

    /// <summary>读完整请求头（以 \r\n\r\n 结束，含之）。</summary>
    private static async Task<byte[]?> ReadHeadAsync(Stream stream, CancellationToken ct)
    {
        var buf = new byte[8192];
        int total = 0;
        while (total < buf.Length)
        {
            int n;
            try { n = await stream.ReadAsync(buf.AsMemory(total, buf.Length - total), ct); }
            catch (OperationCanceledException) { return null; }
            if (n == 0) return null;
            total += n;
            if (total >= 4)
            {
                for (int i = 0; i <= total - 4; i++)
                {
                    if (buf[i] == '\r' && buf[i + 1] == '\n' && buf[i + 2] == '\r' && buf[i + 3] == '\n')
                    {
                        var head = new byte[i + 4];
                        Array.Copy(buf, head, i + 4);
                        return head;
                    }
                }
            }
        }
        return null;
    }

    /// <summary>读一段可用数据（等待至少 1 字节到达后一次性取出）。</summary>
    private static async Task<byte[]?> ReadAvailableAsync(Stream stream, CancellationToken ct)
    {
        var buf = new byte[8192];
        int n;
        try { n = await stream.ReadAsync(buf.AsMemory(), ct); }
        catch (OperationCanceledException) { return null; }
        if (n <= 0) return null;
        var result = new byte[n];
        Array.Copy(buf, result, n);
        return result;
    }

    /// <summary>单向流转发（12 MB/s 级缓冲，任一端关闭即结束）。</summary>
    private static async Task PumpAsync(Stream from, Stream to, CancellationToken ct)
    {
        var buf = new byte[96 * 1024];
        try
        {
            while (true)
            {
                int n;
                try { n = await from.ReadAsync(buf, ct); }
                catch { break; }
                if (n <= 0) break;
                await to.WriteAsync(buf.AsMemory(0, n), ct);
                await to.FlushAsync(ct);
            }
        }
        catch { }
        finally
        {
            try { to.Close(); } catch { }
            try { from.Close(); } catch { }
        }
    }

    /// <summary>释放资源。</summary>
    public void Dispose() => Stop();
}
