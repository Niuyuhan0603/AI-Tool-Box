namespace AI_Tool_Box.Services;

/// <summary>Ollama 本地 AI 服务契约：列出已安装模型与流式生成回答。</summary>
public interface IOllamaService
{
    /// <summary>获取或设置 Ollama 服务基地址（默认 http://localhost:11434）。</summary>
    string BaseUrl { get; set; }

    /// <summary>列出 Ollama 已安装模型。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>模型名称列表；服务不可用时返回空列表。</returns>
    Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken cancellationToken = default);

    /// <summary>流式生成回答，按增量片段回调。</summary>
    /// <param name="model">目标模型名称。</param>
    /// <param name="prompt">提示词文本。</param>
    /// <param name="onChunk">每收到一段增量文本时的回调。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>表示生成完成的任务。</returns>
    Task GenerateStreamAsync(string model, string prompt, Action<string> onChunk, CancellationToken cancellationToken = default);
}
