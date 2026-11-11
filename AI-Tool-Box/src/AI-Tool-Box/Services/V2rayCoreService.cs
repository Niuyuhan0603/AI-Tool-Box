using Serilog;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace AI_Tool_Box.Services;

/// <summary>v2ray 核心管理：定位 v2ray.exe、写入 VMess(WS+TLS) 配置、启动本地 HTTP 入站代理、停止。</summary>
public interface IV2rayCoreService
{
    /// <summary>当前使用的 v2ray.exe 路径（未检测到为 null）。</summary>
    string? CorePath { get; }

    /// <summary>核心进程是否运行中。</summary>
    bool IsRunning { get; }

    /// <summary>本地 HTTP 入站端口（0=未运行）。</summary>
    int LocalPort { get; }

    /// <summary>核心输出日志（按行回调）。</summary>
    event Action<string>? Log;

    /// <summary>检测 v2ray.exe（用户指定 → Assets\v2ray → GreenHub 安装目录 → PATH）。</summary>
    string? DetectCore();

    /// <summary>指定 v2ray.exe 路径（持久化到 %LOCALAPPDATA%\AI-Tool-Box\v2ray_path.txt）。</summary>
    void SetCorePath(string? path);

    /// <summary>以指定账号启动核心，返回本地 HTTP 代理端口。</summary>
    Task<int> StartAsync(VpnGhAccount account, CancellationToken ct = default);

    /// <summary>停止核心进程。</summary>
    void Stop();
}

/// <summary>v2ray 核心实现（支持 v2ray 4.x 的 <c>-c</c> 与 5.x/xray 的 <c>run -c</c>）。</summary>
public sealed class V2rayCoreService : IV2rayCoreService
{
    private const int PortStart = 10810;
    private const int PortEnd = 10899;

    private readonly ILogger _logger;
    private Process? _process;
    private string? _exePath;

    /// <inheritdoc/>
    public event Action<string>? Log;

    /// <summary>构造：读取上次保存的自定义核心路径。</summary>
    public V2rayCoreService(ILogger logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _exePath = LoadSavedPath();
    }

    /// <summary>核心配置与运行目录：%LOCALAPPDATA%\AI-Tool-Box\v2ray\。</summary>
    private static string WorkDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AI-Tool-Box", "v2ray");

    private static string PathFile => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AI-Tool-Box", "v2ray_path.txt");

    /// <inheritdoc/>
    public string? CorePath => _exePath is not null && File.Exists(_exePath) ? _exePath : DetectCore();

    /// <inheritdoc/>
    public bool IsRunning => _process is { HasExited: false };

    /// <inheritdoc/>
    public int LocalPort { get; private set; }

    /// <inheritdoc/>
    public void SetCorePath(string? path)
    {
        _exePath = string.IsNullOrWhiteSpace(path) ? null : path.Trim();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(PathFile)!);
            File.WriteAllText(PathFile, _exePath ?? string.Empty, Encoding.UTF8);
            _logger.Information("v2ray 核心路径已保存：{Path}", _exePath);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "保存 v2ray 核心路径失败（忽略）");
        }
    }

    /// <inheritdoc/>
    public string? DetectCore()
    {
        // 1) 用户指定
        if (!string.IsNullOrWhiteSpace(_exePath) && File.Exists(_exePath)) return _exePath;

        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var progFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var progFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);

        // 2) 随应用放置（可选）
        var candidates = new List<string>
        {
            Path.Combine(AppContext.BaseDirectory, "Assets", "v2ray", "v2ray.exe"),
            // 3) GreenHub 客户端自带核心（安装目录 / 解包目录）
            Path.Combine(local, "Programs", "GreenHub", "resources", "libs", "v2ray.exe"),
            Path.Combine(progFiles, "GreenHub", "resources", "libs", "v2ray.exe"),
            Path.Combine(progFilesX86, "GreenHub", "resources", "libs", "v2ray.exe"),
            Path.Combine(local, "GreenHub", "resources", "libs", "v2ray.exe"),
            Path.Combine(progFiles, "GreenHub", "libs", "v2ray.exe"),
            Path.Combine(progFilesX86, "GreenHub", "libs", "v2ray.exe"),
        };

        // 4) PATH
        var pathVar = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var dir in pathVar.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            try { candidates.Add(Path.Combine(dir.Trim(), "v2ray.exe")); } catch { /* 非法路径忽略 */ }
        }

        foreach (var candidate in candidates)
        {
            try
            {
                if (File.Exists(candidate)) return candidate;
            }
            catch { /* 忽略非法路径 */ }
        }
        return null;
    }

    /// <inheritdoc/>
    public async Task<int> StartAsync(VpnGhAccount account, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(account);
        Stop();

        var exe = CorePath ?? throw new FileNotFoundException(
            "未检测到 v2ray 核心（v2ray.exe）。请在 VPN 页点击「浏览…」指定，或使用 GreenHub 自带的 libs\\v2ray.exe。");

        Directory.CreateDirectory(WorkDir);
        var port = FindFreePort();
        var configPath = Path.Combine(WorkDir, "config.json");
        var json = BuildConfig(account, port);
        // v2ray 不识别 UTF-8 BOM：必须无 BOM 写入
        await File.WriteAllTextAsync(configPath, json, new UTF8Encoding(false), ct);

        _process = StartProcess(exe, new[] { "-c", configPath });
        var ready = await WaitReadyAsync(port, TimeSpan.FromSeconds(10), ct);

        if (!ready)
        {
            // v2ray 5.x / xray 语法：run -c
            Stop();
            _process = StartProcess(exe, new[] { "run", "-c", configPath });
            ready = await WaitReadyAsync(port, TimeSpan.FromSeconds(12), ct);
        }

        if (!ready)
        {
            Stop();
            throw new InvalidOperationException(
                "v2ray 核心启动失败（本地代理端口未就绪）。请确认节点可用，并查看日志中的核心输出。");
        }

        LocalPort = port;
        _logger.Information("v2ray 核心已启动：{Exe} 本地 HTTP 代理 127.0.0.1:{Port}", exe, port);
        Log?.Invoke($"v2ray 已就绪：127.0.0.1:{port}");
        return port;
    }

    /// <inheritdoc/>
    public void Stop()
    {
        try
        {
            if (_process is { HasExited: false })
            {
                _process.Kill(entireProcessTree: true);
                _process.WaitForExit(3000);
                Log?.Invoke("v2ray 核心已停止");
            }
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "停止 v2ray 核心失败");
        }
        finally
        {
            _process?.Dispose();
            _process = null;
            LocalPort = 0;
        }
    }

    /// <summary>启动进程并挂接输出回调。</summary>
    private Process StartProcess(string exe, string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Path.GetDirectoryName(exe) ?? WorkDir,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) =>
        {
            if (string.IsNullOrWhiteSpace(e.Data)) return;
            _logger.Information("v2ray: {Line}", e.Data);
            Log?.Invoke(e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (string.IsNullOrWhiteSpace(e.Data)) return;
            _logger.Warning("v2ray(err): {Line}", e.Data);
            Log?.Invoke("[err] " + e.Data);
        };
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return process;
    }

    /// <summary>等待本地 HTTP 入站端口可连接（且进程未退出）。</summary>
    private async Task<bool> WaitReadyAsync(int port, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (_process is null || _process.HasExited) return false;
            try
            {
                using var client = new TcpClient();
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(800);
                await client.ConnectAsync(IPAddress.Loopback, port, cts.Token);
                if (client.Connected) return true;
            }
            catch { /* 未就绪，继续等待 */ }
            await Task.Delay(250, ct);
        }
        return false;
    }

    /// <summary>选择一个可用的本地端口。</summary>
    private static int FindFreePort()
    {
        for (var port = PortStart; port <= PortEnd; port++)
        {
            try
            {
                var probe = new TcpListener(IPAddress.Loopback, port);
                probe.Start();
                probe.Stop();
                return port;
            }
            catch { /* 占用，试下一个 */ }
        }
        throw new InvalidOperationException($"没有可用的本地端口（{PortStart}-{PortEnd}）。");
    }

    /// <summary>生成 v2ray 配置（HTTP 入站 → VMess(WS+TLS) 出站）。</summary>
    private static string BuildConfig(VpnGhAccount account, int port)
    {
        var config = new
        {
            log = new { loglevel = "warning" },
            inbounds = new object[]
            {
                new
                {
                    tag = "default",
                    port,
                    listen = "127.0.0.1",
                    protocol = "http",
                    sniffing = new { enabled = true, destOverride = new[] { "http", "tls" } },
                    settings = new { timeout = 0, allowTransparent = false, userLevel = 0 },
                },
            },
            outbounds = new object[]
            {
                new
                {
                    tag = "out-t",
                    mux = new { enabled = true },
                    protocol = "vmess",
                    settings = new
                    {
                        vnext = new[]
                        {
                            new
                            {
                                address = account.Address,
                                port = account.Port,
                                users = new[] { new { id = account.Id, alterId = account.AlterId } },
                            },
                        },
                    },
                    streamSettings = new
                    {
                        network = "ws",
                        security = "tls",
                        wsSettings = new { path = account.Path },
                    },
                },
                new { tag = "direct", protocol = "freedom", settings = new { } },
            },
            routing = new
            {
                rules = new object[]
                {
                    new { type = "field", inboundTag = new[] { "default" }, outboundTag = "out-t" },
                },
            },
        };

        return JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
    }

    private static string? LoadSavedPath()
    {
        try
        {
            return File.Exists(PathFile) ? File.ReadAllText(PathFile, Encoding.UTF8).Trim() : null;
        }
        catch
        {
            return null;
        }
    }
}
