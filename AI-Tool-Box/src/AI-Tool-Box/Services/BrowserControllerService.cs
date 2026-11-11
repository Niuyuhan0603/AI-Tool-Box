using Serilog;
using System.Text;
using System.Text.Json;

namespace AI_Tool_Box.Services;

/// <summary>AI 浏览器控制器契约：让大模型通过约定 JSON 动作循环操控内嵌 WebView2。</summary>
public interface IBrowserControllerService
{
    /// <summary>是否有控制任务在运行。</summary>
    bool IsRunning { get; }

    /// <summary>启动控制循环，直到模型输出 done 或达到最大步数。</summary>
    /// <param name="goal">任务目标（自然语言）。</param>
    /// <param name="maxSteps">最大步数上限。</param>
    /// <param name="progress">每步进度回调。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>结束说明。</returns>
    Task<string> RunAsync(string goal, int maxSteps, IProgress<string> progress, CancellationToken ct = default);
}

/// <summary>AI 浏览器控制器：解析模型输出的 JSON 动作（navigate/click/type/read/readpage/wait/done）并驱动 WebView2。
/// 依赖约定式 JSON 协议，不依赖模型 function-calling 能力。</summary>
public sealed class BrowserControllerService : IBrowserControllerService
{
    private const string SystemPrompt = """
        你是一个浏览器操作智能体，可以操控一个内嵌浏览器（WebView2）来完成任务。
        每次只输出【一个】JSON 对象，不要输出任何解释文字、前后缀或 Markdown 代码围栏。

        可用动作（action 字段）：
        1. {"action":"navigate","url":"https://..."}                 跳转到指定网址
        2. {"action":"click","selector":"CSS选择器"}                 点击元素
        3. {"action":"type","selector":"CSS选择器","text":"内容"}    清空并填入文本（用于填代码/输入框）
        4. {"action":"read","selector":"CSS选择器"}                  读取该元素的文本
        5. {"action":"readpage"}                                     读取整页正文
        6. {"action":"wait","ms":1500}                               等待若干毫秒（页面加载/跳转后使用）
        7. {"action":"done","summary":"完成说明"}                    任务完成，结束循环

        规则：
        - 只能使用上面列出的字段名；selector 必须是标准 CSS 选择器（可含 #id、.class、[attr]、:nth-child 等）。
        - 页面跳转后先 wait 再 read，确认已加载完成。
        - 不确定页面结构时，先用 readpage 观察，再决定下一步。
        - 任务达成或无法继续时，输出 done。
        """;

    private readonly ILogger _logger;
    private readonly IWebShellService _web;
    private readonly IAiQuickService _ai;
    private int _running;

    /// <summary>初始化。</summary>
    public BrowserControllerService(ILogger logger, IWebShellService web, IAiQuickService ai)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _web = web ?? throw new ArgumentNullException(nameof(web));
        _ai = ai ?? throw new ArgumentNullException(nameof(ai));
    }

    /// <inheritdoc/>
    public bool IsRunning => Volatile.Read(ref _running) == 1;

    /// <inheritdoc/>
    public async Task<string> RunAsync(string goal, int maxSteps, IProgress<string> progress, CancellationToken ct = default)
    {
        if (Interlocked.Exchange(ref _running, 1) == 1)
        {
            return "已有浏览器控制任务在运行。";
        }

        try
        {
            if (maxSteps < 1) maxSteps = 10;
            var history = new StringBuilder();
            var lastResult = "（尚未执行任何动作）";

            for (var step = 1; step <= maxSteps; step++)
            {
                ct.ThrowIfCancellationRequested();

                var url = _web.CurrentUrl ?? "(未知)";
                var pageText = await _web.GetContentAsync() ?? string.Empty;
                if (pageText.Length > 4000) pageText = pageText[..4000];

                var content =
                    $"【目标】{goal}\n" +
                    $"【当前网址】{url}\n" +
                    $"【页面正文（已截断）】\n{pageText}\n\n" +
                    $"【上一步结果】{lastResult}\n" +
                    $"【已执行步骤】\n{(history.Length == 0 ? "（无）" : history.ToString())}\n\n" +
                    "请输出下一步动作 JSON：";

                progress?.Report($"步骤 {step}/{maxSteps}：请求模型决策…");

                string reply;
                try
                {
                    reply = await _ai.AskAsync(SystemPrompt, content, null, ct);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    _logger.Error(ex, "浏览器控制器调用模型失败");
                    return $"调用模型失败：{ex.Message}";
                }

                var action = ParseAction(reply);
                if (action is null)
                {
                    lastResult = "上一条回复无法解析为 JSON 动作，请严格只输出一个 JSON 对象。";
                    history.AppendLine($"- （解析失败）{Truncate(reply, 160)}");
                    progress?.Report(lastResult);
                    continue;
                }

                progress?.Report($"步骤 {step}：{action.Describe()}");
                var (result, done) = await ExecuteActionAsync(action, ct);
                lastResult = result;
                history.AppendLine($"- {action.Describe()} → {Truncate(result, 200)}");

                if (done)
                {
                    progress?.Report($"完成：{result}");
                    return result;
                }
            }

            var msg = $"达到最大步数（{maxSteps}），自动停止。";
            progress?.Report(msg);
            return msg;
        }
        catch (OperationCanceledException)
        {
            progress?.Report("已停止。");
            return "已停止。";
        }
        finally
        {
            Interlocked.Exchange(ref _running, 0);
        }
    }

    private async Task<(string Result, bool Done)> ExecuteActionAsync(BrowserAction a, CancellationToken ct)
    {
        switch (a.Action)
        {
            case "navigate":
            {
                if (string.IsNullOrWhiteSpace(a.Url)) return ("navigate 缺少 url", false);
                var ok = await _web.NavigateAsync(a.Url);
                await WaitForPageAsync(ct);
                return (ok ? $"已导航到 {a.Url}" : $"导航失败：{a.Url}", false);
            }
            case "click":
            {
                if (string.IsNullOrWhiteSpace(a.Selector)) return ("click 缺少 selector", false);
                var clicked = await _web.ClickAsync(a.Selector);
                await Task.Delay(400, ct);
                return (clicked ? $"已点击 {a.Selector}" : $"未找到或点击失败：{a.Selector}", false);
            }
            case "type":
            {
                if (string.IsNullOrWhiteSpace(a.Selector)) return ("type 缺少 selector", false);
                var typed = await _web.SetValueAsync(a.Selector, a.Text ?? string.Empty);
                return (typed ? $"已填入 {a.Text?.Length ?? 0} 字到 {a.Selector}" : $"填入失败：{a.Selector}", false);
            }
            case "read":
            {
                if (string.IsNullOrWhiteSpace(a.Selector)) return ("read 缺少 selector", false);
                var text = await _web.ReadTextAsync(a.Selector);
                return (text is null ? $"未找到元素：{a.Selector}" : Truncate(text, 1500), false);
            }
            case "readpage":
            {
                var page = await _web.GetContentAsync() ?? string.Empty;
                return (Truncate(page, 3000), false);
            }
            case "wait":
            {
                var ms = Math.Clamp(a.Ms, 100, 15000);
                await Task.Delay(ms, ct);
                return ($"已等待 {ms}ms", false);
            }
            case "done":
                return (string.IsNullOrWhiteSpace(a.Summary) ? "任务完成。" : a.Summary!, true);
            default:
                return ($"未知动作：{a.Action}", false);
        }
    }

    /// <summary>简单等待页面正文非空且稳定（最多 15 秒）。</summary>
    private async Task WaitForPageAsync(CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        string? last = null;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(700, ct);
            var cur = await _web.GetContentAsync();
            if (!string.IsNullOrWhiteSpace(cur) && cur == last) return;
            last = cur;
        }
    }

    /// <summary>从模型回复中提取首个 JSON 对象并解析为动作。</summary>
    private static BrowserAction? ParseAction(string reply)
    {
        if (string.IsNullOrWhiteSpace(reply)) return null;
        var text = reply.Trim();

        // 去掉整体 Markdown 代码围栏
        if (text.StartsWith("```", StringComparison.Ordinal))
        {
            var firstNl = text.IndexOf('\n');
            if (firstNl >= 0) text = text[(firstNl + 1)..];
            var last = text.LastIndexOf("```", StringComparison.Ordinal);
            if (last >= 0) text = text[..last];
            text = text.Trim();
        }

        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start < 0 || end <= start) return null;

        try
        {
            using var doc = JsonDocument.Parse(text[start..(end + 1)]);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (!root.TryGetProperty("action", out var a) || a.ValueKind != JsonValueKind.String) return null;

            return new BrowserAction
            {
                Action = (a.GetString() ?? string.Empty).Trim().ToLowerInvariant(),
                Url = GetString(root, "url"),
                Selector = GetString(root, "selector"),
                Text = GetString(root, "text"),
                Summary = GetString(root, "summary"),
                Ms = root.TryGetProperty("ms", out var m) && m.TryGetInt32(out var mv) ? mv : 1000
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? GetString(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    /// <summary>浏览器动作描述。</summary>
    private sealed class BrowserAction
    {
        public string Action { get; init; } = string.Empty;
        public string? Url { get; init; }
        public string? Selector { get; init; }
        public string? Text { get; init; }
        public string? Summary { get; init; }
        public int Ms { get; init; }

        public string Describe() => Action switch
        {
            "navigate" => $"navigate {Url}",
            "click" => $"click {Selector}",
            "type" => $"type {Selector}（{Text?.Length ?? 0} 字）",
            "read" => $"read {Selector}",
            "readpage" => "readpage",
            "wait" => $"wait {Ms}ms",
            "done" => "done",
            _ => Action
        };
    }
}
