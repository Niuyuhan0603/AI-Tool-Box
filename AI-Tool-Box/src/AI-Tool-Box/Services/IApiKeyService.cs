namespace AI_Tool_Box.Services;

/// <summary>API Key 安全存储契约：使用 DPAPI 加密保存敏感密钥（遵循 Rules.md 安全准则）。</summary>
public interface IApiKeyService
{
    /// <summary>保存指定名称的 API Key（加密后落盘）。</summary>
    /// <param name="name">密钥逻辑名（如 provider 名）。</param>
    /// <param name="apiKey">明文 API Key。</param>
    void Save(string name, string apiKey);

    /// <summary>读取指定名称的 API Key 明文；不存在时返回 null。</summary>
    /// <param name="name">密钥逻辑名。</param>
    /// <returns>明文 API Key 或 null。</returns>
    string? Load(string name);

    /// <summary>删除指定名称的 API Key。</summary>
    /// <param name="name">密钥逻辑名。</param>
    void Delete(string name);
}
