using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Serilog;

namespace AI_Tool_Box.Services;

/// <summary>已导入的 Edge 扩展条目。</summary>
public sealed class EdgeExtensionItem
{
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("version")] public string Version { get; set; } = string.Empty;
    [JsonPropertyName("installPath")] public string InstallPath { get; set; } = string.Empty;
    [JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;
}

/// <summary>持久化存储结构。</summary>
public sealed class EdgeExtensionStore
{
    [JsonPropertyName("testerUnlocked")] public bool TesterUnlocked { get; set; }
    [JsonPropertyName("extensions")] public List<EdgeExtensionItem> Extensions { get; set; } = new();
}

/// <summary>内测人员功能 + Edge 扩展管理：
/// 输入口令 114514 解锁"导入Edge扩展"；导入扩展文件夹（含 manifest.json）复制到应用数据目录；
/// 列表可启用/禁用；一键以 --load-extension 方式启动 Edge 加载全部已启用扩展。</summary>
public sealed class EdgeExtensionService
{
    /// <summary>内测口令。</summary>
    public const string TesterCode = "114514";

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    private readonly ILogger _logger;
    private readonly string _storePath;
    private readonly string _rootDir;

    public EdgeExtensionService(ILogger logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        var baseDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AI-Tool-Box");
        _rootDir = Path.Combine(baseDir, "edge_extensions");
        _storePath = Path.Combine(baseDir, "edge_extensions.json");
        Store = Load();
    }

    /// <summary>当前存储快照。</summary>
    public EdgeExtensionStore Store { get; private set; }

    /// <summary>内测功能是否已解锁。</summary>
    public bool IsTesterUnlocked => Store.TesterUnlocked;

    private EdgeExtensionStore Load()
    {
        try
        {
            if (File.Exists(_storePath))
            {
                var s = JsonSerializer.Deserialize<EdgeExtensionStore>(File.ReadAllText(_storePath));
                if (s is not null)
                {
                    // 清理已被手动删除的安装目录
                    s.Extensions.RemoveAll(e => !Directory.Exists(e.InstallPath));
                    _logger.Information("Edge 扩展配置已加载：{Count} 个扩展，内测解锁={Unlocked}",
                        s.Extensions.Count, s.TesterUnlocked);
                    return s;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Edge 扩展配置加载失败，使用默认配置");
        }
        return new EdgeExtensionStore();
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_storePath)!);
            File.WriteAllText(_storePath, JsonSerializer.Serialize(Store, JsonOpts));
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Edge 扩展配置保存失败");
        }
    }

    /// <summary>校验内测口令；正确则解锁并持久化。</summary>
    public bool UnlockTester(string code)
    {
        if (!string.Equals(code?.Trim(), TesterCode, StringComparison.Ordinal)) return false;
        Store.TesterUnlocked = true;
        Save();
        _logger.Information("内测人员功能已解锁");
        return true;
    }

    /// <summary>导入扩展文件夹：复制到应用数据目录并登记（同名覆盖）。</summary>
    public EdgeExtensionItem Import(string sourceFolder)
    {
        if (string.IsNullOrEmpty(sourceFolder) || !Directory.Exists(sourceFolder))
            throw new DirectoryNotFoundException("扩展文件夹不存在：" + sourceFolder);

        var manifest = Path.Combine(sourceFolder, "manifest.json");
        if (!File.Exists(manifest))
            throw new InvalidDataException("所选文件夹不含 manifest.json，不是有效的浏览器扩展目录。");

        string name = new DirectoryInfo(sourceFolder).Name;
        string version = string.Empty;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(manifest));
            if (doc.RootElement.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String)
                name = n.GetString() ?? name;
            if (doc.RootElement.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.String)
                version = v.GetString() ?? string.Empty;
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "解析扩展 manifest.json 失败，退化为目录名");
        }

        // 安装目录名去掉非法字符
        var safe = string.Join("_", name.Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries));
        if (safe.Length == 0) safe = "extension";
        var installPath = Path.Combine(_rootDir, safe);
        if (Directory.Exists(installPath)) Directory.Delete(installPath, recursive: true);
        Directory.CreateDirectory(Path.GetDirectoryName(installPath)!);
        CopyDirectory(sourceFolder, installPath);

        var item = new EdgeExtensionItem { Name = name, Version = version, InstallPath = installPath, Enabled = true };
        Store.Extensions.RemoveAll(e => string.Equals(e.InstallPath, installPath, StringComparison.OrdinalIgnoreCase));
        Store.Extensions.Add(item);
        Save();
        _logger.Information("已导入 Edge 扩展：{Name} {Version} → {Path}", name, version, installPath);
        return item;
    }

    /// <summary>设置扩展启用状态。</summary>
    public void SetEnabled(EdgeExtensionItem item, bool enabled)
    {
        item.Enabled = enabled;
        Save();
        _logger.Information("Edge 扩展 {Name} 已{State}", item.Name, enabled ? "启用" : "禁用");
    }

    /// <summary>删除扩展（目录 + 登记）。</summary>
    public void Remove(EdgeExtensionItem item)
    {
        try
        {
            if (Directory.Exists(item.InstallPath)) Directory.Delete(item.InstallPath, recursive: true);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "删除扩展目录失败：{Path}", item.InstallPath);
        }
        Store.Extensions.Remove(item);
        Save();
        _logger.Information("已删除 Edge 扩展：{Name}", item.Name);
    }

    private static void CopyDirectory(string src, string dst)
    {
        foreach (var dir in Directory.GetDirectories(src, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(dir.Replace(src, dst, StringComparison.OrdinalIgnoreCase));
        }
        foreach (var file in Directory.GetFiles(src, "*", SearchOption.AllDirectories))
        {
            File.Copy(file, file.Replace(src, dst, StringComparison.OrdinalIgnoreCase), overwrite: true);
        }
    }
}
