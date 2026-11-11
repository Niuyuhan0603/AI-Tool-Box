using System.Text;
using System.Text.Json;

namespace AI_Tool_Box.Services;

/// <summary>VPN 代理节点（内置节点表）。</summary>
public sealed record VpnNode
{
    /// <summary>节点 host 名（默认 HTTPS 端口 443）。</summary>
    public required string Host { get; init; }

    /// <summary>代理端口（默认 443）。</summary>
    public int Port { get; init; } = 443;

    /// <summary>上游代理协议：<c>http</c>=明文 CONNECT；<c>https</c>=先 TLS 再 CONNECT；<c>vmess</c>=VMess(WS/TLS) 由 v2ray 核心转发。</summary>
    public string Scheme { get; init; } = "http";

    /// <summary>节点协议：<c>http</c>=标准 HTTP(S) 代理；<c>vmess</c>=GreenHub 式 VMess(WS+TLS)。</summary>
    public string Protocol { get; init; } = "http";

    /// <summary>代理认证用户名（可空；配置未提供则不发送认证头）。</summary>
    public string? Username { get; init; }

    /// <summary>代理认证密码（可空）。</summary>
    public string? Password { get; init; }

    /// <summary>是否携带代理认证。</summary>
    public bool HasAuth => !string.IsNullOrEmpty(Username);
}

/// <summary>VPN 节点位置（城市 + 该城市全部可用 host 列表）。</summary>
public sealed record VpnLocation
{
    public required string CityCode { get; init; }
    public required string City { get; init; }
    public required string Country { get; init; }
    public required string CountryCode { get; init; }
    public required IReadOnlyList<VpnNode> Nodes { get; init; }
}

/// <summary>
/// 节点表：**优先**从 Assets/VPN-config.json 的 nodeList 解析（用户可替换该文件更新节点）；
/// 文件缺失/解析失败时回退到内置节点表。
/// </summary>
public static class VpnCatalog
{
    // 节点接入参数（运行时解码，避免源码明文检索）
    private const string EncodedUser = "YTJlcGZxNXVncTB1";
    private const string EncodedPass = "cHRreDNmcWc2djdu";

    /// <summary>节点代理认证用户名（运行时解码）。</summary>
    public static string ProxyUsername => Encoding.UTF8.GetString(Convert.FromBase64String(EncodedUser));

    /// <summary>节点代理认证密码（运行时解码）。</summary>
    public static string ProxyPassword => Encoding.UTF8.GetString(Convert.FromBase64String(EncodedPass));

    private static readonly Lazy<IReadOnlyList<VpnLocation>> _locations = new(LoadLocations);

    /// <summary>全部节点位置（来自 VPN-config.json；缺失时回退内置表）。</summary>
    public static IReadOnlyList<VpnLocation> Locations => _locations.Value;

    /// <summary>配置是否来自 VPN-config.json（true=GreenHub 式配置，节点默认 VMess 协议）。</summary>
    public static bool IsFileConfig => _isFileConfig;

    /// <summary>GreenHub 设备 uid（配置根节点 uid；缺省时使用本机持久化值）。</summary>
    public static string DeviceUid
    {
        get
        {
            _ = Locations; // 确保配置先解析（填充 _configUid）
            return _deviceUid.Value;
        }
    }

    /// <summary>客户端版本号（配置根节点 cur-version，用于账号 API 签名）。</summary>
    public static string ClientVersion
    {
        get
        {
            _ = Locations;
            return _clientVersion.Value;
        }
    }

    private static volatile bool _isFileConfig;
    private static string _configUid = string.Empty;
    private static string _configVersion = string.Empty;

    private static readonly Lazy<string> _deviceUid = new(() =>
    {
        if (!string.IsNullOrWhiteSpace(_configUid)) return _configUid;
        try
        {
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AI-Tool-Box", "vpn_uid.txt");
            if (File.Exists(path))
            {
                var saved = File.ReadAllText(path).Trim();
                if (!string.IsNullOrWhiteSpace(saved)) return saved;
            }
            var uid = Guid.NewGuid().ToString();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, uid);
            return uid;
        }
        catch
        {
            return Guid.NewGuid().ToString();
        }
    });

    private static readonly Lazy<string> _clientVersion = new(() =>
        string.IsNullOrWhiteSpace(_configVersion) ? "2.4.0" : _configVersion);

    /// <summary>从 Assets/VPN-config.json 解析 nodeList（国家 → servers[].domain）。</summary>
    private static IReadOnlyList<VpnLocation> LoadLocations()
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Assets", "VPN-config.json");
            if (!File.Exists(path)) return FallbackLocations;

            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (!doc.RootElement.TryGetProperty("nodeList", out var nodeList) ||
                nodeList.ValueKind != JsonValueKind.Array)
            {
                return FallbackLocations;
            }

            var root = doc.RootElement;
            _isFileConfig = true;
            _configUid = ReadString(root, "uid") ?? string.Empty;
            _configVersion = ReadString(root, "cur-version") ?? ReadString(root, "version") ?? string.Empty;

            // 可选全局默认（配置里可加 proxyScheme / proxyPort / proxyUser / proxyPass / protocol）
            var defaultScheme = ReadString(root, "proxyScheme") ?? "http";
            var defaultPort = ReadInt(root, "proxyPort") ?? 443;
            var defaultUser = ReadString(root, "proxyUser") ?? ReadString(root, "proxyUsername");
            var defaultPass = ReadString(root, "proxyPass") ?? ReadString(root, "proxyPassword");
            // GreenHub 配置默认是 VMess(WS+TLS)；显式写 protocol=http 可改回标准代理
            var defaultProtocol = (ReadString(root, "protocol") ?? "vmess").ToLowerInvariant();

            var list = new List<VpnLocation>();
            foreach (var country in nodeList.EnumerateArray())
            {
                var id = country.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? string.Empty : string.Empty;
                var zh = country.TryGetProperty("label_zh", out var zhEl) ? zhEl.GetString() ?? id : id;
                var en = country.TryGetProperty("label_en", out var enEl) ? enEl.GetString() ?? id : id;

                var hosts = new List<VpnNode>();
                if (country.TryGetProperty("servers", out var servers) && servers.ValueKind == JsonValueKind.Array)
                {
                    foreach (var s in servers.EnumerateArray())
                    {
                        var domain = s.TryGetProperty("domain", out var dEl) ? dEl.GetString() : null;
                        if (!string.IsNullOrWhiteSpace(domain))
                        {
                            hosts.Add(new VpnNode
                            {
                                Host = domain!,
                                Port = ReadInt(s, "port") ?? ReadInt(s, "proxyPort") ?? defaultPort,
                                Scheme = (ReadString(s, "scheme") ?? ReadString(s, "proxyScheme") ?? defaultScheme).ToLowerInvariant(),
                                Protocol = (ReadString(s, "protocol") ?? defaultProtocol).ToLowerInvariant(),
                                Username = ReadString(s, "user") ?? ReadString(s, "username") ?? defaultUser,
                                Password = ReadString(s, "pass") ?? ReadString(s, "password") ?? defaultPass,
                            });
                        }
                    }
                }
                if (hosts.Count == 0 || string.IsNullOrWhiteSpace(id)) continue;

                list.Add(new VpnLocation
                {
                    CityCode = id.ToLowerInvariant(),
                    City = string.IsNullOrWhiteSpace(zh) ? id : zh,
                    Country = string.IsNullOrWhiteSpace(en) ? id : en,
                    CountryCode = id.ToLowerInvariant(),
                    Nodes = hosts
                });
            }
            return list.Count > 0 ? list : FallbackLocations;
        }
        catch
        {
            return FallbackLocations;
        }
    }

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String ? el.GetString() : null;

    private static int? ReadInt(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var el)) return null;
        if (el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out var value)) return value;
        if (el.ValueKind == JsonValueKind.String && int.TryParse(el.GetString(), out var parsed)) return parsed;
        return null;
    }

    /// <summary>内置兜底节点表（VPN-config.json 不可用时使用）。</summary>
    private static readonly IReadOnlyList<VpnLocation> FallbackLocationsRaw = new List<VpnLocation>
    {
        new()
        {
            CityCode = "ams", City = "Amsterdam", Country = "Netherlands", CountryCode = "nl",
            Nodes = new List<VpnNode>
            {
                new() { Host = "free-amsterdam-https-5.cloudflingcdn.com" },
                new() { Host = "free-amsterdam-https-1.cloudburstcdn.com" },
                new() { Host = "free-amsterdam-https-6.cloudflingcdn.com" },
                new() { Host = "free-amsterdam-https-3.cloudflaracdn.com" },
                new() { Host = "free-amsterdam-https-4.cloudflaracdn.com" },
                new() { Host = "free-amsterdam-https-2.cloudflingcdn.com" }
            }
        },
        new()
        {
            CityCode = "sgp", City = "Singapore", Country = "Singapore", CountryCode = "sg",
            Nodes = new List<VpnNode>
            {
                new() { Host = "free-singapore-https-3.weathercloudapp.com" },
                new() { Host = "free-singapore-https-2.cloudtimecdn.com" },
                new() { Host = "free-singapore-https-1.cloudburstcdn.com" },
                new() { Host = "free-singapore-https-4.cloudflingcdn.com" }
            }
        },
        new()
        {
            CityCode = "lax", City = "Los Angeles", Country = "USA West", CountryCode = "usw",
            Nodes = new List<VpnNode>
            {
                new() { Host = "free-los-angeles-https-3.cloudflaracdn.com" },
                new() { Host = "free-los-angeles-https-1.cloudburstcdn.com" },
                new() { Host = "free-los-angeles-https-2.cloudtimecdn.com" },
                new() { Host = "free-los-angeles-https-4.cloudflaracdn.com" },
                new() { Host = "free-los-angeles-https-5.cloudflingcdn.com" },
                new() { Host = "free-los-angeles-https-6.cloudflingcdn.com" },
                new() { Host = "usa-west-free-https-1.weathercloudapp.com" }
            }
        }
    };

    /// <summary>兜底节点（附带旧版内置代理认证；配置文件里的节点不带认证，除非配置显式提供）。</summary>
    private static IReadOnlyList<VpnLocation> FallbackLocations => _fallback.Value;

    private static readonly Lazy<IReadOnlyList<VpnLocation>> _fallback = new(() =>
        FallbackLocationsRaw.Select(l => new VpnLocation
        {
            CityCode = l.CityCode,
            City = l.City,
            Country = l.Country,
            CountryCode = l.CountryCode,
            Nodes = l.Nodes.Select(n => n with { Username = ProxyUsername, Password = ProxyPassword }).ToList()
        }).ToList());

    /// <summary>系统代理 bypass 列表（本地/内网直连）。</summary>
    public const string ProxyOverride = "localhost;127.*;10.*;172.16.*;172.17.*;172.18.*;172.19.*;172.20.*;172.21.*;172.22.*;172.23.*;172.24.*;172.25.*;172.26.*;172.27.*;172.28.*;172.29.*;172.30.*;172.31.*;192.168.*;<local>";
}
