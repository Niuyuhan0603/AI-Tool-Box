namespace AI_Tool_Box.Models;

/// <summary>自动答题：离线模式的题组站点配置。用户手工拾取页面区域（CSS 选择器）+ 填写题组 URL 规律。</summary>
public sealed class AutoAnswerSiteProfile
{
    /// <summary>站点档案 Id。</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>站点名称（仅展示）。</summary>
    public string Name { get; set; } = "洛谷";

    /// <summary>题组 URL 规律，用 {n} 表示题号；如 https://www.luogu.com.cn/problem/P{n}</summary>
    public string UrlTemplate { get; set; } = "https://www.luogu.com.cn/problem/P{n}";

    /// <summary>起始题号。</summary>
    public int StartIndex { get; set; } = 1001;

    /// <summary>题号步长（洛谷题号连续 → 1）。</summary>
    public int Step { get; set; } = 1;

    /// <summary>题目区域选择器（读取题面）；留空则取整页正文。</summary>
    public string ProblemSelector { get; set; } = string.Empty;

    /// <summary>代码输入区域选择器（AI 生成的代码自动填入此区域）。</summary>
    public string TypeSelector { get; set; } = string.Empty;

    /// <summary>提交代码按钮选择器。</summary>
    public string SubmitSelector { get; set; } = string.Empty;

    /// <summary>判题 / 报错结果区域选择器（提交后读取以判断结果）。</summary>
    public string ErrorRegionSelector { get; set; } = string.Empty;

    /// <summary>编程语言（提示模型用）：C++ / Python 等。</summary>
    public string Language { get; set; } = "C++";
}

/// <summary>自动答题持久化设置。</summary>
public sealed class AutoAnswerSettings
{
    /// <summary>站点档案列表。</summary>
    public List<AutoAnswerSiteProfile> Sites { get; set; } = new();

    /// <summary>当前选中站点档案 Id。</summary>
    public string SelectedSiteId { get; set; } = string.Empty;

    /// <summary>离线代码生成使用的本机 Ollama 模型名（独立于右侧栏 AI 辅助档案）。</summary>
    public string OllamaModel { get; set; } = string.Empty;

    /// <summary>本机 Ollama 基地址。</summary>
    public string OllamaBaseUrl { get; set; } = "http://localhost:11434";

    /// <summary>本次运行题数上限（跑完自动停止）。</summary>
    public int RunCount { get; set; } = 1;

    /// <summary>联网模式下浏览器控制器的最大步数。</summary>
    public int MaxSteps { get; set; } = 30;
}

/// <summary>区域拾取结果：区域身份（CSS 选择器）+ 供用户确认的结构化描述（不含区域内的文本内容）。
/// 自动答题的四个拾取按钮只“借鉴”页面拾取的交互（悬停高亮 + 单击），仅取“是哪个区域”——
/// 因为同一站点的每题 HTML 布局相同（选择器可复用），而区域内的文字每题都不同（不可固化）。</summary>
public sealed record RegionPickResult(string Selector, string Label);
