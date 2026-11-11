using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Serilog;

namespace AI_Tool_Box.Services;

/// <summary>
/// 极简 HTTP/1.1 服务器：TcpListener 直连实现（无 ASP.NET 依赖，保持自包含部署）。
/// 借鉴 LocalSend：以 HTTP REST 承载局域网传输协议。
/// 支持流式请求体（/upload 大文件直接消费 Body 流）；每请求一条连接（Connection: close）。
/// </summary>
internal sealed class MiniHttpServer : IDisposable
{
    /// <summary>HTTP 请求上下文。</summary>
    public sealed class HttpRequest
    {
        /// <summary>HTTP 方法（大写）。</summary>
        public required string Method { get; init; }

        /// <summary>请求路径（不含查询串）。</summary>
        public required string Path { get; init; }

        /// <summary>查询参数。</summary>
        public required Dictionary<string, string> Query { get; init; }

        /// <summary>请求头（键保持原大小写）。</summary>
        public required Dictionary<string, string> Headers { get; init; }

        /// <summary>请求体流（当前位置在头部之后；大文件可流式读取）。</summary>
        public required Stream Body { get; init; }

        /// <summary>对端 IP。</summary>
        public required string RemoteIp { get; init; }

        /// <summary>本次连接是否走 TLS（对端开启“加密”时会用 HTTPS 访问同一端口）。</summary>
        public bool IsHttps { get; init; }

        /// <summary>Content-Length（缺失为 0）。</summary>
        public long ContentLength { get; init; }

        /// <summary>读取全部请求体为字节（JSON 等小负载；超限/缺失长度抛 IOException）。</summary>
        public async Task<byte[]> ReadBodyBytesAsync(int limitBytes, CancellationToken ct)
        {
            if (ContentLength <= 0) throw new IOException("缺少有效 Content-Length");
            if (ContentLength > limitBytes) throw new IOException($"请求体超限（{ContentLength} > {limitBytes}）");
            var buf = new byte[ContentLength];
            var done = 0;
            while (done < buf.Length)
            {
                var n = await Body.ReadAsync(buf.AsMemory(done, buf.Length - done), ct);
                if (n == 0) throw new IOException("请求体提前结束");
                done += n;
            }
            return buf;
        }
    }

    /// <summary>HTTP 响应。</summary>
    public sealed class HttpResponse
    {
        /// <summary>状态码。</summary>
        public int StatusCode { get; init; } = 200;

        /// <summary>Content-Type。</summary>
        public string ContentType { get; init; } = "application/json; charset=utf-8";

        /// <summary>响应体字节。</summary>
        public byte[] Body { get; init; } = Array.Empty<byte>();

        /// <summary>JSON 响应。</summary>
        public static HttpResponse Json(int code, string json) =>
            new() { StatusCode = code, Body = Encoding.UTF8.GetBytes(json) };

        /// <summary>纯文本响应。</summary>
        public static HttpResponse Text(int code, string text) =>
            new() { StatusCode = code, Body = Encoding.UTF8.GetBytes(text) };
    }

    private static readonly Dictionary<int, string> ReasonPhrases = new()
    {
        [200] = "OK",
        [400] = "Bad Request",
        [403] = "Forbidden",
        [404] = "Not Found",
        [409] = "Conflict",
        [413] = "Payload Too Large",
        [500] = "Internal Server Error",
    };

    private readonly ILogger _logger;
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;

    /// <summary>请求处理器（每请求一个连接）。</summary>
    public Func<HttpRequest, CancellationToken, Task<HttpResponse>>? Handler { get; set; }

    /// <summary>实际监听端口（未启动为 0）。</summary>
    public int Port { get; private set; }

    /// <summary>初始化。</summary>
    public MiniHttpServer(ILogger logger) => _logger = logger;

    /// <summary>从 preferredPort 起尝试绑定（冲突自动 +1，最多 attempts 个）。</summary>
    public bool Start(int preferredPort, int attempts = 64)
    {
        for (var i = 0; i < attempts; i++)
        {
            var port = preferredPort + i;
            try
            {
                var listener = new TcpListener(IPAddress.Any, port);
                listener.Start(64);
                _listener = listener;
                Port = port;
                _cts = new CancellationTokenSource();
                _ = AcceptLoopAsync(_cts.Token);
                return true;
            }
            catch (SocketException)
            {
                // 端口被占用，尝试下一个
            }
        }
        return false;
    }

    /// <summary>停止监听。</summary>
    public void Stop()
    {
        try { _cts?.Cancel(); } catch { }
        try { _listener?.Stop(); } catch { }
        try { _cts?.Dispose(); } catch { }
        _listener = null;
        _cts = null;
        Port = 0;
    }

    /// <inheritdoc/>
    public void Dispose() => Stop();

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        var listener = _listener!;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(ct);
                _ = Task.Run(() => HandleConnectionAsync(client, ct), CancellationToken.None);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.Warning(ex, "MiniHttpServer AcceptLoop 异常");
        }
    }

    private async Task HandleConnectionAsync(TcpClient client, CancellationToken ct)
    {
        try
        {
            using (client)
            using (var stream = client.GetStream())
            {
                client.NoDelay = true;
                client.ReceiveTimeout = 30_000;

                // 协议自动识别：TLS ClientHello 首字节固定为 0x16，明文 HTTP 首字节是方法名（G/P/D/O/H…）。
                // LocalSend 的“添加收藏”对话框会用对端自身的 https 设置做定向探测，因此同一端口必须同时兼容 HTTP 与 HTTPS。
                var first = new byte[1];
                if (await stream.ReadAsync(first.AsMemory(0, 1), ct) == 0) return;

                Stream io = stream;
                SslStream? ssl = null;
                var isHttps = first[0] == 0x16;
                try
                {
                    if (isHttps)
                    {
                        ssl = new SslStream(new PrefixStream(stream, first));
                        using var handshakeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        handshakeCts.CancelAfter(TimeSpan.FromSeconds(15));
                        await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                        {
                            ServerCertificate = GetServerCertificate(),
                            ClientCertificateRequired = false,
                            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                            CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                        }, handshakeCts.Token);
                        io = ssl;
                        _logger.Debug("MiniHttpServer：来自 {Ip} 的连接已升级为 TLS", client.Client.RemoteEndPoint);
                    }

                    // 请求行
                    var requestLine = await ReadLineAsync(io, ct, isHttps ? null : first[0]);
                    if (string.IsNullOrWhiteSpace(requestLine)) return;
                    var parts = requestLine.Split(' ');
                    if (parts.Length < 3) return;
                    var method = parts[0].ToUpperInvariant();
                    var rawTarget = parts[1];

                    // 头部（最多 100 行）
                    var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    for (var i = 0; i < 100; i++)
                    {
                        var line = await ReadLineAsync(io, ct);
                        if (string.IsNullOrEmpty(line)) break;
                        var idx = line.IndexOf(':');
                        if (idx <= 0) continue;
                        headers[line[..idx].Trim()] = line[(idx + 1)..].Trim();
                    }

                    var contentLength = headers.TryGetValue("Content-Length", out var cl) && long.TryParse(cl, out var len) ? len : 0;

                    // 路径 + 查询串
                    var path = rawTarget;
                    string? query = null;
                    var qIdx = rawTarget.IndexOf('?');
                    if (qIdx >= 0)
                    {
                        path = rawTarget[..qIdx];
                        query = rawTarget[(qIdx + 1)..];
                    }
                    var queryParams = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    if (!string.IsNullOrEmpty(query))
                    {
                        foreach (var kv in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
                        {
                            var eq = kv.IndexOf('=');
                            if (eq <= 0) queryParams[Uri.UnescapeDataString(kv)] = string.Empty;
                            else queryParams[Uri.UnescapeDataString(kv[..eq])] = Uri.UnescapeDataString(kv[(eq + 1)..]);
                        }
                    }

                    var request = new HttpRequest
                    {
                        Method = method,
                        Path = path,
                        Query = queryParams,
                        Headers = headers,
                        Body = io,
                        RemoteIp = ((IPEndPoint)client.Client.RemoteEndPoint!).Address.ToString(),
                        IsHttps = isHttps,
                        ContentLength = contentLength,
                    };

                    HttpResponse response;
                    if (Handler is null)
                    {
                        response = HttpResponse.Text(500, "no handler");
                    }
                    else
                    {
                        try
                        {
                            response = await Handler(request, ct);
                        }
                        catch (OperationCanceledException)
                        {
                            return; // 连接取消，直接断开
                        }
                        catch (Exception ex)
                        {
                            _logger.Warning(ex, "MiniHttpServer 处理请求异常：{Path}", path);
                            response = HttpResponse.Text(500, ex.Message);
                        }
                    }

                    var reason = ReasonPhrases.TryGetValue(response.StatusCode, out var r) ? r : "OK";
                    var headSb = new StringBuilder(256);
                    headSb.Append($"HTTP/1.1 {response.StatusCode} {reason}\r\n");
                    headSb.Append($"Content-Type: {response.ContentType}\r\n");
                    headSb.Append($"Content-Length: {response.Body.Length}\r\n");
                    headSb.Append("Connection: close\r\n\r\n");
                    await io.WriteAsync(Encoding.ASCII.GetBytes(headSb.ToString()), ct);
                    if (response.Body.Length > 0)
                    {
                        await io.WriteAsync(response.Body, ct);
                    }
                    await io.FlushAsync(ct);
                }
                finally
                {
                    if (ssl is not null)
                    {
                        try { await ssl.DisposeAsync(); } catch { }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "MiniHttpServer 连接处理结束（忽略异常）");
        }
    }

    /// <summary>逐字节读一行（避免 StreamReader 预读吃掉请求体字节）。firstByte 为已探测消费的首字节。</summary>
    private static async Task<string> ReadLineAsync(Stream stream, CancellationToken ct, byte? firstByte = null)
    {
        var sb = new StringBuilder(128);
        if (firstByte is { } b && b != (byte)'\n' && b != (byte)'\r') sb.Append((char)b);
        var one = new byte[1];
        while (true)
        {
            var n = await stream.ReadAsync(one.AsMemory(0, 1), ct);
            if (n == 0) break;
            var c = (char)one[0];
            if (c == '\n') break;
            if (c != '\r') sb.Append(c);
            if (sb.Length > 16 * 1024) throw new IOException("HTTP 行过长");
        }
        return sb.ToString();
    }

    /// <summary>懒创建自签名证书（LocalSend 关闭证书校验，任意自签名证书都可用于 HTTPS 定向探测/收藏）。</summary>
    private X509Certificate2 GetServerCertificate()
    {
        var cert = LocalSendCertificate.Get();
        if (!_certificateLogged)
        {
            _certificateLogged = true;
            _logger.Information("MiniHttpServer：已生成自签名 TLS 证书（兼容对端 https 探测与客户端证书要求）");
        }
        return cert;
    }

    private bool _certificateLogged;

    /// <summary>把已探测消费的首字节“回推”到流头部，供 SslStream 完成握手。</summary>
    private sealed class PrefixStream : Stream
    {
        private readonly Stream _inner;
        private readonly byte[] _prefix;
        private int _offset;

        public PrefixStream(Stream inner, byte[] prefix)
        {
            _inner = inner;
            _prefix = prefix;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_offset < _prefix.Length)
            {
                var n = Math.Min(count, _prefix.Length - _offset);
                Array.Copy(_prefix, _offset, buffer, offset, n);
                _offset += n;
                return n;
            }
            return _inner.Read(buffer, offset, count);
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (_offset < _prefix.Length)
            {
                var n = Math.Min(buffer.Length, _prefix.Length - _offset);
                _prefix.AsMemory(_offset, n).CopyTo(buffer);
                _offset += n;
                return new ValueTask<int>(n);
            }
            return _inner.ReadAsync(buffer, ct);
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
            => ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();

        public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct)
            => _inner.WriteAsync(buffer, offset, count, ct);

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
            => _inner.WriteAsync(buffer, ct);

        public override void Flush() => _inner.Flush();
        public override Task FlushAsync(CancellationToken ct) => _inner.FlushAsync(ct);

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}

/// <summary>
/// 本机的 LocalSend 自签名 TLS 证书（进程内共享）。
/// LocalSend 开启“加密”时，服务端会向客户端索取客户端证书（TLS alert 116 = certificate_required），
/// 而它关闭了证书链校验，因此任意自签名证书即可；EKU 同时包含服务器认证与客户端认证。
/// </summary>
internal static class LocalSendCertificate
{
    private static readonly object Gate = new();
    private static X509Certificate2? _certificate;

    /// <summary>获取（懒创建）共享证书。</summary>
    public static X509Certificate2 Get()
    {
        if (_certificate is not null) return _certificate;
        lock (Gate)
        {
            if (_certificate is not null) return _certificate;

            using var rsa = RSA.Create(2048);
            var request = new CertificateRequest("CN=AI Tool Box LocalSend", rsa,
                HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(
                X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, false));
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
                new OidCollection { new("1.3.6.1.5.5.7.3.1"), new("1.3.6.1.5.5.7.3.2") }, false));

            using var created = request.CreateSelfSigned(
                DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(10));
            // 导出再导入，确保私钥可被 SslStream / Schannel 客户端握手使用
            _certificate = new X509Certificate2(created.Export(X509ContentType.Pfx));
            return _certificate;
        }
    }
}
