using Serilog;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AI_Tool_Box.Services;

/// <summary>GreenHub 节点账号（VMess 连接参数，由节点账号 API 下发）。</summary>
public sealed record VpnGhAccount(string Address, int Port, string Id, string Path, int AlterId, string Email);

/// <summary>GreenHub 节点账号 API 客户端（POST https://{domain}/d/v1/account，签名 = HMAC-SHA256 + Base64）。</summary>
public interface IVpnGhAccountClient
{
    /// <summary>获取指定域名节点的 VMess 账号；失败抛异常（含 stats 错误码）。</summary>
    Task<VpnGhAccount> GetAccountAsync(string domain, CancellationToken ct = default);
}

/// <summary>GreenHub 账号 API 实现。</summary>
public sealed class VpnGhAccountClient : IVpnGhAccountClient
{
    // 脱敏：真实签名密钥已从源码移除，请在本地填入你自己的密钥后再使用该功能。
    private const string HmacKey = "REPLACE_WITH_YOUR_HMAC_KEY";
    private const string Platform = "win";

    private readonly ILogger _logger;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };

    /// <summary>构造。</summary>
    public VpnGhAccountClient(ILogger logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc/>
    public async Task<VpnGhAccount> GetAccountAsync(string domain, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(domain)) throw new ArgumentException("节点域名为空。", nameof(domain));

        var uid = VpnCatalog.DeviceUid;
        var version = VpnCatalog.ClientVersion;
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var signBase = $"timestamp={timestamp}&uid={uid}&version={version}";
        var sign = Sign(signBase);

        var url = $"https://{domain}/d/v1/account?uid={Uri.EscapeDataString(uid)}"
                  + $"&version={Uri.EscapeDataString(version)}"
                  + $"&platform={Platform}"
                  + $"&sign={Uri.EscapeDataString(sign)}";

        using var req = new HttpRequestMessage(HttpMethod.Post, url);
        using var resp = await _http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"账号接口 HTTP {(int)resp.StatusCode}：{Trim(body)}");

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        var stats = root.TryGetProperty("stats", out var statsEl) && statsEl.TryGetInt32(out var s) ? s : -1;
        var msg = root.TryGetProperty("msg", out var msgEl) ? msgEl.GetString() ?? string.Empty : string.Empty;
        if (stats != 0)
            throw new InvalidOperationException($"账号获取失败（stats={stats} {msg}）");
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("账号接口返回缺少 data。");

        var account = new VpnGhAccount(
            Address: GetString(data, "address") ?? domain,
            Port: GetInt(data, "port") ?? 443,
            Id: GetString(data, "id") ?? throw new InvalidOperationException("账号缺少 id。"),
            Path: GetString(data, "path") ?? "/wes",
            AlterId: GetInt(data, "alterId") ?? 0,
            Email: GetString(data, "email") ?? string.Empty);

        _logger.Information("GreenHub 账号获取成功：{Domain} → {Address}:{Port}{Path}",
            domain, account.Address, account.Port, account.Path);
        return account;
    }

    private static string Sign(string payload)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(HmacKey));
        return Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(payload)));
    }

    private static string? GetString(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int? GetInt(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : null;

    private static string Trim(string s) => s.Length > 300 ? s[..300] + "…" : s;
}
