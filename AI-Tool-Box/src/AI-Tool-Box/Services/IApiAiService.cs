namespace AI_Tool_Box.Services;

/// <summary>OpenAI 兼容 API 服务契约：列出模型与流式生成回答。</summary>
public interface IApiAiService
{
    /// <summary>获取或设置 API 基地址（如 https://api.openai.com/v1）。</summary>
    string BaseUrl { get; set; }

    /// <summary>获取或设置 API Key（Bearer 认证）。</summary>
    string? ApiKey { get; set; }

    /// <summary>列出 API 可用模型（GET /models）。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>模型标识列表；失败时返回空列表。</returns>
    Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken cancellationToken = default);

    /// <summary>流式生成回答，按增量片段回调（POST /chat/completions，SSE）。</summary>
    /// <param name="model">目标模型标识。</param>
    /// <param name="prompt">提示词文本。</param>
    /// <param name="onChunk">每收到一段增量文本时的回调。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task GenerateStreamAsync(string model, string prompt, Action<string> onChunk, CancellationToken cancellationToken = default);
}
