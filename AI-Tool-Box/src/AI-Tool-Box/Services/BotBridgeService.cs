using Serilog;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace AI_Tool_Box.Services;

/// <summary>通讯软件接入：Telegram Bot 长轮询自动收发 + 通用 Webhook 单向推送。</summary>
public interface IBotBridgeService
{
    /// <summary>Bot 是否运行中。</summary>
    bool IsRunning { get; }

    /// <summary>启动 Telegram Bot 长轮询（文本消息 → AI 回答）。</summary>
    Task StartTelegramAsync(string botToken, bool useAi, Action<string> log, CancellationToken ct = default);

    /// <summary>停止 Bot。</summary>
    void Stop();

    /// <summary>向 Webhook 推送文本（企业微信/钉钉/飞书/Slack 等通用 JSON）。</summary>
    Task<string> SendWebhookAsync(string url, string text, string kind, CancellationToken ct = default);
}

/// <summary>Bot 桥接实现。</summary>
public sealed class BotBridgeService : IBotBridgeService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(40) };

    private readonly ILogger _logger;
    private readonly IAiQuickService _ai;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    /// <summary>构造：注入日志与 AI 服务。</summary>
    public BotBridgeService(ILogger logger, IAiQuickService ai)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _ai = ai ?? throw new ArgumentNullException(nameof(ai));
    }

    /// <inheritdoc/>
    public bool IsRunning => _loop is { IsCompleted: false };

    /// <inheritdoc/>
    public Task StartTelegramAsync(string botToken, bool useAi, Action<string> log, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(botToken)) throw new ArgumentException("Bot Token 不能为空。", nameof(botToken));
        Stop();

        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var token = botToken.Trim();
        log($"启动 Telegram Bot（AI 回复：{(useAi ? "开" : "关")}）");
        _loop = Task.Run(() => PollLoopAsync(token, useAi, log, _cts.Token), _cts.Token);
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public void Stop()
    {
        try
        {
            _cts?.Cancel();
            _cts?.Dispose();
        }
        catch { /* 忽略 */ }
        _cts = null;
        _loop = null;
    }

    private async Task PollLoopAsync(string token, bool useAi, Action<string> log, CancellationToken ct)
    {
        var baseUrl = $"https://api.telegram.org/bot{token}";
        long offset = 0;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var url = $"{baseUrl}/getUpdates?timeout=25&offset={offset}";
                using var resp = await Http.GetAsync(url, ct);
                var body = await resp.Content.ReadAsStringAsync(ct);
                if (!resp.IsSuccessStatusCode)
                {
                    log($"取消息失败 HTTP {(int)resp.StatusCode}：{Trim(body)}");
                    await Task.Delay(3000, ct);
                    continue;
                }

                using var doc = JsonDocument.Parse(body);
                if (!doc.RootElement.TryGetProperty("result", out var results) || results.ValueKind != JsonValueKind.Array)
                    continue;

                foreach (var update in results.EnumerateArray())
                {
                    if (update.TryGetProperty("update_id", out var idEl)) offset = idEl.GetInt64() + 1;
                    if (!update.TryGetProperty("message", out var message)) continue;
                    var text = message.TryGetProperty("text", out var textEl) ? textEl.GetString() : null;
                    if (!message.TryGetProperty("chat", out var chat) || !chat.TryGetProperty("id", out var chatIdEl)) continue;
                    if (string.IsNullOrWhiteSpace(text)) continue;

                    var chatId = chatIdEl.GetInt64();
                    log($"收到消息（chat {chatId}）：{Trim(text)}");

                    var reply = await BuildReplyAsync(text, useAi, ct);
                    await SendTelegramAsync(baseUrl, chatId, reply, ct);
                    log($"已回复：{Trim(reply)}");
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                log($"轮询异常：{ex.Message}");
                try { await Task.Delay(3000, ct); } catch { break; }
            }
        }
        log("Telegram Bot 已停止");
    }

    private async Task<string> BuildReplyAsync(string text, bool useAi, CancellationToken ct)
    {
        var trimmed = text.Trim();
        if (trimmed.Equals("/start", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals("/help", StringComparison.OrdinalIgnoreCase))
        {
            return "AI-Tool-Box Bot\n直接发送文字 → AI 回答\n发送 /time → 当前时间\n发送 /help → 本帮助";
        }
        if (trimmed.Equals("/time", StringComparison.OrdinalIgnoreCase))
            return DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

        var prompt = trimmed.StartsWith("/ai ", StringComparison.OrdinalIgnoreCase) ? trimmed[4..] : trimmed;
        if (!useAi) return "（AI 回复已关闭）收到：" + prompt;

        try
        {
            var answer = await _ai.AskAsync("你是一个简洁的中文助手，回答控制在 500 字以内。", prompt, ct: ct);
            return string.IsNullOrWhiteSpace(answer) ? "（AI 未返回内容）" : answer;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Bot AI 回复失败");
            return $"AI 调用失败：{ex.Message}";
        }
    }

    private static async Task SendTelegramAsync(string baseUrl, long chatId, string text, CancellationToken ct)
    {
        var payload = JsonSerializer.Serialize(new { chat_id = chatId, text = text.Length > 4000 ? text[..4000] : text });
        using var content = new StringContent(payload, Encoding.UTF8, "application/json");
        using var resp = await Http.PostAsync($"{baseUrl}/sendMessage", content, ct);
        _ = resp.StatusCode;
    }

    /// <inheritdoc/>
    public async Task<string> SendWebhookAsync(string url, string text, string kind, CancellationToken ct = default)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https"))
            throw new ArgumentException("Webhook 地址必须是 http(s) URL。");

        object payload = kind switch
        {
            "企业微信" => new { msgtype = "text", text = new { content = text } },
            "钉钉" => new { msgtype = "text", text = new { content = text } },
            "飞书" => new { msg_type = "text", content = new { text } },
            "Slack" => new { text },
            _ => new { text },
        };

        using var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        using var resp = await Http.PostAsync(uri, content, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        _logger.Information("Webhook 推送 {Kind}：HTTP {Status}", kind, (int)resp.StatusCode);
        return $"HTTP {(int)resp.StatusCode} {Trim(body)}";
    }

    private static string Trim(string s) => s.Length > 300 ? s[..300] + "…" : s;
}
