using AI_Tool_Box.Models;
using Serilog;
using System.Text;

namespace AI_Tool_Box.Services;

/// <summary>自动答题进度快照。</summary>
public sealed class AutoAnswerProgress
{
    /// <summary>当前提示信息。</summary>
    public string Message { get; init; } = string.Empty;

    /// <summary>当前题号（如 1001）。</summary>
    public int ProblemNumber { get; init; }

    /// <summary>已完成题数。</summary>
    public int DoneCount { get; init; }

    /// <summary>本次总题数。</summary>
    public int Total { get; init; }

    /// <summary>生成的代码（可选）。</summary>
    public string? Code { get; init; }

    /// <summary>判题结果文本（可选）。</summary>
    public string? Result { get; init; }

    /// <summary>是否为错误/异常提示。</summary>
    public bool IsError { get; init; }
}

/// <summary>离线自动答题服务契约。</summary>
public interface IAutoAnswerService
{
    /// <summary>是否有任务在运行。</summary>
    bool IsRunning { get; }

    /// <summary>离线模式：按题组规律逐题「取题面 → 本机模型生成代码 → 填入 → 提交 → 读结果」，跑够题数自动停止。</summary>
    /// <param name="site">站点配置（含各区域选择器）。</param>
    /// <param name="ollamaModel">本机 Ollama 模型名。</param>
    /// <param name="ollamaBaseUrl">本机 Ollama 地址。</param>
    /// <param name="runCount">本次题数上限。</param>
    /// <param name="progress">进度回调。</param>
    /// <param name="ct">取消令牌。</param>
    Task RunOfflineAsync(
        AutoAnswerSiteProfile site,
        string ollamaModel,
        string ollamaBaseUrl,
        int runCount,
        IProgress<AutoAnswerProgress> progress,
        CancellationToken ct = default);
}

/// <summary>离线自动答题编排服务：通过 WebView2 JS 注入读写页面 + 本机 Ollama 生成代码。</summary>
public sealed class AutoAnswerService : IAutoAnswerService
{
    private readonly ILogger _logger;
    private readonly IWebShellService _web;
    private readonly IOllamaService _ollama;
    private int _running;

    /// <summary>初始化。</summary>
    public AutoAnswerService(ILogger logger, IWebShellService web, IOllamaService ollama)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _web = web ?? throw new ArgumentNullException(nameof(web));
        _ollama = ollama ?? throw new ArgumentNullException(nameof(ollama));
    }

    /// <inheritdoc/>
    public bool IsRunning => Volatile.Read(ref _running) == 1;

    /// <inheritdoc/>
    public async Task RunOfflineAsync(
        AutoAnswerSiteProfile site,
        string ollamaModel,
        string ollamaBaseUrl,
        int runCount,
        IProgress<AutoAnswerProgress> progress,
        CancellationToken ct = default)
    {
        if (Interlocked.Exchange(ref _running, 1) == 1)
        {
            progress.Report(new AutoAnswerProgress { Message = "已有自动答题任务在运行。", IsError = true });
            return;
        }

        try
        {
            if (runCount < 1) runCount = 1;
            if (!string.IsNullOrWhiteSpace(ollamaBaseUrl))
            {
                _ollama.BaseUrl = ollamaBaseUrl;
            }

            for (var i = 0; i < runCount; i++)
            {
                ct.ThrowIfCancellationRequested();
                var n = site.StartIndex + i * site.Step;
                var url = BuildUrl(site.UrlTemplate, n);
                progress.Report(new AutoAnswerProgress
                {
                    Message = $"[{i + 1}/{runCount}] 打开第 {n} 题：{url}",
                    ProblemNumber = n,
                    DoneCount = i,
                    Total = runCount
                });

                if (!await _web.NavigateAsync(url))
                {
                    progress.Report(new AutoAnswerProgress { Message = $"导航失败：{url}", ProblemNumber = n, DoneCount = i, Total = runCount, IsError = true });
                    continue;
                }

                if (!await WaitForStableAsync(site.ProblemSelector, TimeSpan.FromSeconds(30), ct))
                {
                    progress.Report(new AutoAnswerProgress { Message = $"第 {n} 题页面加载超时，跳过。", ProblemNumber = n, DoneCount = i, Total = runCount, IsError = true });
                    continue;
                }

                var problem = await ReadRegionAsync(site.ProblemSelector, ct) ?? string.Empty;
                if (problem.Length > 9000) problem = problem[..9000];
                if (string.IsNullOrWhiteSpace(problem))
                {
                    progress.Report(new AutoAnswerProgress { Message = $"第 {n} 题未读取到题面（可检查「题目区域」选择器）。", ProblemNumber = n, DoneCount = i, Total = runCount, IsError = true });
                    continue;
                }

                progress.Report(new AutoAnswerProgress { Message = $"第 {n} 题已读取题面，正在用 {ollamaModel} 生成代码…", ProblemNumber = n, DoneCount = i, Total = runCount });

                string code;
                try
                {
                    code = await GenerateCodeAsync(site.Language, problem, ollamaModel, ct);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    _logger.Error(ex, "生成代码失败（第 {N} 题）", n);
                    progress.Report(new AutoAnswerProgress { Message = $"第 {n} 题代码生成失败：{ex.Message}", ProblemNumber = n, DoneCount = i, Total = runCount, IsError = true });
                    continue;
                }

                if (string.IsNullOrWhiteSpace(code))
                {
                    progress.Report(new AutoAnswerProgress { Message = $"第 {n} 题模型返回空代码，跳过。", ProblemNumber = n, DoneCount = i, Total = runCount, IsError = true });
                    continue;
                }

                progress.Report(new AutoAnswerProgress { Message = $"第 {n} 题已生成代码（{code.Length} 字），填入编辑器…", ProblemNumber = n, DoneCount = i, Total = runCount, Code = code });

                // 注意：填入前先“点一下”代码输入区（模拟鼠标单击 + 聚焦），把光标重新聚焦到编辑器；
                // 很多在线编辑器（CodeMirror / Ace 等）在未获焦点时直接用脚本赋值不生效。
                if (!string.IsNullOrWhiteSpace(site.TypeSelector))
                {
                    await _web.ClickAsync(site.TypeSelector);
                    await Task.Delay(120, ct);
                }

                if (string.IsNullOrWhiteSpace(site.TypeSelector) || !await _web.SetValueAsync(site.TypeSelector, code))
                {
                    progress.Report(new AutoAnswerProgress { Message = $"第 {n} 题代码填入失败（检查「代码输入区」选择器）。", ProblemNumber = n, DoneCount = i, Total = runCount, IsError = true });
                    continue;
                }

                var beforeResult = await ReadRegionAsync(site.ErrorRegionSelector, ct);
                await Task.Delay(400, ct);

                progress.Report(new AutoAnswerProgress { Message = $"第 {n} 题点击提交…", ProblemNumber = n, DoneCount = i, Total = runCount });
                if (string.IsNullOrWhiteSpace(site.SubmitSelector) || !await _web.ClickAsync(site.SubmitSelector))
                {
                    progress.Report(new AutoAnswerProgress { Message = $"第 {n} 题提交按钮未找到或点击失败（检查「提交按钮」选择器）。", ProblemNumber = n, DoneCount = i, Total = runCount, IsError = true });
                    continue;
                }

                var result = await WaitForChangedAsync(site.ErrorRegionSelector, beforeResult, TimeSpan.FromSeconds(35), ct);
                progress.Report(new AutoAnswerProgress
                {
                    Message = $"第 {n} 题判题结果：{result}",
                    ProblemNumber = n,
                    DoneCount = i + 1,
                    Total = runCount,
                    Result = result
                });

                await Task.Delay(800, ct);
            }

            progress.Report(new AutoAnswerProgress { Message = $"已完成 {runCount} 题，自动停止。", DoneCount = runCount, Total = runCount });
        }
        catch (OperationCanceledException)
        {
            progress.Report(new AutoAnswerProgress { Message = "已停止。", IsError = true });
        }
        finally
        {
            Interlocked.Exchange(ref _running, 0);
        }
    }

    /// <summary>把题号套入 URL 规律（含 {n} 则替换，否则拼接）。</summary>
    private static string BuildUrl(string template, int n)
    {
        if (string.IsNullOrWhiteSpace(template)) return string.Empty;
        return template.Contains("{n}", StringComparison.Ordinal)
            ? template.Replace("{n}", n.ToString(), StringComparison.Ordinal)
            : template + n;
    }

    /// <summary>读取指定区域文本；选择器为空时退回整页正文。</summary>
    private async Task<string?> ReadRegionAsync(string selector, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return string.IsNullOrWhiteSpace(selector)
            ? await _web.GetContentAsync()
            : await _web.ReadTextAsync(selector);
    }

    /// <summary>轮询直到某区域文本非空且连续两次一致（视为页面加载完成）。</summary>
    private async Task<bool> WaitForStableAsync(string selector, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        string? last = null;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(600, ct);
            var cur = await ReadRegionAsync(selector, ct);
            if (!string.IsNullOrWhiteSpace(cur) && cur == last) return true;
            last = cur;
        }
        return false;
    }

    /// <summary>轮询直到某区域文本变化且稳定（视为判题结果已出现）。</summary>
    private async Task<string> WaitForChangedAsync(string selector, string? before, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        string? last = null;
        string? cur = before;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(700, ct);
            cur = await ReadRegionAsync(selector, ct);
            if (!string.IsNullOrWhiteSpace(cur) && cur != before && cur == last) return cur!;
            last = cur;
        }
        return string.IsNullOrWhiteSpace(cur) ? "(未检测到判题结果)" : cur!;
    }

    /// <summary>调用本机 Ollama 生成代码，并剥离可能的 Markdown 围栏。</summary>
    private async Task<string> GenerateCodeAsync(string language, string problem, string model, CancellationToken ct)
    {
        var prompt =
            $"你是算法竞赛选手。请阅读下面的题目，用 {(string.IsNullOrWhiteSpace(language) ? "C++" : language)} 编写一份完整、可直接提交并通过的解答代码。\n" +
            "要求：只输出代码本体，不要任何解释文字，不要使用 Markdown 代码围栏（不要 ```）。\n\n" +
            "题目：\n" + problem;

        var sb = new StringBuilder();
        await _ollama.GenerateStreamAsync(model, prompt, chunk => sb.Append(chunk), ct);
        return StripCodeFences(sb.ToString());
    }

    /// <summary>去掉 ```lang ... ``` 围栏，仅保留代码。</summary>
    private static string StripCodeFences(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        var t = text.Trim();
        if (!t.StartsWith("```", StringComparison.Ordinal)) return t;

        var firstNl = t.IndexOf('\n');
        if (firstNl >= 0) t = t[(firstNl + 1)..];
        var lastFence = t.LastIndexOf("```", StringComparison.Ordinal);
        if (lastFence >= 0) t = t[..lastFence];
        return t.Trim();
    }
}
