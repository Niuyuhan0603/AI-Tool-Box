using System.Security.Cryptography;
using System.Text;
using Serilog;

namespace AI_Tool_Box.Services;

/// <summary>API Key 安全存储实现：使用 DPAPI（CurrentUser 作用域）加密后保存到本地文件。</summary>
public sealed class ApiKeyService : IApiKeyService
{
    private static readonly string StoreDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AI-Tool-Box", "keys");

    private readonly ILogger _logger;

    /// <summary>初始化 ApiKeyService 并确保存储目录存在。</summary>
    /// <param name="logger">日志器。</param>
    public ApiKeyService(ILogger logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        try
        {
            Directory.CreateDirectory(StoreDir);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "创建密钥存储目录失败：{Dir}", StoreDir);
        }
    }

    /// <inheritdoc/>
    public void Save(string name, string apiKey)
    {
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrEmpty(apiKey))
        {
            return;
        }

        try
        {
            var bytes = Encoding.UTF8.GetBytes(apiKey);
            var cipher = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
            File.WriteAllBytes(GetPath(name), cipher);
            _logger.Information("API Key 已加密保存：{Name}", name);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "保存 API Key 失败：{Name}", name);
        }
    }

    /// <inheritdoc/>
    public string? Load(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var path = GetPath(name);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var cipher = File.ReadAllBytes(path);
            var bytes = ProtectedData.Unprotect(cipher, null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "读取 API Key 失败：{Name}", name);
            return null;
        }
    }

    /// <inheritdoc/>
    public void Delete(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        try
        {
            var path = GetPath(name);
            if (File.Exists(path))
            {
                File.Delete(path);
                _logger.Information("API Key 已删除：{Name}", name);
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "删除 API Key 失败：{Name}", name);
        }
    }

    private static string GetPath(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(c, '_');
        }
        return Path.Combine(StoreDir, name + ".key");
    }
}
