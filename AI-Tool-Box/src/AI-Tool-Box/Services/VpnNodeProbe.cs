using Serilog;
using System.Diagnostics;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;

namespace AI_Tool_Box.Services;

/// <summary>VPN 节点状态（扫描/诊断结果）。</summary>
public enum VpnNodeState
{
    /// <summary>未扫描。</summary>
    Unknown = 0,
    /// <summary>标准 HTTP(S) 代理可用（CONNECT 成功）。</summary>
    ProxyReady = 1,
    /// <summary>在线，但不是标准 HTTP(S) 代理（如专用客户端协议 / 仅状态接口）。</summary>
    NotProxy = 2,
    /// <summary>代理需要认证（HTTP 407）。</summary>
    AuthRequired = 3,
    /// <summary>TCP 不可达或超时。</summary>
    Unreachable = 4,
}

/// <summary>节点探测结果。</summary>
public sealed record VpnProbeResult(VpnNodeState State, int LatencyMs, string Detail)
{
    /// <summary>是否可作为标准代理连接。</summary>
    public bool CanProxy => State == VpnNodeState.ProxyReady;
}

/// <summary>
/// 节点能力探测：TCP 连通 → （可选 TLS）→ 发 CONNECT 到测试目标，按响应判定
/// 标准代理可用 / 需认证 / 非标准代理（专用协议） / 不可达。
/// </summary>
public static class VpnNodeProbe
{
    private const string TargetHost = "www.gstatic.com";
    private const int TargetPort = 443;
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan IoTimeout = TimeSpan.FromSeconds(4);

    /// <summary>探测单个节点（Https 代理先 TLS；http 代理先明文，失败再试 TLS）。</summary>
    public static async Task<VpnProbeResult> ProbeAsync(VpnNode node, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var preferTls = string.Equals(node.Scheme, "https", StringComparison.OrdinalIgnoreCase);

        var first = await AttemptAsync(node, preferTls, ct);
        var result = first;

        // 明文尝试得到"非代理"时，再试一次 TLS（很多节点其实是 TLS 后再 CONNECT）
        if (!preferTls && first.State == VpnNodeState.NotProxy)
        {
            var second = await AttemptAsync(node, true, ct);
            if (second.State is VpnNodeState.ProxyReady or VpnNodeState.AuthRequired) result = second;
            else if (second.State == VpnNodeState.Unreachable) result = first;
            else result = second;
        }

        result = result with { LatencyMs = (int)sw.ElapsedMilliseconds };
        Log.Information("节点探测 {Host}:{Port}（{Scheme}）→ {State}：{Detail}（{Ms} ms）",
            node.Host, node.Port, preferTls ? "https" : "http", result.State, result.Detail, result.LatencyMs);
        return result;
    }

    private static async Task<VpnProbeResult> AttemptAsync(VpnNode node, bool useTls, CancellationToken ct)
    {
        using var tcp = new TcpClient();
        try
        {
            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            connectCts.CancelAfter(ConnectTimeout);
            await tcp.ConnectAsync(node.Host, node.Port, connectCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new VpnProbeResult(VpnNodeState.Unreachable, 0, "TCP 连接超时");
        }
        catch (Exception ex)
        {
            return new VpnProbeResult(VpnNodeState.Unreachable, 0, "TCP 连接失败：" + ex.Message);
        }

        Stream stream = tcp.GetStream();
        SslStream? ssl = null;
        try
        {
            if (useTls)
            {
                ssl = new SslStream(stream, leaveInnerStreamOpen: false, (_, _, _, _) => true);
                using var tlsCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                tlsCts.CancelAfter(IoTimeout);
                await ssl.AuthenticateAsClientAsync(node.Host).WaitAsync(tlsCts.Token);
                stream = ssl;
            }

            var request = new StringBuilder()
                .Append($"CONNECT {TargetHost}:{TargetPort} HTTP/1.1\r\n")
                .Append($"Host: {TargetHost}:{TargetPort}\r\n")
                .Append("Proxy-Connection: keep-alive\r\n");
            if (node.HasAuth)
            {
                var basic = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{node.Username}:{node.Password}"));
                request.Append("Proxy-Authorization: Basic ").Append(basic).Append("\r\n");
            }
            request.Append("\r\n");

            using var ioCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            ioCts.CancelAfter(IoTimeout);
            var bytes = Encoding.ASCII.GetBytes(request.ToString());
            await stream.WriteAsync(bytes, ioCts.Token);
            await stream.FlushAsync(ioCts.Token);

            var buffer = new byte[1024];
            var read = await stream.ReadAsync(buffer.AsMemory(), ioCts.Token);
            if (read <= 0)
                return new VpnProbeResult(VpnNodeState.NotProxy, 0, useTls ? "TLS 连接已关闭（非代理节点）" : "连接已关闭（可能要求 TLS）");

            var text = Encoding.ASCII.GetString(buffer, 0, read);
            var firstLine = text.Split('\n')[0].Trim();
            var code = ParseStatusCode(firstLine);

            if (code == 200) return new VpnProbeResult(VpnNodeState.ProxyReady, 0, "CONNECT 200（标准代理可用）");
            if (code == 407) return new VpnProbeResult(VpnNodeState.AuthRequired, 0, "HTTP 407（需要代理认证）");

            var server = text.Contains("cloudflare", StringComparison.OrdinalIgnoreCase) ? "Cloudflare" : string.Empty;
            var detail = string.IsNullOrEmpty(firstLine) ? "无有效响应" : firstLine;
            if (!string.IsNullOrEmpty(server)) detail += $"（{server}）";
            return new VpnProbeResult(VpnNodeState.NotProxy, 0, detail);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new VpnProbeResult(VpnNodeState.NotProxy, 0, "代理握手超时（非标准代理）");
        }
        catch (Exception ex)
        {
            return new VpnProbeResult(VpnNodeState.NotProxy, 0, "握手失败：" + ex.Message);
        }
        finally
        {
            ssl?.Dispose();
        }
    }

    private static int ParseStatusCode(string statusLine)
    {
        // 形如：HTTP/1.1 200 Connection Established
        var parts = statusLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 && int.TryParse(parts[1], out var code) ? code : 0;
    }
}
