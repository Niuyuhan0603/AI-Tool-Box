using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Serilog;

namespace AI_Tool_Box.Services;

/// <summary>Ollama 本地 AI 服务实现：通过 HTTP 调用 Ollama REST API（/api/tags 列模型，/api/generate 流式生成）。</summary>
public sealed class OllamaService : IOllamaService
{
    private static readonly Uri DefaultBase = new("http://localhost:11434");
    private readonly ILogger _logger;
    private readonly HttpClient _http;

    /// <summary>初始化 OllamaService 并创建内部 HttpClient。</summary>
    /// <param name="logger">日志器。</param>
    public OllamaService(ILogger logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _http = new HttpClient { BaseAddress = DefaultBase, Timeout = TimeSpan.FromMinutes(10) };
        BaseUrl = DefaultBase.ToString();
    }

    /// <inheritdoc/>
    public string BaseUrl
    {
        get => _http.BaseAddress?.ToString() ?? DefaultBase.ToString();
        set
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return;
            }
            try
            {
                _http.BaseAddress = new Uri(value.TrimEnd('/') + "/");
                _logger.Information("Ollama 基地址已更新：{Url}", _http.BaseAddress);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Ollama 基地址无效：{Url}", value);
            }
        }
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var resp = await _http.GetAsync("/api/tags", cancellationToken);
            resp.EnsureSuccessStatusCode();

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(cancellationToken));
            var list = new List<string>();
            if (doc.RootElement.TryGetProperty("models", out var models))
            {
                foreach (var m in models.EnumerateArray())
                {
                    if (m.TryGetProperty("name", out var name))
                    {
                        list.Add(name.GetString() ?? string.Empty);
                    }
                }
            }
            _logger.Information("Ollama 模型列表：{Count} 个", list.Count);
            return list;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "列出 Ollama 模型失败");
            return Array.Empty<string>();
        }
    }

    /// <summary>
    /// 探测任意地址（如 http://localhost:11434 或 …/v1）的 Ollama /api/tags，返回实际已安装模型。
    /// 供侧栏 AI 辅助与设置页填充真实模型（不修改共享 BaseUrl，服务不可用时返回空列表）。
    /// </summary>
    public static async Task<IReadOnlyList<string>> ListModelsAtAsync(string baseUrl, CancellationToken cancellationToken = default)
    {
        var root = ToOllamaRoot(baseUrl);
        if (root.Length == 0) return Array.Empty<string>();
        try
        {
            using var resp = await ProbeHttp.GetAsync(root + "/api/tags", cancellationToken);
            if (!resp.IsSuccessStatusCode) return Array.Empty<string>();
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(cancellationToken));
            return ReadTagNames(doc.RootElement);
        }
        catch (Exception)
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>把 OpenAI 兼容基地址（…/v1）归一化为 Ollama 根地址。</summary>
    public static string ToOllamaRoot(string? baseUrl)
    {
        var b = (baseUrl ?? string.Empty).Trim().TrimEnd('/');
        if (b.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)) b = b[..^3].TrimEnd('/');
        return b;
    }

    private static readonly HttpClient ProbeHttp = new() { Timeout = TimeSpan.FromSeconds(5) };

    private static List<string> ReadTagNames(JsonElement root)
    {
        var list = new List<string>();
        if (root.TryGetProperty("models", out var models))
        {
            foreach (var m in models.EnumerateArray())
            {
                if (m.TryGetProperty("name", out var name))
                {
                    var n = name.GetString();
                    if (!string.IsNullOrWhiteSpace(n)) list.Add(n);
                }
            }
        }
        return list;
    }

    /// <inheritdoc/>
    public async Task GenerateStreamAsync(string model, string prompt, Action<string> onChunk, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            throw new ArgumentException("模型名不能为空", nameof(model));
        }
        if (string.IsNullOrWhiteSpace(prompt))
        {
            throw new ArgumentException("提示词不能为空", nameof(prompt));
        }

        var payload = new
        {
            model,
            prompt,
            stream = true
        };
        using var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

        using var resp = await _http.PostAsync("/api/generate", content, cancellationToken);
        resp.EnsureSuccessStatusCode();

        using var stream = await resp.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);

        while (!reader.EndOfStream)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            try
            {
                using var chunk = JsonDocument.Parse(line);
                if (chunk.RootElement.TryGetProperty("response", out var text))
                {
                    var t = text.GetString();
                    if (!string.IsNullOrEmpty(t))
                    {
                        onChunk(t);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "解析 Ollama 流式响应失败：{Line}", line);
            }
        }
    }
}
