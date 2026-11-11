using System.Text.Json;
using Serilog;
using Serilog.Events;
using Serilog.Extensions.Hosting;

namespace AI_Tool_Box.Services;

/// <summary>应用程序日志基础设施配置（Serilog）。
/// 支持用户自定义日志输出目录：持久化到 %LOCALAPPDATA%\AI-Tool-Box\app_settings.json 的 logDirectory 键；
/// 运行时切换目录通过 <see cref="ReloadableLogger.Reload"/> 热重载，无需重启应用。</summary>
public static class LoggerSetup
{
    /// <summary>默认日志文件存放目录（相对应用安装目录）。</summary>
    public const string LogDirectory = "logs";

    private static readonly string SettingsDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AI-Tool-Box");
    private static readonly string SettingsPath = Path.Combine(SettingsDir, "app_settings.json");

    private static ReloadableLogger? _reloadable;
    private static string? _customDir;

    /// <summary>当前生效的日志根目录（自定义优先，未设置时为安装目录\logs）。</summary>
    public static string CurrentLogRoot => ResolveLogRoot(_customDir);

    /// <summary>配置并构建应用主 <see cref="ILogger"/>（可重载引导日志器），写入控制台与滚动文件。</summary>
    /// <returns>已配置完成的 <see cref="ILogger"/> 实例。</returns>
    public static ILogger Configure()
    {
        _customDir = ReadCustomDir();
        var logRoot = ResolveLogRoot(_customDir);
        EnsureDir(logRoot);

        var cfg = new LoggerConfiguration();
        ApplyConfiguration(cfg, logRoot);
        _reloadable = cfg.CreateBootstrapLogger();
        return _reloadable;
    }

    /// <summary>设置自定义日志目录并热重载日志管道；传 null/空白恢复默认目录。</summary>
    public static void SetCustomDirectory(string? dir)
    {
        _customDir = string.IsNullOrWhiteSpace(dir) ? null : dir.Trim();
        WriteCustomDir(_customDir);

        var logRoot = ResolveLogRoot(_customDir);
        EnsureDir(logRoot);

        if (_reloadable is not null)
        {
            _reloadable.Reload(lc => ApplyConfiguration(lc, logRoot));
        }
    }

    private static LoggerConfiguration ApplyConfiguration(LoggerConfiguration cfg, string logRoot)
    {
        return cfg.MinimumLevel.Information()
            .Enrich.FromLogContext()
            .Enrich.WithProperty("App", "AI-Tool-Box")
            .WriteTo.Console(
                outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
            .WriteTo.File(
                path: Path.Combine(logRoot, "aitoolbox-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14,
                encoding: System.Text.Encoding.UTF8,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}");
    }

    private static string ResolveLogRoot(string? customDir)
    {
        if (!string.IsNullOrWhiteSpace(customDir)) return customDir!;
        return Path.Combine(AppContext.BaseDirectory, LogDirectory);
    }

    private static void EnsureDir(string root)
    {
        if (!Directory.Exists(root)) Directory.CreateDirectory(root);
    }

    /// <summary>读取 app_settings.json 的 logDirectory 键（启动时尚无日志器，直接读文件，失败静默）。</summary>
    private static string? ReadCustomDir()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(SettingsPath));
            if (doc.RootElement.TryGetProperty("logDirectory", out var el)
                && el.ValueKind == JsonValueKind.String)
            {
                var v = el.GetString();
                return string.IsNullOrWhiteSpace(v) ? null : v;
            }
        }
        catch
        {
            // 启动早期无日志器可用：读取失败按默认目录处理
        }
        return null;
    }

    /// <summary>把 logDirectory 键合并写回 app_settings.json（保留其它键；null 表示移除）。</summary>
    private static void WriteCustomDir(string? dir)
    {
        try
        {
            Directory.CreateDirectory(SettingsDir);
            var store = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            if (File.Exists(SettingsPath))
            {
                var existing = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(SettingsPath));
                if (existing is not null) store = existing;
            }
            if (string.IsNullOrWhiteSpace(dir)) store.Remove("logDirectory");
            else store["logDirectory"] = JsonSerializer.SerializeToElement(dir);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(store, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // 写入失败不阻断日志重载（下次启动会回退默认目录）
        }
    }

    /// <summary>以崩溃日志形式记录未捕获异常并刷新日志缓冲。</summary>
    /// <param name="logger">宿主日志器实例。</param>
    /// <param name="exception">未捕获的异常。</param>
    public static void LogFatalException(ILogger logger, Exception exception)
    {
        logger.Fatal(exception, "应用发生未捕获异常，进程将终止");
        Log.CloseAndFlush();
    }
}
