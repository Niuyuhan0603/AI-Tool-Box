using System.Text.Json;

namespace AI_Tool_Box.Services;

/// <summary>
/// FFmpeg 可执行文件定位器（线程安全，单例使用）。
/// 解析顺序：<br/>
/// 1) 用户自定义路径（持久化于 %LOCALAPPDATA%\AI-Tool-Box\app_settings.json 的 ffmpegPath 键）；<br/>
/// 2) 系统 PATH 及常见安装位置；<br/>
/// 3) 应用目录 Assets\ffmpeg.exe（不再随包内置；若用户手动放置此文件也会被识别）。
/// </summary>
public sealed class FfmpegLocator
{
    private static readonly string SettingsDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AI-Tool-Box");

    private static readonly string SettingsPath = Path.Combine(SettingsDir, "app_settings.json");
    private const string SettingsKey = "ffmpegPath";

    private readonly object _gate = new();
    private string? _customPath;
    private bool _loaded;

    /// <summary>读取用户自定义 ffmpeg 路径（可为 null 表示未设置）。</summary>
    public string? GetCustomPath()
    {
        lock (_gate)
        {
            EnsureLoaded();
            return _customPath;
        }
    }

    /// <summary>设置用户自定义 ffmpeg 路径（null/空白表示清除），立即持久化到设置文件。</summary>
    public void SetCustomPath(string? path)
    {
        lock (_gate)
        {
            _customPath = string.IsNullOrWhiteSpace(path) ? null : path.Trim();
            SaveSettings();
        }
    }

    /// <summary>
    /// 解析当前实际生效的 ffmpeg.exe 绝对路径；找不到返回 null。
    /// 自定义路径仅在文件真实存在时生效，否则自动回退到下一优先级。
    /// </summary>
    public string? Resolve()
    {
        string? custom;
        lock (_gate)
        {
            EnsureLoaded();
            custom = _customPath;
        }
        if (!string.IsNullOrWhiteSpace(custom))
        {
            try { if (File.Exists(custom)) return custom; } catch { /* 无效路径则忽略 */ }
        }

        // 系统 PATH
        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var dir in pathEnv.Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            try
            {
                var full = Path.Combine(dir.Trim(), "ffmpeg.exe");
                if (File.Exists(full)) return full;
            }
            catch { }
        }

        // 常见安装位置
        string[] candidates =
        {
            @"C:\Program Files\FFmpeg\bin\ffmpeg.exe",
            @"C:\ProgramData\chocolatey\bin\ffmpeg.exe",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), @"scoop\shims\ffmpeg.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), @"AppData\Local\Microsoft\WinGet\Links\ffmpeg.exe"),
        };
        foreach (var c in candidates)
        {
            try { if (File.Exists(c)) return c; } catch { }
        }

        // 最后兜底：应用目录 Assets\ffmpeg.exe（不再随包内置，仅当用户手动放置此文件时识别）
        var bundled = Path.Combine(AppContext.BaseDirectory, "Assets", "ffmpeg.exe");
        try { if (File.Exists(bundled)) return bundled; } catch { }
        return null;
    }

    /// <summary>首次访问时从 app_settings.json 读取自定义路径（在锁内调用）。</summary>
    private void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;
        try
        {
            if (!File.Exists(SettingsPath)) return;
            using var doc = JsonDocument.Parse(File.ReadAllText(SettingsPath));
            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty(SettingsKey, out var el) &&
                el.ValueKind == JsonValueKind.String)
            {
                _customPath = el.GetString();
            }
        }
        catch
        {
            // 设置文件损坏或不可读时按未设置处理
        }
    }

    /// <summary>把自定义路径写回 app_settings.json（保留其它键；在锁内调用）。</summary>
    private void SaveSettings()
    {
        try
        {
            Directory.CreateDirectory(SettingsDir);
            var settings = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            if (File.Exists(SettingsPath))
            {
                try
                {
                    var existing = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(SettingsPath));
                    if (existing is not null) settings = existing;
                }
                catch { /* 损坏则重建 */ }
            }

            if (_customPath is null) settings.Remove(SettingsKey);
            else settings[SettingsKey] = JsonSerializer.SerializeToElement(_customPath);

            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // 持久化失败不影响内存中的设置
        }
    }
}
