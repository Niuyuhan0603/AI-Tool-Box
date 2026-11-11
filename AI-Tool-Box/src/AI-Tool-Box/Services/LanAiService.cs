using AI_Tool_Box.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Dispatching;
using Serilog;
using System.Collections.ObjectModel;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AI_Tool_Box.Services;

/// <summary>局域网 AI 服务实现：
/// 1) 服务端：HttpListener 代理转发到本机 Ollama/Api（黑白名单 + 授权询问）；
/// 2) UDP 9532 广播心跳（服务端广播 Hello）；客户端 ScanPeersAsync 监听 4 秒汇总；
/// 3) 持久化 lanai_settings.json 到 LOCALAPPDATA\AI-Tool-Box\webdata\。</summary>
public sealed class LanAiService : ILanAiService, IDisposable
{
    private static readonly string DataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AI-Tool-Box", "webdata");
    private static readonly string SettingsFile = Path.Combine(DataDir, "lanai_settings.json");
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true, PropertyNameCaseInsensitive = true, Converters = { new JsonStringEnumConverter() } };

    private readonly ILogger _logger;
    private readonly DispatcherQueue _dispatcher;
    private readonly object _peersLock = new();
    private readonly Dictionary<string, LanAiPeer> _peers = new(); // key: Ip:Port
    private readonly Dictionary<string, LanAiAuthRequest> _pendingAuth = new(); // key: Id

    private HttpListener? _http;
    private Task? _httpLoop;
    private UdpClient? _udpServer;   // 服务端广播
    private Task? _udpServerLoop;
    private PeriodicTimer? _heartbeatTimer;
    private CancellationTokenSource? _cts;
    private int _actualPort;

    private readonly IApiKeyService? _apiKeyService;  // 允许转发 API 时读取 BaseUrl/Key

    /// <inheritdoc/>
    public LanAiSettings Settings { get; private set; } = new();

    /// <inheritdoc/>
    public ObservableCollection<LanAiPeer> OnlinePeers { get; } = new();

    /// <inheritdoc/>
    public ObservableCollection<LanAiSavedEndpoint> SavedEndpoints { get; } = new();

    /// <inheritdoc/>
    public ObservableCollection<LanAiAuthRequest> PendingAuth { get; } = new();

    /// <inheritdoc/>
    public bool IsServerRunning => _http is not null && _http.IsListening;

    /// <inheritdoc/>
    public string LocalIp { get; private set; } = "127.0.0.1";

    /// <inheritdoc/>
    public int ActualServerPort => IsServerRunning ? _actualPort : Settings.ServerPort;

    /// <inheritdoc/>
    public string InfoText =>
        $"{Environment.MachineName} @ {LocalIp}:{ActualServerPort} | 后端：{(Settings.Backend == LanAiBackend.Ollama ? "Ollama" : (Settings.AllowApiForward ? "API(允许转发)" : "API(禁止转发)"))}";

    /// <summary>初始化：加载 JSON；解析本机 IP。</summary>
    public LanAiService(ILogger logger, DispatcherQueue dispatcher, IApiKeyService? apiKeyService = null)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _apiKeyService = apiKeyService;
        LocalIp = GetLocalLanIp();
        LoadSettings();
    }

    private static string GetLocalLanIp()
    {
        try
        {
            using var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, 0);
            s.Connect("8.8.8.8", 65530);
            if (s.LocalEndPoint is IPEndPoint ep) return ep.Address.ToString();
        }
        catch { /* ignore */ }
        return Dns.GetHostEntry(Dns.GetHostName()).AddressList
            .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a))?.ToString()
            ?? "127.0.0.1";
    }

    private void LoadSettings()
    {
        try
        {
            Directory.CreateDirectory(DataDir);
            if (File.Exists(SettingsFile))
            {
                using var fs = File.OpenRead(SettingsFile);
                var s = JsonSerializer.Deserialize<LanAiSettings>(fs, JsonOpts);
                if (s is not null) Settings = s;
            }
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "加载局域网 AI 配置失败，使用默认配置");
        }
        foreach (var ep in Settings.SavedEndpoints) SavedEndpoints.Add(ep);
    }

    /// <inheritdoc/>
    public void SaveSettings()
    {
        try
        {
            Directory.CreateDirectory(DataDir);
            Settings.SavedEndpoints = SavedEndpoints.ToList();
            using var fs = File.Create(SettingsFile);
            JsonSerializer.Serialize(fs, Settings, JsonOpts);
            _logger.Information("局域网 AI 配置已保存到 {File}", SettingsFile);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "保存局域网 AI 配置失败");
        }
    }

    // ============== 服务端 ==============

    /// <inheritdoc/>
    public bool StartServer()
    {
        if (IsServerRunning) return true;
        try
        {
            _cts = new CancellationTokenSource();
            // 端口碰撞自增（最多 64 次）
            _actualPort = Settings.ServerPort;
            int tries = 0;
            while (true)
            {
                try
                {
                    _http = new HttpListener();
                    _http.Prefixes.Add($"http://+:{_actualPort}/");
                    _http.Start();
                    break;
                }
                catch (HttpListenerException) when (tries++ < 64)
                {
                    _http?.Close();
                    _http = null;
                    _actualPort++;
                }
            }
            _httpLoop = RunHttpLoopAsync(_http, _cts.Token);

            // UDP 广播心跳
            _udpServer = new UdpClient();
            _udpServer.EnableBroadcast = true;
            _heartbeatTimer = new PeriodicTimer(TimeSpan.FromSeconds(3));
            _udpServerLoop = RunUdpHeartbeatAsync(_udpServer, _heartbeatTimer, _cts.Token);

            _logger.Information("局域网 AI 服务端已启动：HTTP {Port}，UDP {Udp}", _actualPort, Settings.UdpPort);
            return true;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "启动局域网 AI 服务端失败");
            StopServer();
            return false;
        }
    }

    /// <inheritdoc/>
    public void StopServer()
    {
        try { _cts?.Cancel(); } catch { }
        try { _heartbeatTimer?.Dispose(); } catch { }
        try
        {
            if (_http is not null)
            {
                _http.Stop();
                _http.Close();
            }
        }
        catch { }
        try { _udpServer?.Close(); } catch { }
        try { _httpLoop?.Wait(TimeSpan.FromSeconds(2)); } catch { }
        try { _udpServerLoop?.Wait(TimeSpan.FromSeconds(2)); } catch { }
        _http = null;
        _udpServer = null;
        _httpLoop = null;
        _udpServerLoop = null;
        _cts = null;
        _logger.Information("局域网 AI 服务端已停止");
    }

    private async Task RunUdpHeartbeatAsync(UdpClient udp, PeriodicTimer timer, CancellationToken ct)
    {
        var broadcastEp = new IPEndPoint(IPAddress.Broadcast, Settings.UdpPort);
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                try
                {
                    var msg = JsonSerializer.SerializeToUtf8Bytes(new
                    {
                        Type = "LanAiHello",
                        MachineName = Environment.MachineName,
                        Ip = LocalIp,
                        Port = _actualPort,
                        Backend = Settings.Backend.ToString(),
                        ApiForward = Settings.AllowApiForward
                    }, JsonOpts);
                    await udp.SendAsync(msg, msg.Length, broadcastEp);
                }
                catch (Exception ex)
                {
                    _logger.Warning(ex, "发送局域网 AI 心跳失败");
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _logger.Error(ex, "UDP 心跳循环结束"); }
    }

    private async Task RunHttpLoopAsync(HttpListener http, CancellationToken ct)
    {
        using var forwardHttp = new HttpClient(new SocketsHttpHandler { UseCookies = false }) { Timeout = TimeSpan.FromMinutes(3) };
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var ctx = await http.GetContextAsync().WaitAsync(ct);
                _ = HandleRequestAsync(ctx, forwardHttp); // fire-and-forget
            }
            catch (OperationCanceledException) { break; }
            catch (HttpListenerException) { break; }
            catch (Exception ex) { _logger.Error(ex, "HTTP 循环 GetContext 异常"); }
        }
    }

    private async Task HandleRequestAsync(HttpListenerContext ctx, HttpClient forwardHttp)
    {
        var req = ctx.Request;
        var resp = ctx.Response;
        try
        {
            string fromIp = ctx.Request.RemoteEndPoint?.Address.ToString() ?? "unknown";

            // 1) 黑白名单 + 授权询问
            int auth = AuthorizeIp(fromIp, out bool needConfirm, out LanAiAuthRequest? pend);
            if (auth == 0) // 0 = 拒绝
            {
                resp.StatusCode = (int)HttpStatusCode.Forbidden;
                var buf = Encoding.UTF8.GetBytes("""{"error":"IP 被拒绝（黑白名单）"}""");
                resp.ContentType = "application/json; charset=utf-8";
                await resp.OutputStream.WriteAsync(buf);
                resp.Close();
                return;
            }
            if (auth == 2 && needConfirm && pend is not null) // 2 = 需弹确认
            {
                // 30 秒轮询等待用户授权
                bool ok = await WaitAuthDecisionAsync(pend, 30_000);
                if (!ok)
                {
                    resp.StatusCode = (int)HttpStatusCode.Forbidden;
                    var buf = Encoding.UTF8.GetBytes("""{"error":"用户拒绝授权或超时"}""");
                    resp.ContentType = "application/json; charset=utf-8";
                    await resp.OutputStream.WriteAsync(buf);
                    resp.Close();
                    return;
                }
            }

            // 2) 决定转发目标
            string? target = BuildForwardTarget(req.Url?.PathAndQuery ?? "/");
            if (target is null)
            {
                resp.StatusCode = (int)HttpStatusCode.ServiceUnavailable;
                var buf = Encoding.UTF8.GetBytes("""{"error":"后端未配置或不允许 API 转发"}""");
                resp.ContentType = "application/json; charset=utf-8";
                await resp.OutputStream.WriteAsync(buf);
                resp.Close();
                return;
            }

            // 3) 读取入站请求 Body
            using var reqBody = new MemoryStream();
            if (req.InputStream is not null) await req.InputStream.CopyToAsync(reqBody);
            reqBody.Position = 0;

            using var fwdReq = new HttpRequestMessage(new HttpMethod(req.HttpMethod), target);
            if (reqBody.Length > 0)
            {
                var content = new ByteArrayContent(reqBody.ToArray());
                if (req.ContentType is not null) content.Headers.ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse(req.ContentType);
                fwdReq.Content = content;
            }
            if (Settings.Backend == LanAiBackend.Api && _apiKeyService is not null)
            {
                var key = _apiKeyService.Load("api");
                if (!string.IsNullOrEmpty(key)) fwdReq.Headers.TryAddWithoutValidation("Authorization", "Bearer " + key);
            }

            using var fwdResp = await forwardHttp.SendAsync(fwdReq, HttpCompletionOption.ResponseHeadersRead);
            resp.StatusCode = (int)fwdResp.StatusCode;
            foreach (var h in fwdResp.Content.Headers)
            {
                try { resp.AddHeader(h.Key, string.Join(", ", h.Value)); } catch { }
            }
            resp.ContentType = fwdResp.Content.Headers.ContentType?.ToString() ?? "application/json; charset=utf-8";
            await using (var fs = await fwdResp.Content.ReadAsStreamAsync())
            {
                await fs.CopyToAsync(resp.OutputStream);
            }
            resp.Close();
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "AI 代理请求失败 {Path}", req.Url?.PathAndQuery);
            try
            {
                resp.StatusCode = (int)HttpStatusCode.InternalServerError;
                var buf = Encoding.UTF8.GetBytes("{\"error\":\"" + JsonEncodedText.Encode(ex.Message).ToString() + "\"}");
                await resp.OutputStream.WriteAsync(buf);
                resp.Close();
            }
            catch { }
        }
    }

    private string? BuildForwardTarget(string pathAndQuery)
    {
        if (Settings.Backend == LanAiBackend.Ollama)
        {
            var baseUrl = Settings.OllamaBaseUrl.TrimEnd('/');
            // Ollama 兼容 API 映射：
            //   GET  /v1/models   -> GET  /api/tags
            //   POST /v1/chat/completions -> POST /api/chat
            //   其他转发到 Ollama 原样
            string path = pathAndQuery;
            if (path.StartsWith("/v1/models", StringComparison.OrdinalIgnoreCase))
            {
                return baseUrl + "/api/tags";
            }
            if (path.StartsWith("/v1/chat/completions", StringComparison.OrdinalIgnoreCase))
            {
                return baseUrl + "/api/chat";
            }
            // 其它路径（含 /v1/completions /v1/embeddings 等）透传
            return baseUrl + path;
        }
        else // Api
        {
            if (!Settings.AllowApiForward) return null;
            string baseUrl = Settings.ApiForwardBaseUrl?.Trim() ?? string.Empty;
            if (string.IsNullOrEmpty(baseUrl))
            {
                // 兼容：如果用户之前在 ApiKeyService 存过 provider=api_baseurl 就用（非 DPAPI 加密数据一般不支持，主要依赖显式 Settings 配置）
                return null;
            }
            baseUrl = baseUrl.TrimEnd('/');
            return baseUrl + pathAndQuery;
        }
    }

    /// <summary>
    /// IP 授权检查。返回值：0=拒绝，1=自动通过，2=需要弹授权询问。
    /// </summary>
    private int AuthorizeIp(string ip, out bool needConfirm, out LanAiAuthRequest? pending)
    {
        needConfirm = false;
        pending = null;
        bool isBlack = Settings.Blacklist.Any(b => NormalizeIp(b) == NormalizeIp(ip));
        bool isWhite = Settings.Whitelist.Any(w => NormalizeIp(w) == NormalizeIp(ip));
        if (Settings.Mode == LanAiAuthMode.Blacklist)
        {
            if (isBlack) return 0;
            if (isWhite) return 1;
            if (!Settings.PromptUnknownIp) return 0;
            pending = AddPending(ip, "unknown-blacklist");
            needConfirm = true;
            return 2;
        }
        else // Whitelist
        {
            if (isWhite) return 1;
            if (isBlack) return 0;
            if (!Settings.PromptUnknownIp) return 0;
            pending = AddPending(ip, "unknown-whitelist");
            needConfirm = true;
            return 2;
        }
    }

    private LanAiAuthRequest AddPending(string fromIp, string endpointHint)
    {
        var r = new LanAiAuthRequest
        {
            FromIp = fromIp,
            MachineName = TryReverseDns(fromIp),
            Endpoint = endpointHint,
            RequestedAtUtc = DateTime.UtcNow
        };
        _pendingAuth[r.Id] = r;
        _dispatcher.TryEnqueue(() =>
        {
            if (!PendingAuth.Any(x => x.Id == r.Id)) PendingAuth.Add(r);
        });
        // 60 秒超时自动拒绝
        _ = Task.Delay(60_000).ContinueWith(_ =>
        {
            if (_pendingAuth.Remove(r.Id, out var rr) && !rr.Handled)
            {
                rr.Handled = true;
                _dispatcher.TryEnqueue(() => PendingAuth.Remove(rr));
            }
        });
        return r;
    }

    private async Task<bool> WaitAuthDecisionAsync(LanAiAuthRequest pend, int timeoutMs)
    {
        using var cts = new CancellationTokenSource(timeoutMs);
        try
        {
            while (!cts.IsCancellationRequested)
            {
                if (_authResults.TryGetValue(pend.Id, out var allowed))
                {
                    // 移除临时结果
                    _authResults.TryRemove(pend.Id, out _);
                    return allowed;
                }
                if (pend.Handled)
                {
                    // RespondAuth 没来得及写 _authResults 视为拒绝
                    _authResults.TryRemove(pend.Id, out _);
                    return false;
                }
                await Task.Delay(100, cts.Token);
            }
        }
        catch (OperationCanceledException) { }
        // 超时 = 拒绝
        pend.Handled = true;
        _dispatcher.TryEnqueue(() => PendingAuth.Remove(pend));
        _pendingAuth.Remove(pend.Id, out _);
        _authResults.TryRemove(pend.Id, out _);
        return false;
    }

    /// <inheritdoc/>
    public void RespondAuth(string requestId, bool allow, bool remember)
    {
        if (!_pendingAuth.TryGetValue(requestId, out var r)) return;
        r.Handled = true;
        if (remember)
        {
            if (allow)
            {
                if (!Settings.Whitelist.Contains(r.FromIp)) Settings.Whitelist.Add(r.FromIp);
                Settings.Blacklist.Remove(r.FromIp);
            }
            else
            {
                if (!Settings.Blacklist.Contains(r.FromIp)) Settings.Blacklist.Add(r.FromIp);
                Settings.Whitelist.Remove(r.FromIp);
            }
            SaveSettings();
        }
        _pendingAuth.Remove(requestId, out _);
        _dispatcher.TryEnqueue(() =>
        {
            for (int i = PendingAuth.Count - 1; i >= 0; i--)
                if (PendingAuth[i].Id == requestId) PendingAuth.RemoveAt(i);
        });
        // 通过临时 in-memory "Handled" 标记已经足够；WaitAuthDecisionAsync 再轮询就看到 Handled=true
        // 但区分 allow/deny 需要额外状态：我们借 remember 存不到这里，简单做法：用 PendingAuth 字典值替换
        // 以下用小 hack：将 r.MachineName 前面加 "ALLOW:" 或 "DENY:" — WaitAuthDecisionAsync 需要检查
        // 为了简洁，这里改用一个 ConcurrentDictionary<string, bool> 存放结果。
        if (!_authResults.TryAdd(requestId, allow)) _authResults[requestId] = allow;
    }

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> _authResults = new();

    private static string NormalizeIp(string ip) => ip?.Trim() ?? string.Empty;

    private static string TryReverseDns(string ip)
    {
        try
        {
            var entry = Dns.GetHostEntry(IPAddress.Parse(ip));
            return entry.HostName;
        }
        catch { return "未知设备"; }
    }

    // ============== 客户端 ==============

    /// <inheritdoc/>
    public async Task ScanPeersAsync(int waitSeconds = 4)
    {
        using var udp = new UdpClient();
        udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        try { udp.Client.Bind(new IPEndPoint(IPAddress.Any, 0)); } catch { }
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(waitSeconds));

        // 清空过期项
        var cutoff = DateTime.UtcNow.AddSeconds(-12);
        lock (_peersLock)
        {
            var expired = _peers.Where(kv => kv.Value.LastSeenUtc < cutoff).Select(kv => kv.Key).ToList();
            foreach (var k in expired) _peers.Remove(k);
            _dispatcher.TryEnqueue(() =>
            {
                for (int i = OnlinePeers.Count - 1; i >= 0; i--)
                    if (OnlinePeers[i].LastSeenUtc < cutoff) OnlinePeers.RemoveAt(i);
            });
        }

        // 发送 Query 广播触发服务端立即回包（服务端默认 3 秒心跳，Query 可让 UI 更快速）
        try
        {
            var q = JsonSerializer.SerializeToUtf8Bytes(new { Type = "LanAiQuery", FromIp = LocalIp, MachineName = Environment.MachineName }, JsonOpts);
            await udp.SendAsync(q, q.Length, new IPEndPoint(IPAddress.Broadcast, Settings.UdpPort));
        }
        catch { }

        // 监听
        try
        {
            while (!cts.IsCancellationRequested)
            {
                var recv = await udp.ReceiveAsync().WaitAsync(cts.Token);
                try
                {
                    var doc = JsonDocument.Parse(recv.Buffer);
                    var type = doc.RootElement.GetProperty("Type").GetString();
                    if (type is not "LanAiHello") continue;
                    string ip = doc.RootElement.GetProperty("Ip").GetString() ?? recv.RemoteEndPoint.Address.ToString();
                    int port = doc.RootElement.GetProperty("Port").GetInt32();
                    string mn = doc.RootElement.GetProperty("MachineName").GetString() ?? string.Empty;
                    string be = doc.RootElement.TryGetProperty("Backend", out var b) ? b.GetString() ?? string.Empty : string.Empty;
                    if (ip == LocalIp && port == _actualPort) continue; // 跳过自己
                    var key = $"{ip}:{port}";
                    lock (_peersLock)
                    {
                        if (_peers.TryGetValue(key, out var existing))
                        {
                            existing.LastSeenUtc = DateTime.UtcNow;
                            existing.MachineName = mn;
                            existing.Backend = be;
                        }
                        else
                        {
                            var p = new LanAiPeer { Ip = ip, Port = port, MachineName = mn, Backend = be, LastSeenUtc = DateTime.UtcNow };
                            _peers[key] = p;
                            var cap = p;
                            _dispatcher.TryEnqueue(() =>
                            {
                                if (!OnlinePeers.Any(x => x.Ip == cap.Ip && x.Port == cap.Port)) OnlinePeers.Add(cap);
                            });
                        }
                    }
                }
                catch (Exception ex) { _logger.Warning(ex, "解析 LanAi UDP 广播失败"); }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _logger.Error(ex, "扫描局域网 AI 失败"); }
    }

    /// <inheritdoc/>
    public void SaveEndpoint(LanAiPeer peer, string name)
    {
        if (peer is null) return;
        var url = BuildBaseUrl(peer.Ip, peer.Port);
        var existing = SavedEndpoints.FirstOrDefault(x => x.BaseUrl == url);
        if (existing is null)
        {
            var ep = new LanAiSavedEndpoint
            {
                Name = string.IsNullOrWhiteSpace(name) ? peer.Display : name,
                BaseUrl = url,
                OwnerIp = peer.Ip,
                OwnerMachine = peer.MachineName,
                AddedAtUtc = DateTime.UtcNow
            };
            SavedEndpoints.Add(ep);
        }
        else
        {
            existing.Name = string.IsNullOrWhiteSpace(name) ? peer.Display : name;
            existing.OwnerMachine = peer.MachineName;
            existing.OwnerIp = peer.Ip;
        }
        SaveSettings();
    }

    /// <inheritdoc/>
    public void RemoveSavedEndpoint(string baseUrl)
    {
        var e = SavedEndpoints.FirstOrDefault(x => x.BaseUrl == baseUrl);
        if (e is not null) SavedEndpoints.Remove(e);
        SaveSettings();
    }

    private static string BuildBaseUrl(string ip, int port) => $"http://{ip}:{port}/";

    public void Dispose()
    {
        StopServer();
    }
}
