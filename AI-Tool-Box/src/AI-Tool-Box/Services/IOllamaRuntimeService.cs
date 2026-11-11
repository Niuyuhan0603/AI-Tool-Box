namespace AI_Tool_Box.Services;

/// <summary>Ollama 运行时管理契约：检测安装、启动/停止服务、拉取与删除模型（调用外部 ollama.exe）。</summary>
public interface IOllamaRuntimeService
{
    /// <summary>当前使用的 ollama.exe 路径；未检测到时为 null。</summary>
    string? ExecutablePath { get; }

    /// <summary>由本应用启动的服务的进程是否仍在运行。</summary>
    bool IsServeProcessRunning { get; }

    /// <summary>手动指定 ollama.exe 路径（持久化到 %LOCALAPPDATA%\AI-Tool-Box\ollama_path.txt）。</summary>
    void SetExecutablePath(string path);

    /// <summary>检测 ollama.exe（用户指定 → PATH → 常见安装目录 → 应用目录 Assets\ollama）。</summary>
    string? Detect();

    /// <summary>探测 http://localhost:11434 是否已可响应。</summary>
    Task<bool> IsServerRunningAsync(CancellationToken ct = default);

    /// <summary>一键启动 <c>ollama serve</c>；已在运行则直接返回 true。</summary>
    Task<bool> StartServeAsync(CancellationToken ct = default);

    /// <summary>停止由本应用启动的服务进程。</summary>
    void StopServe();

    /// <summary>拉取模型（<c>ollama pull</c>），逐行回报进度文本。</summary>
    Task<bool> PullModelAsync(string model, IProgress<string>? output = null, CancellationToken ct = default);

    /// <summary>删除模型（优先 HTTP /api/delete，失败回退 <c>ollama rm</c>）。</summary>
    Task<bool> DeleteModelAsync(string model, CancellationToken ct = default);
}
