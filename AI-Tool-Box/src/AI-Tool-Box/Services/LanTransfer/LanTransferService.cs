using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Net;
using System.Net.Http.Headers;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Microsoft.UI.Dispatching;
using Serilog;

namespace AI_Tool_Box.Services;

/// <summary>
/// 局域网传输服务（借鉴 LocalSend 协议思路）：
/// - 发现：UDP 多播 224.0.0.167:53317，announce 宣告 + HTTP register 回应，指纹去重；
/// - 传输：HTTP REST 会话式（/api/localsend/v2/register、prepare-upload、upload、cancel），
///   prepare-upload 响应 sessionId + 逐文件 token（或 declined/busy），upload 凭 token 流式上传；
/// - 安全：设备指纹识别自己；接收端手动确认或自动接受；文件名净化 + 重名去重 + 大小/SHA256 校验。
/// </summary>
public sealed class LanTransferService : ILanTransferService
{
    #region ===== Constants =====
    private const string MulticastGroup = "224.0.0.167";
    private const int DefaultPort = 53317;
    private const int AnnouncePeriodMs = 5000;
    private const int PeerTimeoutSeconds = 15;
    private const int RequestTimeoutSeconds = 60;      // 接收端等待用户确认
    private const int PrepareTimeoutSeconds = 65;      // 发送端征询超时（> 接收端等待时间）
    private const long HashThresholdBytes = 64 * 1024 * 1024; // 超过 64MB 不计算 SHA256
    private const int JsonBodyLimit = 16 * 1024 * 1024;
    private const int BufferSize = 64 * 1024;
    private static readonly int[] AnnounceBurstDelaysMs = { 100, 500, 2000 };
    #endregion

    private readonly ILogger _logger;
    private readonly DispatcherQueue _dispatcher;
    private readonly MiniHttpServer _http;
    private readonly HttpClient _controlHttp;   // register / prepare / cancel（有整体超时）
    private readonly HttpClient _uploadHttp;    // 上传流（无整体超时）

    private UdpClient? _udp;
    private CancellationTokenSource? _cts;
    private Task? _udpReceiveTask;
    private Task? _announceTask;
    private Task? _cleanupTask;

    // 会话与设备
    private readonly ConcurrentDictionary<string, ReceiverSession> _sessions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, LanPeer> _peerByKey = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DateTime> _lastReplyUtc = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _uiPeersLock = new();
    private volatile LanIncomingRequest? _pendingRequest;

    // 设置持久化
    private readonly string _dataDir;
    private readonly string _settingsPath;
    private LanTransferSettings _settings;

    /// <inheritdoc/>
    public int LocalPort => _http.Port;

    /// <inheritdoc/>
    public string LocalIpAddress { get; }

    /// <inheritdoc/>
    public string Alias
    {
        get => _settings.Alias;
        set
        {
            var v = string.IsNullOrWhiteSpace(value) ? Environment.MachineName : value.Trim();
            if (string.Equals(_settings.Alias, v, StringComparison.Ordinal)) return;
            _settings.Alias = v;
            SaveSettings();
        }
    }

    /// <inheritdoc/>
    public string Fingerprint { get; }

    /// <inheritdoc/>
    public bool IsListening { get; private set; }

    /// <inheritdoc/>
    public bool AutoAccept
    {
        get => _settings.AutoAccept;
        set
        {
            if (_settings.AutoAccept == value) return;
            _settings.AutoAccept = value;
            SaveSettings();
        }
    }

    /// <inheritdoc/>
    public string SaveDirectory
    {
        get => _settings.SaveDir;
        set
        {
            var v = string.IsNullOrWhiteSpace(value)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "AI-Tool-Box")
                : value.Trim();
            if (!Directory.Exists(v)) Directory.CreateDirectory(v);
            _settings.SaveDir = v;
            SaveSettings();
        }
    }

    /// <inheritdoc/>
    public ObservableCollection<LanPeer> OnlinePeers { get; } = new();

    /// <inheritdoc/>
    public ObservableCollection<TransferProgress> Transfers { get; } = new();

    /// <inheritdoc/>
    public event Action<LanIncomingRequest>? IncomingTransferRequested;

    /// <summary>初始化（不占端口；Start 后启用 UDP/HTTP）。</summary>
    public LanTransferService(ILogger logger, DispatcherQueue dispatcher)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _http = new MiniHttpServer(logger);
        // 局域网传输必须绕过系统代理（VPN 模块会写入系统代理，代理无法转发私网地址），
        // 且对端 LocalSend 若开启「加密」会使用自签名证书，这里统一放行。
        _controlHttp = CreateLanHttpClient(TimeSpan.FromSeconds(PrepareTimeoutSeconds));
        _uploadHttp = CreateLanHttpClient(Timeout.InfiniteTimeSpan);

        _dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AI-Tool-Box");
        Directory.CreateDirectory(_dataDir);
        _settingsPath = Path.Combine(_dataDir, "transfer_settings.json");
        _settings = LoadSettings();
        Fingerprint = LoadOrCreateFingerprint();

        LocalIpAddress = GetLocalIPv4();
        try
        {
            if (!Directory.Exists(_settings.SaveDir)) Directory.CreateDirectory(_settings.SaveDir);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "创建默认保存目录失败：{Dir}", _settings.SaveDir);
        }
    }

    #region ===== Start / Stop =====
    /// <inheritdoc/>
    public bool Start()
    {
        if (IsListening) return true;
        try
        {
            _cts = new CancellationTokenSource();
            _http.Handler = HandleHttpRequestAsync;
            if (!_http.Start(DefaultPort))
            {
                _logger.Error("局域网传输：无法绑定 HTTP 端口（起始 {Port}）", DefaultPort);
                Stop();
                return false;
            }

            StartUdp(_cts.Token); // UDP 失败仅降级（不阻断 HTTP 传输）
            _announceTask = AnnounceLoopAsync(_cts.Token);
            _cleanupTask = CleanupLoopAsync(_cts.Token);
            IsListening = true;
            _logger.Information("局域网传输已启动：HTTP:{Http} / UDP 多播 {Group}:{Udp}，本机 {Alias}@{Ip}（指纹 {Fp8}…）",
                _http.Port, MulticastGroup, DefaultPort, Alias, LocalIpAddress, Fingerprint[..8]);
            return true;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "局域网传输启动失败");
            Stop();
            return false;
        }
    }

    /// <inheritdoc/>
    public void Stop()
    {
        IsListening = false;
        try { _cts?.Cancel(); } catch { }
        try { _udp?.Close(); } catch { }
        try { _udp?.Dispose(); } catch { }
        try { _http.Stop(); } catch { }
        foreach (var session in _sessions.Values)
        {
            try { session.Cts.Cancel(); } catch { }
        }
        _sessions.Clear();
        _peerByKey.Clear();
        _udp = null;
        _cts = null;
        _dispatcher.TryEnqueue(() =>
        {
            lock (_uiPeersLock) OnlinePeers.Clear();
        });
        _logger.Information("局域网传输已停止");
    }

    /// <inheritdoc/>
    public void Dispose() => Stop();
    #endregion

    #region ===== HTTP 路由 =====
    private async Task<MiniHttpServer.HttpResponse> HandleHttpRequestAsync(MiniHttpServer.HttpRequest req, CancellationToken ct)
    {
        switch (req.Path)
        {
            // v1 与 v2 的 info/register 语义相同；LocalSend 收藏夹探测固定走 v1（peerProtocolVersion=1.0）
            case "/api/localsend/v1/info" when req.Method == "GET":
            case "/api/localsend/v2/info" when req.Method == "GET":
                _logger.Debug("info 探测：{Ip} 通过 {Scheme} 访问 {Path}", req.RemoteIp, req.IsHttps ? "HTTPS" : "HTTP", req.Path);
                return MiniHttpServer.HttpResponse.Json(200, JsonSerializer.Serialize(DeviceInfo(), LanJson.Options));

            case "/api/localsend/v1/register" when req.Method == "POST":
            case "/api/localsend/v2/register" when req.Method == "POST":
                return await HandleRegisterAsync(req, ct);

            case "/api/localsend/v1/send-request" when req.Method == "POST":
            case "/api/localsend/v2/prepare-upload" when req.Method == "POST":
                return await HandlePrepareUploadAsync(req, ct);

            case "/api/localsend/v1/send" when req.Method == "POST":
            case "/api/localsend/v2/upload" when req.Method == "POST":
                return await HandleUploadAsync(req, ct);

            case "/api/localsend/v1/cancel" when req.Method == "POST":
            case "/api/localsend/v2/cancel" when req.Method == "POST":
                return HandleCancelAsync(req);

            default:
                return MiniHttpServer.HttpResponse.Text(404, "not found");
        }
    }

    private async Task<MiniHttpServer.HttpResponse> HandleRegisterAsync(MiniHttpServer.HttpRequest req, CancellationToken ct)
    {
        try
        {
            var body = await req.ReadBodyBytesAsync(JsonBodyLimit, ct);
            var dto = JsonSerializer.Deserialize<DeviceInfoDto>(body, LanJson.Options);
            if (dto is null) return MiniHttpServer.HttpResponse.Text(400, "invalid json");
            UpsertPeer(req.RemoteIp, dto.Port ?? DefaultPort, dto.Alias, dto.Fingerprint, dto.DeviceType,
                dto.Version, string.Equals(dto.Protocol, "https", StringComparison.OrdinalIgnoreCase));
            return MiniHttpServer.HttpResponse.Json(200, JsonSerializer.Serialize(DeviceInfo(), LanJson.Options));
        }
        catch (Exception ex)
        {
            return MiniHttpServer.HttpResponse.Text(400, ex.Message);
        }
    }

    private async Task<MiniHttpServer.HttpResponse> HandlePrepareUploadAsync(MiniHttpServer.HttpRequest req, CancellationToken ct)
    {
        byte[] body;
        try
        {
            body = await req.ReadBodyBytesAsync(JsonBodyLimit, ct);
        }
        catch (IOException ex)
        {
            return MiniHttpServer.HttpResponse.Text(413, ex.Message);
        }

        PrepareUploadRequestDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<PrepareUploadRequestDto>(body, LanJson.Options);
        }
        catch (JsonException)
        {
            return MiniHttpServer.HttpResponse.Text(400, "invalid json");
        }
        if (dto?.Files is null || dto.Files.Count == 0) return MiniHttpServer.HttpResponse.Text(400, "no files");

        var requesterDisplay = $"{(string.IsNullOrWhiteSpace(dto.Info?.Alias) ? "未知设备" : dto.Info!.Alias)}（{req.RemoteIp}）";
        var fileIds = dto.Files.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();
        var v1 = req.Path.Contains("/v1/", StringComparison.Ordinal);

        // 一次只允许一个待确认请求（忙时拒绝，语义对齐 LocalSend busy）
        if (!IsListening || _pendingRequest is not null)
        {
            var busyMap = fileIds.ToDictionary(id => id, _ => "busy");
            return v1
                ? MiniHttpServer.HttpResponse.Json(200, JsonSerializer.Serialize(busyMap, LanJson.Options))
                : MiniHttpServer.HttpResponse.Json(200,
                    JsonSerializer.Serialize(new PrepareUploadResponseDto { Files = busyMap }, LanJson.Options));
        }

        var sessionId = Guid.NewGuid().ToString("N");
        var token = Guid.NewGuid().ToString("N");

        var response = new PrepareUploadResponseDto { SessionId = sessionId };

        if (!AutoAccept)
        {
            var request = new LanIncomingRequest
            {
                SessionId = sessionId,
                RequesterDisplay = requesterDisplay,
                RequesterIp = req.RemoteIp,
                Files = dto.Files.Values
                    .OrderBy(f => f.Id, StringComparer.Ordinal)
                    .Select(f => new LanIncomingFile(f.Id, f.FileName, f.Size))
                    .ToList(),
            };
            _pendingRequest = request;
            var accepted = false;
            try
            {
                _logger.Information("收到传输请求：{Peer}，{Count} 个文件（共 {Size}）",
                    requesterDisplay, request.Files.Count, TransferProgress.FormatSize(request.Files.Sum(f => f.Size)));
                IncomingTransferRequested?.Invoke(request);
                accepted = await request.WaitResponseAsync(TimeSpan.FromSeconds(RequestTimeoutSeconds));
            }
            catch (TimeoutException)
            {
                accepted = false;
            }
            finally
            {
                _pendingRequest = null;
            }

            if (!accepted)
            {
                _logger.Information("已拒绝传输请求：{Peer}", requesterDisplay);
                foreach (var id in fileIds) response.Files[id] = "declined";
                return v1
                    ? MiniHttpServer.HttpResponse.Json(200, JsonSerializer.Serialize(response.Files, LanJson.Options))
                    : MiniHttpServer.HttpResponse.Json(200, JsonSerializer.Serialize(response, LanJson.Options));
            }
        }

        // 建会话：逐文件建进度条目
        var session = new ReceiverSession(sessionId, token, requesterDisplay);
        var progressList = new List<TransferProgress>();
        foreach (var id in fileIds)
        {
            var meta = dto.Files[id];
            var progress = new TransferProgress
            {
                IsSend = false,
                PeerDisplay = requesterDisplay,
                FileName = SanitizeFileName(meta.FileName),
            };
            progress.TotalBytes = meta.Size;
            progress.Status = "等待接收…";
            session.Files[id] = new ReceiverFile { Meta = meta, Progress = progress };
            response.Files[id] = token;
            progressList.Add(progress);
        }
        _sessions[sessionId] = session;
        _dispatcher.TryEnqueue(() =>
        {
            foreach (var p in progressList) Transfers.Add(p);
        });
        _logger.Information("接受传输会话 {Session}：{Peer}，{Count} 个文件", sessionId, requesterDisplay, fileIds.Count);
        return v1
            ? MiniHttpServer.HttpResponse.Json(200, JsonSerializer.Serialize(response.Files, LanJson.Options))
            : MiniHttpServer.HttpResponse.Json(200, JsonSerializer.Serialize(response, LanJson.Options));
    }

    private async Task<MiniHttpServer.HttpResponse> HandleUploadAsync(MiniHttpServer.HttpRequest req, CancellationToken ct)
    {
        var fileId = req.Query.GetValueOrDefault("fileId") ?? string.Empty;
        var uploadToken = req.Query.GetValueOrDefault("token");
        var sessionIdQuery = req.Query.GetValueOrDefault("sessionId");

        ReceiverSession? session = null;
        if (!string.IsNullOrWhiteSpace(sessionIdQuery))
        {
            _sessions.TryGetValue(sessionIdQuery, out session);
        }
        // v1 上传不带 sessionId：按 fileId + token 反查会话
        if (session is null)
        {
            session = _sessions.Values.FirstOrDefault(s =>
                s.Files.ContainsKey(fileId) && string.Equals(s.Token, uploadToken, StringComparison.Ordinal));
        }
        if (session is null) return MiniHttpServer.HttpResponse.Text(404, "unknown session");
        if (!session.Files.TryGetValue(fileId, out var file))
            return MiniHttpServer.HttpResponse.Text(404, "unknown file");
        if (!string.Equals(session.Token, uploadToken, StringComparison.Ordinal))
            return MiniHttpServer.HttpResponse.Text(403, "bad token");
        if (session.Cts.IsCancellationRequested)
            return MiniHttpServer.HttpResponse.Text(409, "canceled");

        var token = session.Cts.Token;
        var progress = file.Progress;
        var savePath = GetUniqueSavePath(SanitizeFileName(file.Meta.FileName));
        file.SavePath = savePath;
        progress.Status = $"接收中：{file.Meta.FileName}";

        var declared = file.Meta.Size;
        try
        {
            long written = 0;
            await using (var fs = new FileStream(savePath, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize, true))
            {
                var buf = new byte[BufferSize];
                while (written < declared)
                {
                    var n = await req.Body.ReadAsync(buf.AsMemory(0, (int)Math.Min(buf.Length, declared - written)), token);
                    if (n == 0) break;
                    await fs.WriteAsync(buf.AsMemory(0, n), token);
                    written += n;
                    progress.ReportProgress(written);
                }
            }

            if (session.Cts.IsCancellationRequested) throw new OperationCanceledException();

            if (written != declared)
            {
                TryDelete(savePath);
                progress.Status = $"接收失败：数据不完整（{written}/{declared} 字节）";
                progress.IsCompleted = true;
                return MiniHttpServer.HttpResponse.Text(400, "incomplete body");
            }

            if (!string.IsNullOrEmpty(file.Meta.Hash))
            {
                progress.Status = "校验 SHA256…";
                var actual = await ComputeSha256Async(savePath, CancellationToken.None);
                progress.Sha256Result = string.Equals(actual, file.Meta.Hash, StringComparison.OrdinalIgnoreCase)
                    ? $"SHA256 OK ✓（{actual[..12]}…）"
                    : $"SHA256 校验失败 ✗（实际 {actual[..12]}… / 期望 {SafeSub(file.Meta.Hash, 12)}…）";
            }
            else
            {
                progress.Sha256Result = "未提供哈希（大文件跳过校验）";
            }

            progress.Status = $"已保存：{savePath}";
            progress.IsCompleted = true;
            _logger.Information("接收完成：{File}（{Size}）来自 {Peer}", savePath, TransferProgress.FormatSize(declared), session.PeerDisplay);
            return MiniHttpServer.HttpResponse.Json(200, "{}");
        }
        catch (OperationCanceledException)
        {
            TryDelete(savePath);
            progress.Status = "已取消";
            progress.IsCompleted = true;
            return MiniHttpServer.HttpResponse.Text(409, "canceled");
        }
        catch (Exception ex)
        {
            TryDelete(savePath);
            progress.Status = $"接收失败：{ex.Message}";
            progress.IsCompleted = true;
            _logger.Warning(ex, "接收失败：{Peer}", session.PeerDisplay);
            return MiniHttpServer.HttpResponse.Text(500, ex.Message);
        }
    }

    private MiniHttpServer.HttpResponse HandleCancelAsync(MiniHttpServer.HttpRequest req)
    {
        if (_sessions.TryGetValue(req.Query.GetValueOrDefault("sessionId") ?? string.Empty, out var session))
        {
            try { session.Cts.Cancel(); } catch { }
            var canceled = session.Files.Values
                .Where(f => !f.Progress.IsCompleted)
                .Select(f => f.Progress)
                .ToList();
            _dispatcher.TryEnqueue(() =>
            {
                foreach (var p in canceled)
                {
                    p.Status = "已取消";
                    p.IsCompleted = true;
                }
            });
            _logger.Information("发送方取消会话：{Session}", session.SessionId);
        }
        return MiniHttpServer.HttpResponse.Json(200, "{}");
    }
    #endregion

    #region ===== UDP 多播发现 =====
    private void StartUdp(CancellationToken ct)
    {
        try
        {
            var udp = new UdpClient();
            udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            udp.Client.Bind(new IPEndPoint(IPAddress.Any, DefaultPort));
            udp.JoinMulticastGroup(IPAddress.Parse(MulticastGroup));
            _udp = udp;

            _udpReceiveTask = Task.Run(async () =>
            {
                try
                {
                    while (!ct.IsCancellationRequested)
                    {
                        var result = await udp.ReceiveAsync(ct);
                        HandleMulticastPacket(result.Buffer, result.RemoteEndPoint.Address.ToString());
                    }
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    _logger.Warning(ex, "UDP 多播接收异常");
                }
            }, ct);

            _logger.Information("UDP 多播监听已启动（组 {Group}:{Port}）", MulticastGroup, DefaultPort);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "UDP 多播监听启动失败（发现功能降级；请检查防火墙）");
            try { _udp?.Dispose(); } catch { }
            _udp = null;
        }
    }

    /// <inheritdoc/>
    public void AnnounceNow() => SendMulticastAnnounce(announce: true);

    private async Task AnnounceLoopAsync(CancellationToken ct)
    {
        try
        {
            // 启动爆发：100/500/2000ms 各一次（对齐 LocalSend）
            foreach (var delay in AnnounceBurstDelaysMs)
            {
                await Task.Delay(delay, ct);
                SendMulticastAnnounce(announce: true);
            }
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(AnnouncePeriodMs));
            while (!ct.IsCancellationRequested && await timer.WaitForNextTickAsync(ct))
            {
                SendMulticastAnnounce(announce: true);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.Warning(ex, "announce 循环异常");
        }
    }

    private void SendMulticastAnnounce(bool announce)
    {
        var udp = _udp;
        if (udp is null || !IsListening) return;
        try
        {
            var dto = new MulticastDto
            {
                Alias = Alias,
                Version = "2.1",
                DeviceModel = "Windows",
                DeviceType = "desktop",
                Fingerprint = Fingerprint,
                Port = _http.Port,
                Protocol = "http",
                Download = false,
                Announce = announce,
                Announcement = announce,
            };
            var bytes = JsonSerializer.SerializeToUtf8Bytes(dto, LanJson.Options);
            udp.Send(bytes, bytes.Length, new IPEndPoint(IPAddress.Parse(MulticastGroup), DefaultPort));
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "发送多播消息失败");
        }
    }

    private void HandleMulticastPacket(byte[] bytes, string fromIp)
    {
        if (string.Equals(fromIp, LocalIpAddress, StringComparison.OrdinalIgnoreCase)) return;
        try
        {
            var dto = JsonSerializer.Deserialize<MulticastDto>(bytes, LanJson.Options);
            if (dto is null) return;
            if (string.Equals(dto.Fingerprint, Fingerprint, StringComparison.OrdinalIgnoreCase)) return; // 自己

            var port = dto.Port ?? DefaultPort;
            var https = string.Equals(dto.Protocol, "https", StringComparison.OrdinalIgnoreCase);
            UpsertPeer(fromIp, port, dto.Alias, dto.Fingerprint, dto.DeviceType, dto.Version, https);

            // 对方在宣告：按 LocalSend 语义用 HTTP(S) register 回应（失败回退 UDP 回复）
            if (dto.Announce == true && IsListening)
            {
                var last = _lastReplyUtc.GetOrAdd(fromIp, DateTime.MinValue);
                if ((DateTime.UtcNow - last).TotalMilliseconds > 1500)
                {
                    _lastReplyUtc[fromIp] = DateTime.UtcNow;
                    _ = ReplyRegisterAsync(fromIp, port, https);
                }
            }
        }
        catch (JsonException)
        {
            // 忽略非协议广播
        }
    }

    private async Task ReplyRegisterAsync(string ip, int port, bool https)
    {
        try
        {
            var json = JsonSerializer.Serialize(DeviceInfo(), LanJson.Options);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var resp = await _controlHttp.PostAsync($"{Scheme(https)}://{ip}:{port}/api/localsend/v2/register", content);
            var body = await resp.Content.ReadAsStringAsync();
            var info = JsonSerializer.Deserialize<DeviceInfoDto>(body, LanJson.Options);
            if (info is not null)
            {
                UpsertPeer(ip, info.Port ?? port, info.Alias, info.Fingerprint, info.DeviceType,
                    info.Version, string.Equals(info.Protocol, "https", StringComparison.OrdinalIgnoreCase));
            }
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "回应 register 失败（回退 UDP）：{Ip}:{Port}", ip, port);
            // 回退：UDP 多播回复（announce=false，仅为被发现）
            SendMulticastAnnounce(announce: false);
        }
    }
    #endregion

    #region ===== 设备表维护 =====
    private void UpsertPeer(string ip, int port, string? alias, string? fingerprint, string? deviceType,
        string? version = null, bool https = false)
    {
        if (string.Equals(ip, LocalIpAddress, StringComparison.OrdinalIgnoreCase)) return;
        var key = !string.IsNullOrEmpty(fingerprint) ? $"fp:{fingerprint}" : $"ip:{ip}:{port}";
        var now = DateTime.UtcNow;

        if (_peerByKey.TryGetValue(key, out var existing))
        {
            existing.LastSeenUtc = now;
            var changed = !string.Equals(existing.Alias, alias, StringComparison.Ordinal)
                          || existing.Port != port
                          || existing.Https != https
                          || (!string.IsNullOrWhiteSpace(version) && existing.Version != version);
            if (changed)
            {
                var updated = new LanPeer(alias ?? ip, ip, port, fingerprint ?? string.Empty,
                    deviceType ?? "desktop", version ?? existing.Version, https) { LastSeenUtc = now };
                _peerByKey[key] = updated;
                _dispatcher.TryEnqueue(() =>
                {
                    lock (_uiPeersLock)
                    {
                        var old = OnlinePeers.FirstOrDefault(p => p.Equals(existing));
                        if (old is not null) OnlinePeers.Remove(old);
                        if (!OnlinePeers.Contains(updated)) OnlinePeers.Add(updated);
                    }
                });
            }
            return;
        }

        var peer = new LanPeer(alias ?? ip, ip, port, fingerprint ?? string.Empty,
            deviceType ?? "desktop", string.IsNullOrEmpty(version) ? "2.1" : version, https) { LastSeenUtc = now };
        _peerByKey[key] = peer;
        _dispatcher.TryEnqueue(() =>
        {
            lock (_uiPeersLock)
            {
                if (!OnlinePeers.Contains(peer)) OnlinePeers.Add(peer);
            }
            _logger.Information("发现设备：{Peer}（协议 {Version}{Scheme}）", peer.Display, peer.Version,
                peer.Https ? "/HTTPS" : string.Empty);
        });
    }

    private async Task CleanupLoopAsync(CancellationToken ct)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(3));
            while (!ct.IsCancellationRequested && await timer.WaitForNextTickAsync(ct))
            {
                var cutoff = DateTime.UtcNow.AddSeconds(-PeerTimeoutSeconds);
                var expired = _peerByKey.Where(kv => kv.Value.LastSeenUtc < cutoff).Select(kv => kv.Key).ToList();
                if (expired.Count > 0)
                {
                    var removedPeers = new List<LanPeer>();
                    foreach (var key in expired)
                    {
                        if (_peerByKey.TryRemove(key, out var p)) removedPeers.Add(p);
                    }
                    _dispatcher.TryEnqueue(() =>
                    {
                        lock (_uiPeersLock)
                        {
                            foreach (var p in removedPeers)
                            {
                                var match = OnlinePeers.FirstOrDefault(x => x.Equals(p));
                                if (match is not null) OnlinePeers.Remove(match);
                            }
                        }
                    });
                }

                // 清理过期会话（15 分钟）
                var sessionCutoff = DateTime.UtcNow.AddMinutes(-15);
                foreach (var kv in _sessions.Where(kv => kv.Value.CreatedUtc < sessionCutoff).ToList())
                {
                    if (_sessions.TryRemove(kv.Key, out var session))
                    {
                        try { session.Cts.Cancel(); } catch { }
                        try { session.Cts.Dispose(); } catch { }
                    }
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.Warning(ex, "设备/会话清理循环异常");
        }
    }
    #endregion

    #region ===== 发送 =====
    /// <inheritdoc/>
    public async Task SendFilesAsync(LanPeer target, IReadOnlyList<string> filePaths)
    {
        if (target is null) throw new ArgumentNullException(nameof(target));
        if (filePaths is null || filePaths.Count == 0) throw new ArgumentException("未选择文件", nameof(filePaths));

        var files = filePaths.Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (files.Count == 0) throw new FileNotFoundException("所选文件均不存在");

        // 1) 征询前：先建进度条目 + 计算小文件哈希
        var metas = new Dictionary<string, FileMetaDto>();
        var hashById = new Dictionary<string, string>();
        var ordered = new List<(string Id, string Path, TransferProgress Progress)>();
        var addList = new List<TransferProgress>();
        var idx = 0;
        foreach (var path in files)
        {
            var fi = new FileInfo(path);
            var id = $"file_{idx++}";
            var progress = new TransferProgress { IsSend = true, PeerDisplay = target.Display, FileName = fi.Name };
            progress.TotalBytes = fi.Length;
            progress.Status = "征询对方接收…";
            var meta = new FileMetaDto
            {
                Id = id,
                FileName = fi.Name,
                Size = fi.Length,
                FileType = GuessMime(path),
            };
            if (fi.Length <= HashThresholdBytes)
            {
                try
                {
                    var hash = await ComputeSha256Async(path, CancellationToken.None);
                    meta.Hash = hash;
                    hashById[id] = hash;
                }
                catch (Exception ex)
                {
                    _logger.Warning(ex, "计算 SHA256 失败（跳过校验）：{File}", path);
                }
            }
            metas[id] = meta;
            ordered.Add((id, path, progress));
            addList.Add(progress);
        }
        _dispatcher.TryEnqueue(() =>
        {
            foreach (var p in addList) Transfers.Add(p);
        });

        var sessionId = string.Empty;
        var usedV1 = IsV1Protocol(target.Version);
        var linkCts = CancellationTokenSource.CreateLinkedTokenSource(_cts?.Token ?? new CancellationToken());
        try
        {
            // 2) 征询：按对端协议（http/https、v1/v2）自动协商，全部失败时把内层异常写进状态
            var payload = new PrepareUploadRequestDto { Info = DeviceInfo(), Files = metas };
            var prepare = await PrepareOnPeerAsync(target, JsonSerializer.Serialize(payload, LanJson.Options),
                usedV1, linkCts.Token);
            usedV1 = prepare.V1;
            if (prepare.Ok)
            {
                target.Https = prepare.Https;
                target.Version = prepare.V1 ? "1.0" : "2.1";
            }
            if (!prepare.Ok)
            {
                MarkAll(ordered, $"征询失败：{prepare.Error}");
                return;
            }
            if (prepare.Status == 204)
            {
                MarkAll(ordered, "对方未选择任何文件（可能已拒绝）");
                return;
            }
            if (prepare.Status == 403)
            {
                MarkAll(ordered, $"对方拒绝接收{Snippet(prepare.Body)}");
                return;
            }
            if (prepare.Status == 409)
            {
                MarkAll(ordered, "对方正忙（已有进行中的传输会话）");
                return;
            }
            if (prepare.Status is 401 or 429)
            {
                MarkAll(ordered, $"对方设置了接收 PIN 或验证次数过多（HTTP {prepare.Status}）");
                return;
            }
            if (prepare.Status is < 200 or >= 300)
            {
                MarkAll(ordered, $"对方响应异常（HTTP {prepare.Status}）{Snippet(prepare.Body)}");
                return;
            }

            PrepareUploadResponseDto? result;
            if (prepare.V1)
            {
                // v1 send-request 直接返回 {fileId: token}
                var tokens = JsonSerializer.Deserialize<Dictionary<string, string>>(prepare.Body, LanJson.Options)
                             ?? new Dictionary<string, string>();
                result = new PrepareUploadResponseDto { Files = tokens };
            }
            else
            {
                result = JsonSerializer.Deserialize<PrepareUploadResponseDto>(prepare.Body, LanJson.Options);
            }
            if (result?.Files is null)
            {
                MarkAll(ordered, "协议响应无效（缺少 files）");
                return;
            }
            sessionId = result.SessionId ?? string.Empty;

            // 3) 逐文件上传（顺序；对齐 LocalSend 顺序传输）
            var cancelNotified = false;
            foreach (var (id, path, progress) in ordered)
            {
                if (linkCts.IsCancellationRequested)
                {
                    progress.Status = "已取消";
                    progress.IsCompleted = true;
                    continue;
                }
                var status = result.Files.TryGetValue(id, out var s) ? s : "declined";
                if (status is "declined")
                {
                    progress.Status = "对方拒绝接收";
                    progress.IsCompleted = true;
                    continue;
                }
                if (status is "busy")
                {
                    progress.Status = "对方正忙（有未处理的传输请求）";
                    progress.IsCompleted = true;
                    continue;
                }
                if (status.Length == 0)
                {
                    progress.Status = "对方未响应该文件";
                    progress.IsCompleted = true;
                    continue;
                }

                try
                {
                    progress.Status = "发送中…";
                    var fileCts = CancellationTokenSource.CreateLinkedTokenSource(linkCts.Token);
                    progress.SetCancelHandler(() =>
                    {
                        try { fileCts.Cancel(); } catch { }
                        if (!cancelNotified && sessionId.Length > 0)
                        {
                            cancelNotified = true;
                            _ = TryCancelSessionAsync(target, sessionId);
                        }
                    });

                    await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, true);
                    using var content = new UploadStreamContent(fs, b => progress.ReportProgress(b), fileCts.Token);
                    var uploadPath = prepare.V1
                        ? $"/api/localsend/v1/send?fileId={Uri.EscapeDataString(id)}&token={Uri.EscapeDataString(status)}"
                        : $"/api/localsend/v2/upload?sessionId={Uri.EscapeDataString(sessionId)}&fileId={Uri.EscapeDataString(id)}&token={Uri.EscapeDataString(status)}";
                    using var reqMsg = new HttpRequestMessage(HttpMethod.Post,
                        $"{Scheme(prepare.Https)}://{target.IpAddress}:{target.Port}{uploadPath}");
                    reqMsg.Content = content;
                    using var upResp = await _uploadHttp.SendAsync(reqMsg, HttpCompletionOption.ResponseHeadersRead, fileCts.Token);
                    if (upResp.IsSuccessStatusCode)
                    {
                        progress.ReportProgress(progress.TotalBytes);
                        progress.Status = hashById.TryGetValue(id, out var h)
                            ? $"发送完成 ✓（SHA256 {h[..12]}…）"
                            : "发送完成 ✓（未校验哈希）";
                        progress.IsCompleted = true;
                        _logger.Information("发送完成：{File} → {Peer}", path, target.Display);
                    }
                    else
                    {
                        var err = await upResp.Content.ReadAsStringAsync(CancellationToken.None);
                        progress.Status = $"发送失败（{(int)upResp.StatusCode} {SafeSub(err, 80)}）";
                        progress.IsCompleted = true;
                        _logger.Warning("上传被拒：{File} → {Peer}，状态 {Code}，{Err}", path, target.Display, (int)upResp.StatusCode, err);
                    }
                }
                catch (OperationCanceledException)
                {
                    progress.Status = "已取消";
                    progress.IsCompleted = true;
                }
                catch (Exception ex)
                {
                    progress.Status = $"发送失败：{ex.Message}";
                    progress.IsCompleted = true;
                    _logger.Warning(ex, "发送文件失败：{File} → {Peer}", path, target.Display);
                }
            }

            // 4) 有失败/取消 → 通知对端取消会话，清理接收方残留
            if (sessionId.Length > 0 && ordered.Any(o => o.Progress.IsCompleted &&
                (o.Progress.Status.Contains("失败") || o.Progress.Status.Contains("取消"))))
            {
                _ = TryCancelSessionAsync(target, sessionId);
            }
        }
        catch (OperationCanceledException)
        {
            MarkAll(ordered, "已取消");
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "发送流程失败：{Peer}", target.Display);
            MarkAll(ordered, $"发送流程失败：{DescribeTransportError(ex)}");
        }
        finally
        {
            linkCts.Dispose();
        }
    }

    private async Task TryCancelSessionAsync(LanPeer target, string sessionId)
    {
        try
        {
            using var resp = await _controlHttp.PostAsync(
                $"{target.BaseUrl}/api/localsend/v2/cancel?sessionId={Uri.EscapeDataString(sessionId)}",
                content: null);
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "通知对端取消会话失败：{Session}", sessionId);
        }
    }

    /// <summary>
    /// 征询对方接收：依次尝试「对端自称的协议 + 相反协议」×「v2 + v1 路由」，
    /// 遇到 404 视为路由版本不匹配继续下一个；其余状态码（200/204/403/409/401/429）直接返回。
    /// </summary>
    private async Task<(bool Ok, int Status, string Body, bool Https, bool V1, string Error)> PrepareOnPeerAsync(
        LanPeer target, string payloadJson, bool preferV1, CancellationToken ct)
    {
        var schemeOrder = target.Https ? new[] { true, false } : new[] { false, true };
        var versionOrder = preferV1 ? new[] { true, false } : new[] { false, true };
        string? lastError = null;
        var lastStatus = 0;

        foreach (var https in schemeOrder)
        {
            foreach (var v1 in versionOrder)
            {
                var path = v1 ? "/api/localsend/v1/send-request" : "/api/localsend/v2/prepare-upload";
                var url = $"{Scheme(https)}://{target.IpAddress}:{target.Port}{path}";
                try
                {
                    using var resp = await _controlHttp.PostAsync(url,
                        new StringContent(payloadJson, Encoding.UTF8, "application/json"), ct);
                    var body = await resp.Content.ReadAsStringAsync(ct);
                    lastStatus = (int)resp.StatusCode;
                    if (resp.StatusCode == HttpStatusCode.NotFound) continue;
                    _logger.Information("征询 {Url} → HTTP {Status}（{Scheme}/{Version}）", url, lastStatus,
                        Scheme(https), v1 ? "v1" : "v2");
                    return (true, lastStatus, body, https, v1, string.Empty);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    lastError = DescribeTlsFailure(ex);
                    _logger.Warning(ex, "征询失败：{Url}", url);
                }
            }
        }

        return (false, lastStatus, string.Empty, target.Https, preferV1,
            lastError ?? "无法连接到对端（HTTP 与 HTTPS 均失败）");
    }

    private static bool IsV1Protocol(string? version) =>
        string.IsNullOrWhiteSpace(version) || version.StartsWith("1", StringComparison.Ordinal);

    private static string Scheme(bool https) => https ? "https" : "http";

    private static string Snippet(string body) =>
        string.IsNullOrWhiteSpace(body) ? string.Empty : "：" + SafeSub(body.Trim(), 160);

    /// <summary>把传输异常展开成可读文本（含内层原因，便于用户直接看到根因）。</summary>
    private static string DescribeTransportError(Exception ex)
    {
        var sb = new StringBuilder();
        sb.Append(ex.GetType().Name).Append(": ").Append(ex.Message);
        for (var inner = ex.InnerException; inner is not null; inner = inner.InnerException)
        {
            sb.Append(" → ").Append(inner.GetType().Name).Append(": ").Append(inner.Message);
        }
        return sb.ToString();
    }

    /// <summary>
    /// 在传输异常文本后追加 TLS 告警的可操作提示。
    /// alert 116 = certificate_required：对端在握手中要求客户端证书（TLS 1.3）；
    /// alert 48 = unknown_ca：对端不信任本机自签名客户端证书（严格双向 TLS）。
    /// </summary>
    private static string DescribeTlsFailure(Exception ex)
    {
        var text = DescribeTransportError(ex);
        if (text.Contains("alert: '116'", StringComparison.OrdinalIgnoreCase))
        {
            return text
                   + "（TLS alert 116 = 对端要求客户端证书。本机已按 LocalSend 的做法携带自签名客户端证书重试；"
                   + "若仍失败，说明对端只接受它自己的证书链，请在对端 LocalSend 设置中关闭「加密」(HTTPS) 后重试。）";
        }
        if (text.Contains("alert: '48'", StringComparison.OrdinalIgnoreCase))
        {
            return text
                   + "（TLS alert 48 = 对端不信任本机自签名客户端证书：对端开启了严格双向 TLS，"
                   + "请在对端 LocalSend 设置中关闭「加密」(HTTPS) 后重试。）";
        }
        return text;
    }

    /// <summary>
    /// 创建局域网专用 HttpClient：不使用系统代理（VPN 会写入代理），并接受对端自签名证书。
    /// LocalSend 开启「加密」时走 HTTPS，且会索取客户端证书，因此这里同时带上本进程的
    /// 自签名证书（与服务端复用同一张，EKU 同时包含 serverAuth / clientAuth）。
    /// </summary>
    private static HttpClient CreateLanHttpClient(TimeSpan timeout)
    {
        var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            AllowAutoRedirect = true,
            ConnectTimeout = TimeSpan.FromSeconds(8),
            SslOptions =
            {
                RemoteCertificateValidationCallback = (_, _, _, _) => true,
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                ClientCertificates = new X509CertificateCollection { LocalSendCertificate.Get() },
            },
        };
        return new HttpClient(handler) { Timeout = timeout };
    }

    private static void MarkAll(List<(string Id, string Path, TransferProgress Progress)> ordered, string status)
    {
        foreach (var (_, _, progress) in ordered)
        {
            if (progress.IsCompleted) continue;
            progress.Status = status;
            progress.IsCompleted = true;
        }
    }
    #endregion

    #region ===== 内部类型 =====
    private sealed class ReceiverSession
    {
        public ReceiverSession(string sessionId, string token, string peerDisplay)
        {
            SessionId = sessionId;
            Token = token;
            PeerDisplay = peerDisplay;
        }

        public string SessionId { get; }
        public string Token { get; }
        public string PeerDisplay { get; }
        public Dictionary<string, ReceiverFile> Files { get; } = new();
        public CancellationTokenSource Cts { get; } = new();
        public DateTime CreatedUtc { get; } = DateTime.UtcNow;
    }

    private sealed class ReceiverFile
    {
        public required FileMetaDto Meta { get; init; }
        public required TransferProgress Progress { get; init; }
        public string? SavePath { get; set; }
    }

    private sealed class LanTransferSettings
    {
        public string Alias { get; set; } = Environment.MachineName;
        public bool AutoAccept { get; set; }
        public string SaveDir { get; set; } = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "AI-Tool-Box");
    }

    /// <summary>流式上传 HttpContent：边读边回调进度；长度已知（Content-Length 必填）。</summary>
    private sealed class UploadStreamContent : HttpContent
    {
        private readonly Stream _stream;
        private readonly Action<long>? _onProgress;
        private readonly CancellationToken _ct;

        public UploadStreamContent(Stream stream, Action<long>? onProgress, CancellationToken ct)
        {
            _stream = stream;
            _onProgress = onProgress;
            _ct = ct;
        }

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            var buf = new byte[BufferSize];
            int n;
            while ((n = await _stream.ReadAsync(buf.AsMemory(0, buf.Length), _ct)) > 0)
            {
                await stream.WriteAsync(buf.AsMemory(0, n), _ct);
                _onProgress?.Invoke(_stream.Position);
            }
        }

        protected override bool TryComputeLength(out long length)
        {
            try
            {
                length = _stream.Length - _stream.Position;
                return length >= 0;
            }
            catch
            {
                length = 0;
                return false;
            }
        }
    }
    #endregion

    #region ===== 持久化 / 工具 =====
    private LanTransferSettings LoadSettings()
    {
        try
        {
            if (File.Exists(_settingsPath))
            {
                var json = File.ReadAllText(_settingsPath);
                var s = JsonSerializer.Deserialize<LanTransferSettings>(json, LanJson.Options);
                if (s is not null) return s;
            }
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "读取传输设置失败（使用默认值）");
        }
        return new LanTransferSettings();
    }

    private void SaveSettings()
    {
        try
        {
            var json = JsonSerializer.Serialize(_settings, LanJson.Options);
            File.WriteAllText(_settingsPath, json);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "保存传输设置失败");
        }
    }

    private string LoadOrCreateFingerprint()
    {
        var path = Path.Combine(_dataDir, "device_fingerprint.txt");
        try
        {
            if (File.Exists(path))
            {
                var fp = File.ReadAllText(path).Trim();
                if (fp.Length >= 8) return fp;
            }
            var fresh = Guid.NewGuid().ToString("N");
            File.WriteAllText(path, fresh);
            return fresh;
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "读写设备指纹失败（使用临时指纹）");
            return Guid.NewGuid().ToString("N");
        }
    }

    private DeviceInfoDto DeviceInfo() => new()
    {
        Alias = Alias,
        Version = "2.1",
        DeviceModel = "Windows",
        DeviceType = "desktop",
        Fingerprint = Fingerprint,
        Port = _http.Port,
        Protocol = "http",
        Download = false,
    };

    private static string GetLocalIPv4()
    {
        try
        {
            var candidates = new List<string>();
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
                foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                {
                    if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    var s = ua.Address.ToString();
                    if (s.StartsWith("169.254.")) continue; // APIPA
                    candidates.Add(s);
                }
            }
            return candidates.FirstOrDefault(s => s.StartsWith("192.168."))
                ?? candidates.FirstOrDefault(s => s.StartsWith("10."))
                ?? candidates.FirstOrDefault(s =>
                {
                    var parts = s.Split('.');
                    if (parts.Length != 4) return false;
                    return int.TryParse(parts[1], out var b) && b is >= 16 and <= 31 && parts[0] == "172";
                })
                ?? candidates.FirstOrDefault()
                ?? "127.0.0.1";
        }
        catch
        {
            return "127.0.0.1";
        }
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken ct)
    {
        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, true);
        using var sha = SHA256.Create();
        var hash = await sha.ComputeHashAsync(fs, ct);
        var sb = new StringBuilder(hash.Length * 2);
        foreach (var b in hash) sb.Append(b.ToString("x2"));
        return sb.ToString();
    }

    private string GetUniqueSavePath(string safeName)
    {
        var path = Path.Combine(SaveDirectory, safeName);
        if (!File.Exists(path)) return path;
        var ext = Path.GetExtension(safeName);
        var stem = Path.GetFileNameWithoutExtension(safeName);
        for (var i = 1; i < 10000; i++)
        {
            var candidate = Path.Combine(SaveDirectory, $"{stem}_{i}{ext}");
            if (!File.Exists(candidate)) return candidate;
        }
        return Path.Combine(SaveDirectory, $"{Guid.NewGuid():N}_{safeName}");
    }

    private static string SanitizeFileName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "unnamed.bin";
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(name.Length);
        foreach (var c in name)
        {
            if (Array.IndexOf(invalid, c) >= 0 || c < 0x20) sb.Append('_');
            else sb.Append(c);
        }
        var result = sb.ToString().Trim().Trim('.', ' ');
        return result.Length == 0 ? "unnamed.bin" : result;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // 删除失败忽略
        }
    }

    private static string SafeSub(string s, int max) =>
        string.IsNullOrEmpty(s) ? string.Empty : (s.Length <= max ? s : s[..max]);

    private static string GuessMime(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".bmp" => "image/bmp",
            ".svg" => "image/svg+xml",
            ".mp4" => "video/mp4",
            ".mkv" => "video/x-matroska",
            ".avi" => "video/x-msvideo",
            ".mov" => "video/quicktime",
            ".mp3" => "audio/mpeg",
            ".wav" => "audio/wav",
            ".flac" => "audio/flac",
            ".pdf" => "application/pdf",
            ".txt" or ".md" or ".log" => "text/plain",
            ".zip" => "application/zip",
            ".7z" => "application/x-7z-compressed",
            ".rar" => "application/vnd.rar",
            ".apk" => "application/vnd.android.package-archive",
            ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            ".pptx" => "application/vnd.openxmlformats-officedocument.presentationml.presentation",
            _ => "application/octet-stream",
        };
    }
    #endregion
}
