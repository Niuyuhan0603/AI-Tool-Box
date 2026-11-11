using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AI_Tool_Box.Models;
using Serilog;

namespace AI_Tool_Box.Services;

/// <summary>右侧栏 AI 辅助聊天服务契约：多轮流式对话（OpenAI 兼容 / Anthropic 双协议）。</summary>
public interface IAiAssistChatService
{
    /// <summary>流式对话：按增量回调文本；失败抛异常（含 HTTP 状态与响应片段）。</summary>
    /// <param name="profile">提供商档案（协议 + 基地址）。</param>
    /// <param name="apiKey">API-Key（免鉴权档案可为 null/空）。</param>
    /// <param name="model">本次请求使用的模型标识（空则回退到档案默认模型）。</param>
    /// <param name="turns">完整对话历史（含本轮用户消息）。</param>
    /// <param name="onDelta">增量文本回调（在后台线程触发，调用方自行切线程）。</param>
    /// <param name="ct">取消令牌。</param>
    Task StreamAsync(AiAssistProfile profile, string? apiKey, string model, IReadOnlyList<ChatTurn> turns,
        Action<string> onDelta, CancellationToken ct = default);
}

/// <summary>右侧栏 AI 辅助聊天服务实现。</summary>
public sealed class AiAssistChatService : IAiAssistChatService
{
    private static readonly HttpClient Http = CreateClient();

    private readonly ILogger _logger;

    /// <summary>初始化聊天服务。</summary>
    public AiAssistChatService(ILogger logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    private static HttpClient CreateClient()
    {
        var h = new HttpClient(new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(15),
            PooledConnectionLifetime = TimeSpan.FromMinutes(10)
        })
        {
            Timeout = Timeout.InfiniteTimeSpan // SSE 长连接：读流时逐行等待，靠 CancellationToken 取消
        };
        h.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        return h;
    }

    /// <summary>流式对话实现。</summary>
    public async Task StreamAsync(AiAssistProfile profile, string? apiKey, string model, IReadOnlyList<ChatTurn> turns,
        Action<string> onDelta, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var baseUrl = (profile.BaseUrl ?? string.Empty).Trim().TrimEnd('/');
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            throw new InvalidOperationException("API 基地址为空，请在设置中填写。");
        }
        // 以调用方传入的模型为准（侧栏可临时改用本机实际已 pull 的模型），空值才回退档案默认
        var useModel = string.IsNullOrWhiteSpace(model) ? profile.DefaultModel : model.Trim();
        if (string.IsNullOrWhiteSpace(useModel))
        {
            throw new InvalidOperationException("模型标识为空：请在侧栏填写或先加载本机模型列表。");
        }

        using var req = new HttpRequestMessage(HttpMethod.Post,
            profile.Kind == "anthropic" ? $"{baseUrl}/v1/messages" : $"{baseUrl}/chat/completions");
        req.Content = new StringContent(profile.Kind == "anthropic"
            ? BuildAnthropicBody(useModel, profile, turns)
            : BuildOpenAiBody(useModel, profile, turns), Encoding.UTF8, "application/json");

        if (!profile.NoKeyRequired)
        {
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                throw new MissingApiKeyException(profile.DisplayName);
            }
            if (profile.Kind == "anthropic")
            {
                req.Headers.Add("x-api-key", apiKey);
                req.Headers.Add("anthropic-version", "2023-06-01");
            }
            else
            {
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            }
        }

        // 会话 ID：OpenAI 兼容头字段（网关/中转可据此归并会话）
        if (!string.IsNullOrWhiteSpace(profile.SessionId))
        {
            req.Headers.TryAddWithoutValidation("X-Session-Id", profile.SessionId);
        }

        _logger.Information("右侧栏AI辅助请求：{Name} {Url} 模型={Model} 轮次={Turns}",
            profile.DisplayName, req.RequestUri, useModel, turns.Count);

        using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            var snippet = body.Length > 300 ? body[..300] : body;
            _logger.Warning("右侧栏AI辅助请求失败：{Status} {Snippet}", (int)resp.StatusCode, snippet);
            throw new InvalidOperationException(
                FriendlyError(body) ?? $"请求失败 HTTP {(int)resp.StatusCode}：{snippet}");
        }

        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);

        while (!reader.EndOfStream)
        {
            var line = await reader.ReadLineAsync(ct);
            if (line is null) break;
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;

            var payload = line[5..].Trim();
            if (payload == "[DONE]") break;

            string? delta = null;
            try
            {
                using var doc = JsonDocument.Parse(payload);
                var root = doc.RootElement;
                if (profile.Kind == "anthropic")
                {
                    if (root.TryGetProperty("type", out var t) &&
                        t.GetString() == "content_block_delta" &&
                        root.TryGetProperty("delta", out var d) &&
                        d.TryGetProperty("text", out var txt))
                    {
                        delta = txt.GetString();
                    }
                }
                else
                {
                    if (root.TryGetProperty("choices", out var choices) &&
                        choices.ValueKind == JsonValueKind.Array &&
                        choices.GetArrayLength() > 0 &&
                        choices[0].TryGetProperty("delta", out var dv) &&
                        dv.TryGetProperty("content", out var c) &&
                        c.ValueKind == JsonValueKind.String)
                    {
                        delta = c.GetString();
                    }
                }
            }
            catch (JsonException)
            {
                // 非 JSON 心跳/注释行，跳过
            }

            if (!string.IsNullOrEmpty(delta))
            {
                onDelta(delta);
            }
        }
    }

    /// <summary>把本地模型常见失败翻译成可操作的中文提示（无法识别返回 null，保留原始报文）。</summary>
    private static string? FriendlyError(string body)
    {
        if (string.IsNullOrEmpty(body)) return null;
        if (body.Contains("failed to allocate", StringComparison.OrdinalIgnoreCase) ||
            body.Contains("out-of-memory", StringComparison.OrdinalIgnoreCase))
        {
            return "本地模型加载失败：内存/显存不足。请关闭占用内存较多的程序（浏览器、IM、模拟器等）后重试，或换用更小的模型。";
        }
        if (body.Contains("llama-server process has terminated", StringComparison.OrdinalIgnoreCase))
        {
            return "本地模型进程 llama-server 启动失败（通常是显存不足或 Ollama 运行时异常）：重启 Ollama 后再试。";
        }
        return null;
    }

    private static string BuildOpenAiBody(string model, AiAssistProfile profile, IReadOnlyList<ChatTurn> turns)
    {
        var msgs = turns.Select(t => new { role = t.Role, content = t.Content });
        var body = new Dictionary<string, object?>
        {
            ["model"] = model,
            ["messages"] = msgs,
            ["stream"] = true,
        };
        // 会话标识（OpenAI 规范字段 user；空值不发，避免部分网关校验失败）
        if (!string.IsNullOrWhiteSpace(profile.SessionId)) body["user"] = profile.SessionId;
        return JsonSerializer.Serialize(body);
    }

    private static string BuildAnthropicBody(string model, AiAssistProfile profile, IReadOnlyList<ChatTurn> turns)
    {
        // Anthropic 约束：首条必须是 user；system 由调用方并入 user 首轮文本
        var msgs = turns.Select(t => new { role = t.Role, content = t.Content });
        var body = new Dictionary<string, object?>
        {
            ["model"] = model,
            ["max_tokens"] = 4096,
            ["stream"] = true,
            ["messages"] = msgs,
        };
        if (!string.IsNullOrWhiteSpace(profile.SessionId))
        {
            body["metadata"] = new { user_id = profile.SessionId };
        }
        return JsonSerializer.Serialize(body);
    }
}

/// <summary>API-Key 未填写异常（侧栏据此弹窗引导用户前往设置）。</summary>
public sealed class MissingApiKeyException : Exception
{
    /// <summary>缺少 Key 的提供商显示名。</summary>
    public string ProviderName { get; }

    /// <summary>构造未填写 Key 异常。</summary>
    public MissingApiKeyException(string providerName)
        : base($"“{providerName}”尚未填写 API-Key，请在 设置 → 右侧栏AI辅助 中填写后重试。")
    {
        ProviderName = providerName;
    }
}
