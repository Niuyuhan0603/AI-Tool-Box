using System.Collections.ObjectModel;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using AI_Tool_Box.Models;
using Microsoft.UI.Dispatching;
using Serilog;

namespace AI_Tool_Box.Services;

/// <summary>UDP 广播消息 Type 常量。</summary>
internal static class NetMsgType
{
    public const string Hello = "Hello";
    public const string Query = "Query";
}

/// <summary>局域网 UDP 消息（Hello/Query）。</summary>
internal sealed class NetUdpMsg
{
    public string Type { get; set; } = string.Empty;
    // Hello
    public string? Name { get; set; }
    public string? Ip { get; set; }
    public int TcpPort { get; set; }
    // Query
    public string? Keyword { get; set; }
    public string? FromIp { get; set; }
    public int FromTcpPort { get; set; }
    public long Ts { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
}

/// <summary>通过 TCP 推送的答案匹配集合。</summary>
internal sealed class NetAnswerMatchesPayload
{
    public string SenderName { get; set; } = string.Empty;
    public string SenderIp { get; set; } = string.Empty;
    public List<AnswerItem> Matches { get; set; } = new();
}

/// <summary>题库分享服务实现。UDP 9529 广播/查询；TCP 9530 推送匹配；本地 JSON 持久化。</summary>
public sealed class AnswerShareService : IAnswerShareService
{
    #region Constants
    private const int UdpPort = 9529;
    private const int TcpPortStart = 9530;
    private const int BroadcastIntervalMs = 3000;
    private const int PeerTimeoutSeconds = 15;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        IncludeFields = false
    };
    #endregion

    private readonly ILogger _logger;
    private readonly DispatcherQueue _dispatcher;
    private readonly object _peersLock = new();
    private readonly ConcurrentDictionary<string, LanPeer> _peerByKey = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _ioLock = new(); // answers.json / settings.json 读写互斥

    // UDP + TCP
    private UdpClient? _udp;
    private CancellationTokenSource? _cts;
    private Task? _helloTask;
    private Task? _udpReceiveTask;
    private Task? _peerCleanupTask;
    private TcpListener? _tcp;
    private Task? _acceptTask;
    private int _actualTcpPort;
    private string _localIp;
    private string _localName;

    // Data
    private readonly ObservableCollection<AnswerItem> _local = new();
    private readonly ObservableCollection<AnswerMatch> _lanResults = new();
    private readonly ObservableCollection<LanPeer> _peers = new();
    private AnswerShareSettings _settings;

    /// <inheritdoc/>
    public ObservableCollection<AnswerItem> LocalItems => _local;
    /// <inheritdoc/>
    public ObservableCollection<AnswerMatch> LanResults => _lanResults;
    /// <inheritdoc/>
    public ObservableCollection<LanPeer> OnlinePeers => _peers;
    /// <inheritdoc/>
    public AnswerShareSettings Settings { get => _settings; private set => _settings = value; }
    /// <inheritdoc/>
    public bool IsSharing { get; private set; }
    /// <inheritdoc/>
    public string LocalIp => _localIp;
    /// <inheritdoc/>
    public int LocalTcpPort => _actualTcpPort;
    /// <inheritdoc/>
    public string SettingsPath { get; }
    /// <inheritdoc/>
    public string AnswersPath { get; }

    /// <summary>初始化题库服务：加载本地 JSON 题库/设置（不启动网络）。</summary>
    public AnswerShareService(ILogger logger, DispatcherQueue dispatcher)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _localName = Environment.MachineName;
        _localIp = GuessLocalIPv4();
        _actualTcpPort = TcpPortStart;

        var webDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AI-Tool-Box", "webdata");
        if (!Directory.Exists(webDir)) Directory.CreateDirectory(webDir);
        AnswersPath = Path.Combine(webDir, "answers.json");
        SettingsPath = Path.Combine(webDir, "ans_settings.json");
        _settings = new AnswerShareSettings();

        LoadLocalFile();
        LoadSettings();
        _logger.Information("题库服务初始化：{Local}@{Ip}，Answers={Count}", _localName, _localIp, _local.Count);
    }

    #region Start / Stop
    /// <inheritdoc/>
    public bool StartSharing()
    {
        if (IsSharing) return true;
        try
        {
            _cts = new CancellationTokenSource();
            if (!StartTcp()) return false;
            StartUdp(_cts.Token);
            StartPeerCleanup(_cts.Token);
            IsSharing = true;
            _logger.Information("题库共享已开启：UDP {Udp}/TCP {Tcp}", UdpPort, _actualTcpPort);
            return true;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "题库共享启动失败");
            StopSharing();
            return false;
        }
    }

    /// <inheritdoc/>
    public void StopSharing()
    {
        IsSharing = false;
        try { _cts?.Cancel(); } catch { }
        try { _udp?.Close(); } catch { }
        try { _udp?.Dispose(); } catch { }
        try { _tcp?.Stop(); } catch { }
        try { _cts?.Dispose(); } catch { }
        _udp = null;
        _tcp = null;
        _cts = null;
        _logger.Information("题库共享已停止");
    }

    /// <inheritdoc/>
    public void Dispose() => StopSharing();
    #endregion

    #region Local
    /// <inheritdoc/>
    public void SaveLocal(AnswerItem item)
    {
        if (item is null) throw new ArgumentNullException(nameof(item));
        item.UpdatedAtUtc = DateTime.UtcNow;
        if (string.IsNullOrWhiteSpace(item.Id)) item.Id = Guid.NewGuid().ToString("N");

        _dispatcher.TryEnqueue(() =>
        {
            var idx = _local.ToList().FindIndex(x => x.Id == item.Id);
            if (idx >= 0) _local[idx] = item;
            else _local.Insert(0, item);
            PersistLocalFile();
        });
        _logger.Information("题库条目已保存：{Id} {Preview}", item.Id, SafePreview(item.Question));
    }

    /// <inheritdoc/>
    public bool DeleteLocal(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return false;
        bool removed = false;
        void Remove()
        {
            var match = _local.FirstOrDefault(x => x.Id == id);
            if (match != null)
            {
                removed = _local.Remove(match);
                if (removed) PersistLocalFile();
            }
        }
        if (_dispatcher.HasThreadAccess)
        {
            // UI 线程：直接同步移除（ObservableCollection 线程亲和）
            Remove();
        }
        else
        {
            // 后台线程：排队到 UI 线程并等待结果
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _dispatcher.TryEnqueue(() => { try { Remove(); } finally { tcs.TrySetResult(removed); } });
            removed = tcs.Task.Wait(TimeSpan.FromSeconds(2)) && tcs.Task.Result;
        }
        if (removed) _logger.Information("题库条目已删除：{Id}", id);
        return removed;
    }

    /// <inheritdoc/>
    public IEnumerable<AnswerItem> SearchLocal(string keyword, int limit = 20)
    {
        // 未填写题干的条目不参与搜索与局域网共享
        IEnumerable<AnswerItem> source = _local.Where(x => !string.IsNullOrWhiteSpace(x.Question));
        if (string.IsNullOrWhiteSpace(keyword)) return source.Take(limit).ToList();
        var k = keyword.Trim().ToLowerInvariant().Replace(" ", string.Empty);
        if (k.Length == 0) return source.Take(limit).ToList();
        return source
            .Select(x => new { Item = x, Score = ScoreFor(x, k) })
            .Where(o => o.Score > 0)
            .OrderByDescending(o => o.Score)
            .ThenByDescending(o => o.Item.UpdatedAtUtc)
            .Take(limit)
            .Select(o => o.Item)
            .ToList();
    }

    private static int ScoreFor(AnswerItem item, string normalizedKeyword)
    {
        int s = 0;
        var q = item.NormalizedQuestion;
        var a = (item.Answer ?? string.Empty).ToLowerInvariant().Replace(" ", string.Empty);
        var t = (item.Tags ?? string.Empty).ToLowerInvariant().Replace(" ", string.Empty);
        if (q.Contains(normalizedKeyword, StringComparison.Ordinal)) s += 100;
        if (a.Contains(normalizedKeyword, StringComparison.Ordinal)) s += 40;
        if (t.Contains(normalizedKeyword, StringComparison.Ordinal)) s += 60;
        // 子字符串分别命中：简单奖励
        foreach (var part in SplitToGrams(normalizedKeyword, 2))
        {
            if (q.Contains(part, StringComparison.Ordinal)) s += 10;
        }
        return s;
    }

    private static IEnumerable<string> SplitToGrams(string s, int n)
    {
        if (s.Length <= n) { yield return s; yield break; }
        for (int i = 0; i <= s.Length - n; i++) yield return s.Substring(i, n);
    }

    private void LoadLocalFile()
    {
        lock (_ioLock)
        {
            try
            {
                if (!File.Exists(AnswersPath)) return;
                var json = File.ReadAllText(AnswersPath, Encoding.UTF8);
                if (string.IsNullOrWhiteSpace(json)) return;
                var list = JsonSerializer.Deserialize<List<AnswerItem>>(json, JsonOptions);
                if (list is null) return;
                foreach (var item in list) _local.Add(item);
            }
            catch (Exception ex) { _logger.Error(ex, "加载 answers.json 失败：{Path}", AnswersPath); }
        }
    }

    private void PersistLocalFile()
    {
        lock (_ioLock)
        {
            try
            {
                var json = JsonSerializer.Serialize(_local.ToList(), new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(AnswersPath, json, Encoding.UTF8);
            }
            catch (Exception ex) { _logger.Error(ex, "保存 answers.json 失败：{Path}", AnswersPath); }
        }
    }

    /// <inheritdoc/>
    public void SaveSettings()
    {
        lock (_ioLock)
        {
            try
            {
                var json = JsonSerializer.Serialize(Settings, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(SettingsPath, json, Encoding.UTF8);
                _logger.Information("题库共享设置已保存：{Path}", SettingsPath);
            }
            catch (Exception ex) { _logger.Error(ex, "保存设置失败"); }
        }
    }

    /// <inheritdoc/>
    public void LoadSettings()
    {
        lock (_ioLock)
        {
            try
            {
                if (File.Exists(SettingsPath))
                {
                    var json = File.ReadAllText(SettingsPath, Encoding.UTF8);
                    if (!string.IsNullOrWhiteSpace(json))
                    {
                        var s = JsonSerializer.Deserialize<AnswerShareSettings>(json, JsonOptions);
                        if (s is not null) { Settings = s; return; }
                    }
                }
                Settings = new AnswerShareSettings();
            }
            catch (Exception ex) { _logger.Error(ex, "加载设置失败"); Settings = new AnswerShareSettings(); }
        }
    }
    #endregion

    #region UDP
    private void StartUdp(CancellationToken ct)
    {
        _udp = new UdpClient();
        _udp.EnableBroadcast = true;
        _udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _udp.Client.Bind(new IPEndPoint(IPAddress.Any, UdpPort));

        _helloTask = Task.Run(async () =>
        {
            try
            {
                using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(BroadcastIntervalMs));
                SendHello();
                while (!ct.IsCancellationRequested && await timer.WaitForNextTickAsync(ct))
                {
                    SendHello();
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { _logger.Error(ex, "UDP Hello 任务失败"); }
        }, ct);

        _udpReceiveTask = Task.Run(async () =>
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    var result = await _udp.ReceiveAsync(ct);
                    HandleUdpMessage(result.Buffer, result.RemoteEndPoint.Address.ToString());
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { _logger.Error(ex, "UDP 接收任务失败"); }
        }, ct);
    }

    private void SendHello()
    {
        if (_udp is null) return;
        var msg = new NetUdpMsg { Type = NetMsgType.Hello, Name = _localName, Ip = LocalIp, TcpPort = _actualTcpPort };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(msg);
        try { _udp.Send(bytes, bytes.Length, new IPEndPoint(IPAddress.Broadcast, UdpPort)); }
        catch { }
    }

    private void SendQuery(string keyword)
    {
        if (_udp is null) return;
        var msg = new NetUdpMsg
        {
            Type = NetMsgType.Query,
            Keyword = keyword,
            FromIp = LocalIp,
            FromTcpPort = _actualTcpPort
        };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(msg);
        try { _udp.Send(bytes, bytes.Length, new IPEndPoint(IPAddress.Broadcast, UdpPort)); }
        catch { }
    }

    private void HandleUdpMessage(byte[] bytes, string fromIp)
    {
        try
        {
            var json = Encoding.UTF8.GetString(bytes);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("Type", out var typeProp)) return;
            var type = typeProp.GetString();
            if (string.Equals(type, NetMsgType.Hello, StringComparison.Ordinal))
            {
                ProcessHello(root, fromIp);
            }
            else if (string.Equals(type, NetMsgType.Query, StringComparison.Ordinal))
            {
                ProcessQuery(root, fromIp);
            }
        }
        catch { /* 忽略非法 UDP 消息 */ }
    }

    private void ProcessHello(JsonElement root, string fromIp)
    {
        string name = root.GetPropertyOrDefault("Name", _ => _.GetString() ?? fromIp, fromIp);
        string ip = root.GetPropertyOrDefault("Ip", _ => _.GetString() ?? fromIp, fromIp);
        int port = root.GetPropertyOrDefault("TcpPort", _ => _.GetInt32(), 0);
        if (port <= 0 || port > 65535) return;
        if (ip == LocalIp && port == LocalTcpPort) return; // 忽略自己
        var peer = new LanPeer(name, ip, port, fingerprint: string.Empty, deviceType: "desktop") { LastSeenUtc = DateTime.UtcNow };
        var key = $"{ip}:{port}";
        bool add = false;
        lock (_peersLock)
        {
            if (_peerByKey.TryGetValue(key, out var existing)) existing.LastSeenUtc = DateTime.UtcNow;
            else { _peerByKey[key] = peer; add = true; }
        }
        if (add)
        {
            _dispatcher.TryEnqueue(() =>
            {
                lock (_peersLock)
                {
                    if (!_peers.Any(p => p.IpAddress == peer.IpAddress && p.Port == peer.Port)) _peers.Add(peer);
                }
            });
            _logger.Information("题库共享：发现设备 {Peer}", peer.Display);
        }
    }

    private void ProcessQuery(JsonElement root, string fromIp)
    {
        // 黑白名单判定
        if (!IsIpAllowed(fromIp))
        {
            _logger.Information("题库共享：拒绝来自 {Ip} 的查询（黑白名单）", fromIp);
            return;
        }
        string? keyword = null;
        string? fromTcpIp = null;
        int fromTcpPort = 0;
        try
        {
            if (root.TryGetProperty("Keyword", out var k)) keyword = k.GetString();
            if (root.TryGetProperty("FromIp", out var f)) fromTcpIp = f.GetString();
            if (root.TryGetProperty("FromTcpPort", out var p)) fromTcpPort = p.GetInt32();
        }
        catch { return; }
        if (string.IsNullOrWhiteSpace(keyword) || string.IsNullOrWhiteSpace(fromTcpIp) || fromTcpPort <= 0) return;

        var matches = SearchLocal(keyword, Settings.MaxMatchesPerPeer > 0 ? Settings.MaxMatchesPerPeer : 5).ToList();
        if (matches.Count == 0) return;

        _ = Task.Run(async () =>
        {
            try
            {
                using var client = new TcpClient();
                await client.ConnectAsync(fromTcpIp, fromTcpPort);
                using var ns = client.GetStream();
                var payload = new NetAnswerMatchesPayload
                {
                    SenderName = _localName,
                    SenderIp = LocalIp,
                    Matches = matches
                };
                var json = JsonSerializer.SerializeToUtf8Bytes(payload);
                var lenBytes = BitConverter.GetBytes(json.Length);
                await ns.WriteAsync(lenBytes);
                await ns.WriteAsync(json);
                await ns.FlushAsync();
                _logger.Information("题库共享：返回 {Count} 条 → {Ip}:{Port}", matches.Count, fromTcpIp, fromTcpPort);
            }
            catch (Exception ex) { _logger.Warning(ex, "题库共享：发送匹配结果失败 → {Ip}", fromTcpIp); }
        });
    }

    private bool IsIpAllowed(string ip)
    {
        if (Settings.Mode == ListMode.Whitelist)
            return Settings.Whitelist.Any(x => NormalizeIp(x) == NormalizeIp(ip));
        return !Settings.Blacklist.Any(x => NormalizeIp(x) == NormalizeIp(ip));

        static string NormalizeIp(string s) => (s ?? string.Empty).Trim();
    }

    private void StartPeerCleanup(CancellationToken ct)
    {
        _peerCleanupTask = Task.Run(async () =>
        {
            try
            {
                using var timer = new PeriodicTimer(TimeSpan.FromSeconds(4));
                while (!ct.IsCancellationRequested && await timer.WaitForNextTickAsync(ct))
                {
                    var cutoff = DateTime.UtcNow.AddSeconds(-PeerTimeoutSeconds);
                    var toRemove = new List<LanPeer>();
                    lock (_peersLock)
                    {
                        foreach (var p in _peers) if (p.LastSeenUtc < cutoff) toRemove.Add(p);
                        foreach (var r in toRemove) _peerByKey.TryRemove($"{r.IpAddress}:{r.Port}", out _);
                    }
                    if (toRemove.Count == 0) continue;
                    _dispatcher.TryEnqueue(() =>
                    {
                        lock (_peersLock)
                        {
                            foreach (var r in toRemove)
                            {
                                var m = _peers.FirstOrDefault(p => p.IpAddress == r.IpAddress && p.Port == r.Port);
                                if (m != null) _peers.Remove(m);
                            }
                        }
                    });
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { _logger.Error(ex, "设备清理任务异常"); }
        }, ct);
    }
    #endregion

    #region TCP
    private bool StartTcp()
    {
        for (int i = 0; i < 64; i++)
        {
            var port = TcpPortStart + i;
            try
            {
                var listener = new TcpListener(IPAddress.Any, port);
                listener.Start(16);
                _tcp = listener;
                _actualTcpPort = port;
                _acceptTask = AcceptLoop(_tcp, _cts!.Token);
                _logger.Information("题库共享 TCP 端口：{Port}", port);
                return true;
            }
            catch (SocketException) { }
        }
        _logger.Error("找不到可用 TCP 端口（起始 {Port}）", TcpPortStart);
        return false;
    }

    private async Task AcceptLoop(TcpListener listener, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(ct);
                _ = Task.Run(() => HandlePayloadAsync(client), ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _logger.Error(ex, "TCP AcceptLoop 异常"); }
    }

    private async Task HandlePayloadAsync(TcpClient client)
    {
        var remoteIp = ((IPEndPoint)client.Client.RemoteEndPoint!).Address.ToString();
        try
        {
            using (client)
            using (var ns = client.GetStream())
            {
                var payload = await ReadPayloadAsync(ns);
                if (payload is null || payload.Matches.Count == 0) return;
                foreach (var item in payload.Matches)
                {
                    var match = new AnswerMatch
                    {
                        Item = item,
                        SourceName = string.IsNullOrWhiteSpace(payload.SenderName) ? remoteIp : payload.SenderName,
                        SourceIp = payload.SenderIp ?? remoteIp
                    };
                    var k = $"{match.SourceIp}|{match.Item.Id}";
                    _dispatcher.TryEnqueue(() =>
                    {
                        // 去重
                        if (!_lanResults.Any(r => r.SourceIp == match.SourceIp && r.Item.Id == match.Item.Id))
                            _lanResults.Insert(0, match);
                    });
                }
                _logger.Information("题库共享：收到 {Count} 条 来自 {Name}@{Ip}", payload.Matches.Count, payload.SenderName, remoteIp);
            }
        }
        catch (Exception ex) { _logger.Warning(ex, "接收匹配结果失败"); }
    }

    private static async Task<NetAnswerMatchesPayload?> ReadPayloadAsync(NetworkStream ns)
    {
        var lenBuf = new byte[4];
        int done = 0;
        while (done < 4)
        {
            var n = await ns.ReadAsync(lenBuf.AsMemory(done, 4 - done));
            if (n == 0) return null;
            done += n;
        }
        int len = BitConverter.ToInt32(lenBuf, 0);
        if (len <= 0 || len > 16 * 1024 * 1024) return null; // 16MB 安全上限
        var buf = new byte[len];
        done = 0;
        while (done < len)
        {
            var n = await ns.ReadAsync(buf.AsMemory(done, len - done));
            if (n == 0) return null;
            done += n;
        }
        try { return JsonSerializer.Deserialize<NetAnswerMatchesPayload>(buf, JsonOptions); }
        catch { return null; }
    }
    #endregion

    #region SearchLan
    /// <inheritdoc/>
    public async Task SearchLanAsync(string keyword, int timeoutSeconds = 5)
    {
        if (string.IsNullOrWhiteSpace(keyword)) return;
        if (!IsSharing) throw new InvalidOperationException("请先开启题库共享（StartSharing）。");
        // 清空上次结果
        _dispatcher.TryEnqueue(_lanResults.Clear);
        SendQuery(keyword);
        _logger.Information("题库局域网搜索：\"{Kw}\" （等待 {Sec} 秒）", keyword, timeoutSeconds);
        await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 1, 60)));
        _logger.Information("题库局域网搜索结束：\"{Kw}\" 共 {Count} 条结果", keyword, _lanResults.Count);
    }
    #endregion

    #region Helpers
    private static string GuessLocalIPv4()
    {
        try
        {
            var candidates = new List<string>();
            foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType is System.Net.NetworkInformation.NetworkInterfaceType.Loopback
                    or System.Net.NetworkInformation.NetworkInterfaceType.Tunnel) continue;
                foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                {
                    if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    var s = ua.Address.ToString();
                    if (s.StartsWith("169.254.")) continue;
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
        catch { return "127.0.0.1"; }
    }

    private static string SafePreview(string s, int max = 40)
    {
        if (string.IsNullOrWhiteSpace(s)) return string.Empty;
        var r = s.Replace('\r', ' ').Replace('\n', ' ');
        return r.Length <= max ? r : r[..max] + "…";
    }
    #endregion
}

/// <summary>JsonElement 友好扩展。</summary>
internal static class JsonElementExtensions
{
    internal static T GetPropertyOrDefault<T>(this JsonElement root, string name, Func<JsonElement, T> extract, T defaultValue)
    {
        if (!root.TryGetProperty(name, out var p)) return defaultValue;
        try { return extract(p); }
        catch { return defaultValue; }
    }
}
