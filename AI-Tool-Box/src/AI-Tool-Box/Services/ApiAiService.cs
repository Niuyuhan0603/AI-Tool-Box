using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Serilog;

namespace AI_Tool_Box.Services;

/// <summary>OpenAI 兼容 API 服务实现：调用 /v1/models 列模型，/v1/chat/completions 流式生成（SSE）。</summary>
public sealed class ApiAiService : IApiAiService
{
    private static readonly string DefaultBase = "https://api.openai.com/v1";
    private readonly ILogger _logger;
    private readonly IUsageTracker? _usage;
    private readonly HttpClient _http;

    /// <summary>初始化 ApiAiService。</summary>
    /// <param name="logger">日志器。</param>
    /// <param name="usage">用量追踪器（可选，DI 注入；存在时记录 token 消耗）。</param>
    public ApiAiService(ILogger logger, IUsageTracker? usage = null)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _usage = usage;
        _http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        _http.BaseAddress = new Uri(DefaultBase);
        BaseUrl = DefaultBase;
    }

    /// <inheritdoc/>
    public string BaseUrl
    {
        get => _http.BaseAddress?.ToString().TrimEnd('/') ?? DefaultBase;
        set
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return;
            }
            try
            {
                _http.BaseAddress = new Uri(value.TrimEnd('/') + "/");
                _logger.Information("API 基地址已更新：{Url}", _http.BaseAddress);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "API 基地址无效：{Url}", value);
            }
        }
    }

    /// <inheritdoc/>
    public string? ApiKey { get; set; }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(ApiKey))
        {
            _logger.Warning("API Key 未配置，无法列出模型");
            return Array.Empty<string>();
        }

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, "models");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ApiKey);
            using var resp = await _http.SendAsync(req, cancellationToken);
            resp.EnsureSuccessStatusCode();

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(cancellationToken));
            var list = new List<string>();
            if (doc.RootElement.TryGetProperty("data", out var data))
            {
                foreach (var m in data.EnumerateArray())
                {
                    if (m.TryGetProperty("id", out var id))
                    {
                        list.Add(id.GetString() ?? string.Empty);
                    }
                }
            }
            _logger.Information("API 模型列表：{Count} 个", list.Count);
            return list;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "列出 API 模型失败");
            return Array.Empty<string>();
        }
    }

    /// <inheritdoc/>
    public async Task GenerateStreamAsync(string model, string prompt, Action<string> onChunk, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            throw new ArgumentException("模型标识不能为空", nameof(model));
        }
        if (string.IsNullOrWhiteSpace(prompt))
        {
            throw new ArgumentException("提示词不能为空", nameof(prompt));
        }
        if (string.IsNullOrEmpty(ApiKey))
        {
            throw new InvalidOperationException("API Key 未配置");
        }

        var payload = new
        {
            model,
            messages = new[]
            {
                new { role = "user", content = prompt }
            },
            stream = true,
            // 请求服务端在最终 chunk 返回 usage（OpenAI 兼容协议 stream_options.include_usage）
            stream_options = new { include_usage = true }
        };
        using var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

        using var req = new HttpRequestMessage(HttpMethod.Post, "chat/completions") { Content = content };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ApiKey);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
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

            // SSE 格式：以 "data: " 开头
            if (!line.StartsWith("data: ", StringComparison.Ordinal))
            {
                continue;
            }

            var json = line.Substring(6);
            if (json == "[DONE]")
            {
                break;
            }

            try
            {
                using var chunk = JsonDocument.Parse(json);
                // usage 字段：出现在最终 chunk（stream_options.include_usage=true 时），
                // 含 prompt_tokens / completion_tokens / total_tokens；用于用量追踪。
                if (_usage is not null && chunk.RootElement.TryGetProperty("usage", out var u))
                {
                    int pt = u.TryGetProperty("prompt_tokens", out var p) && p.TryGetInt32(out var pv) ? pv : 0;
                    int ct = u.TryGetProperty("completion_tokens", out var cc) && cc.TryGetInt32(out var cv) ? cv : 0;
                    if (pt > 0 || ct > 0)
                    {
                        _usage.RecordApiCall(model, pt, ct);
                        _logger.Information("API token 用量已记录：{Model} prompt={P} completion={C}", model, pt, ct);
                    }
                }
                if (chunk.RootElement.TryGetProperty("choices", out var choices))
                {
                    foreach (var c in choices.EnumerateArray())
                    {
                        if (c.TryGetProperty("delta", out var delta) &&
                            delta.TryGetProperty("content", out var text) &&
                            text.ValueKind == JsonValueKind.String)
                        {
                            var t = text.GetString();
                            if (!string.IsNullOrEmpty(t))
                            {
                                onChunk(t);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "解析 API SSE 响应失败：{Line}", json);
            }
        }
    }
}
