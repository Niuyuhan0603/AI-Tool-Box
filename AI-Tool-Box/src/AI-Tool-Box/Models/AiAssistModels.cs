namespace AI_Tool_Box.Models;

/// <summary>右侧栏 AI 辅助的提供商配置档案（API-Key 不落此文件，单独存 DPAPI 密钥库）。</summary>
public sealed class AiAssistProfile
{
    /// <summary>唯一 ID（持久化与 DPAPI Key 名关联：assist:{Id}）。</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>显示名称（如 "OpenAI"、"DeepSeek"、"本地 Ollama"）。</summary>
    public string DisplayName { get; set; } = "OpenAI 兼容";

    /// <summary>协议类型："openai" = OpenAI 兼容 chat/completions；"anthropic" = Anthropic /v1/messages。</summary>
    public string Kind { get; set; } = "openai";

    /// <summary>API 基地址（OpenAI 兼容含 /v1；Anthropic 填根地址）。</summary>
    public string BaseUrl { get; set; } = "https://api.openai.com/v1";

    /// <summary>默认模型标识（可在侧栏临时修改）。</summary>
    public string DefaultModel { get; set; } = "gpt-4o-mini";

    /// <summary>是否免鉴权（本地服务如 Ollama，无需 API-Key）。</summary>
    public bool NoKeyRequired { get; set; }

    /// <summary>会话 ID：同一 ID 的请求在网关/代理侧归为同一会话（留空则自动生成）。</summary>
    public string SessionId { get; set; } = Guid.NewGuid().ToString("N")[..12];
}

/// <summary>主流 AI 厂商预填充信息（用户只填 API-Key 即可使用）。</summary>
public sealed class AiProviderPreset
{
    /// <summary>稳定标识（用于生成档案 Id 与 API-Key 存储名）。</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>厂商显示名（同时作为档案显示名）。</summary>
    public string DisplayName { get; init; } = string.Empty;

    /// <summary>协议类型："openai" / "anthropic"。</summary>
    public string Kind { get; init; } = "openai";

    /// <summary>API 基地址（OpenAI 兼容含 /v1；Anthropic 为根地址）。</summary>
    public string BaseUrl { get; init; } = string.Empty;

    /// <summary>该厂商可选模型列表（第一个为默认）。</summary>
    public IReadOnlyList<string> Models { get; init; } = Array.Empty<string>();

    /// <summary>是否免鉴权（本地服务）。</summary>
    public bool NoKeyRequired { get; init; }

    /// <summary>API-Key 申请/控制台地址（界面提示用）。</summary>
    public string ApplyUrl { get; init; } = string.Empty;

    /// <summary>补充说明（界面提示用）。</summary>
    public string Note { get; init; } = string.Empty;

    /// <inheritdoc/>
    public override string ToString() => DisplayName;
}

/// <summary>主流厂商预填充表（2026 年常见服务；模型可自行改成账号可用名称）。</summary>
public static class AiProviderPresets
{
    /// <summary>全部预置厂商。</summary>
    public static IReadOnlyList<AiProviderPreset> All { get; } =
    [
        new()
        {
            Id = "deepseek",
            DisplayName = "DeepSeek（官方）",
            BaseUrl = "https://api.deepseek.com/v1",
            Models = ["deepseek-chat", "deepseek-reasoner"],
            ApplyUrl = "https://platform.deepseek.com/api_keys",
            Note = "deepseek-chat = 通用对话；deepseek-reasoner = 深度思考（R1）"
        },
        new()
        {
            Id = "openai",
            DisplayName = "OpenAI",
            BaseUrl = "https://api.openai.com/v1",
            Models = ["gpt-4.1", "gpt-4.1-mini", "gpt-4o", "gpt-4o-mini", "o4-mini"],
            ApplyUrl = "https://platform.openai.com/api-keys"
        },
        new()
        {
            Id = "anthropic",
            DisplayName = "Anthropic Claude",
            Kind = "anthropic",
            BaseUrl = "https://api.anthropic.com",
            Models = ["claude-sonnet-4-5", "claude-opus-4-1", "claude-3-5-haiku-latest"],
            ApplyUrl = "https://console.anthropic.com/settings/keys"
        },
        new()
        {
            Id = "gemini",
            DisplayName = "Google Gemini（OpenAI 兼容）",
            BaseUrl = "https://generativelanguage.googleapis.com/v1beta/openai",
            Models = ["gemini-2.5-pro", "gemini-2.5-flash", "gemini-2.5-flash-lite"],
            ApplyUrl = "https://aistudio.google.com/apikey"
        },
        new()
        {
            Id = "qwen",
            DisplayName = "阿里云通义千问（DashScope）",
            BaseUrl = "https://dashscope.aliyuncs.com/compatible-mode/v1",
            Models = ["qwen-max", "qwen-plus", "qwen-turbo", "qwen3-max"],
            ApplyUrl = "https://bailian.console.aliyun.com/",
            Note = "需要百炼/DashScope 的 API-Key（sk- 开头）"
        },
        new()
        {
            Id = "kimi",
            DisplayName = "月之暗面 Kimi",
            BaseUrl = "https://api.moonshot.cn/v1",
            Models = ["kimi-k2-0905-preview", "kimi-k2-turbo-preview",
                "moonshot-v1-128k", "moonshot-v1-32k", "moonshot-v1-8k"],
            ApplyUrl = "https://platform.moonshot.cn/console/api-keys"
        },
        new()
        {
            Id = "glm",
            DisplayName = "智谱 GLM",
            BaseUrl = "https://open.bigmodel.cn/api/paas/v4",
            Models = ["glm-4.5", "glm-4.5-air", "glm-4-plus", "glm-4-flash"],
            ApplyUrl = "https://open.bigmodel.cn/usercenter/apikeys"
        },
        new()
        {
            Id = "qianfan",
            DisplayName = "百度千帆（文心 / 千帆大模型）",
            BaseUrl = "https://qianfan.baidubce.com/v2",
            Models = ["ernie-4.5-turbo-128k", "ernie-4.5-8k-preview", "deepseek-v3",
                "ernie-4.0-8k", "ernie-speed-8k"],
            ApplyUrl = "https://console.bce.baidu.com/iam/#/iam/apikey/list",
            Note = "千帆 v2 为 OpenAI 兼容端点，API-Key 填 IAM 的 API Key（也可在控制台创建应用获取）"
        },
        new()
        {
            Id = "siliconflow",
            DisplayName = "硅基流动 SiliconFlow",
            BaseUrl = "https://api.siliconflow.cn/v1",
            Models = ["deepseek-ai/DeepSeek-V3.2", "Qwen/Qwen3-235B-A22B-Instruct-2507",
                "moonshotai/Kimi-K2-Instruct-0905"],
            ApplyUrl = "https://cloud.siliconflow.cn/account/ak"
        },
        new()
        {
            Id = "openrouter",
            DisplayName = "OpenRouter（聚合网关）",
            BaseUrl = "https://openrouter.ai/api/v1",
            Models = ["openai/gpt-4.1-mini", "anthropic/claude-sonnet-4.5",
                "deepseek/deepseek-chat", "google/gemini-2.5-flash"],
            ApplyUrl = "https://openrouter.ai/keys",
            Note = "一个 Key 可用多家模型，模型名按 OpenRouter 命名"
        },
        new()
        {
            Id = "xai",
            DisplayName = "xAI Grok",
            BaseUrl = "https://api.x.ai/v1",
            Models = ["grok-4", "grok-3", "grok-3-mini"],
            ApplyUrl = "https://console.x.ai/"
        },
        new()
        {
            Id = "doubao",
            DisplayName = "火山方舟 豆包",
            BaseUrl = "https://ark.cn-beijing.volces.com/api/v3",
            Models = ["doubao-pro-32k"],
            ApplyUrl = "https://console.volcengine.com/ark",
            Note = "方舟通常要求填写推理接入点 ID（形如 ep-xxxx）作为模型名"
        },
        new()
        {
            Id = "ollama",
            DisplayName = "本地 Ollama（免鉴权）",
            BaseUrl = "http://localhost:11434/v1",
            // 不预填假模型名：选中该厂商时会从本机 /api/tags 读取实际已安装模型
            Models = Array.Empty<string>(),
            NoKeyRequired = true,
            Note = "先在本机运行 ollama serve；模型列表会自动读取本机已 pull 的模型"
        }
    ];
}

/// <summary>右侧栏 AI 辅助持久化设置（不含 API-Key）。</summary>
public sealed class AiAssistSettings
{
    /// <summary>提供商档案列表。</summary>
    public List<AiAssistProfile> Profiles { get; set; } = CreateDefaultProfiles();

    /// <summary>当前选中的档案 Id。</summary>
    public string SelectedProfileId { get; set; } = string.Empty;

    /// <summary>生成默认档案集（Key 均留空，由用户在设置页自行填写）。</summary>
    public static List<AiAssistProfile> CreateDefaultProfiles() =>
    [
        new()
        {
            DisplayName = "OpenAI",
            Kind = "openai",
            BaseUrl = "https://api.openai.com/v1",
            DefaultModel = "gpt-4o-mini"
        },
        new()
        {
            DisplayName = "DeepSeek",
            Kind = "openai",
            BaseUrl = "https://api.deepseek.com/v1",
            DefaultModel = "deepseek-chat"
        },
        new()
        {
            DisplayName = "Anthropic",
            Kind = "anthropic",
            BaseUrl = "https://api.anthropic.com",
            DefaultModel = "claude-sonnet-4-5"
        },
        new()
        {
            DisplayName = "Gemini（OpenAI 兼容端点）",
            Kind = "openai",
            BaseUrl = "https://generativelanguage.googleapis.com/v1beta/openai",
            DefaultModel = "gemini-2.5-flash"
        },
        new()
        {
            DisplayName = "本地 Ollama（免鉴权）",
            Kind = "openai",
            BaseUrl = "http://localhost:11434/v1",
            // 留空：首次选中档案时从本机 /api/tags 读取实际已安装模型并自动填入
            DefaultModel = string.Empty,
            NoKeyRequired = true
        }
    ];
}

/// <summary>聊天消息轮次（发送给模型的历史）。</summary>
public sealed class ChatTurn
{
    /// <summary>角色："user" / "assistant"。</summary>
    public string Role { get; init; } = "user";

    /// <summary>消息内容。</summary>
    public string Content { get; init; } = string.Empty;
}

/// <summary>侧栏常用提示词：普通项插入输入框；Action 项（如 compress）触发对应动作。</summary>
public sealed class AiAssistPromptPreset
{
    /// <summary>下拉显示名称。</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>插入输入框的提示词文本（Action 项可为空）。</summary>
    public string Prompt { get; init; } = string.Empty;

    /// <summary>动作标识：空=插入文本；"compress"=压缩历史对话。</summary>
    public string Action { get; init; } = string.Empty;

    /// <summary>是否为动作项（不插入文本）。</summary>
    public bool IsAction => !string.IsNullOrEmpty(Action);

    /// <inheritdoc/>
    public override string ToString() => Name;
}

/// <summary>侧栏常用提示词预设表。</summary>
public static class AiAssistPromptPresets
{
    /// <summary>全部预设（按使用频率排序）。</summary>
    public static IReadOnlyList<AiAssistPromptPreset> Common { get; } =
    [
        new()
        {
            Name = "依据页面内容写代码",
            Prompt = "请依据本轮附带（或上文）用户选取的网页片段（结构化 HTML），用 WinUI 3（XAML + C#，MVVM）复刻该区域的界面与交互。"
                   + "要求：给出可直接编译的完整代码，说明每个文件放哪里、关键实现点是什么；不要臆造片段里不存在的功能。"
        },
        new()
        {
            Name = "总结网页要点",
            Prompt = "请把附带的网页片段整理成中文要点摘要：先用一句话概括它讲什么，再分条列出关键信息（数字、时间、结论）。"
                   + "只依据片段内容，缺失的信息标注“未提及”。"
        },
        new()
        {
            Name = "解释这段页面",
            Prompt = "请解释附带网页片段的用途与信息结构：它是页面的哪个区域、由哪些子模块组成、每个模块的作用是什么，并指出可复用的 UI 模式。"
        },
        new()
        {
            Name = "找出页面问题并修复",
            Prompt = "请检查附带网页片段是否有 HTML 结构错误、无障碍问题（ARIA/对比度/可聚焦性）和文案问题，逐条说明并提出修正后的最小改动片段。"
        },
        new()
        {
            Name = "下一步该做什么（Agent）",
            Prompt = "请作为编码 Agent：阅读上面的对话与附带片段，先总结当前进展，再给出接下来 3~5 步可执行计划（每步写明文件路径与改动点），并标注哪些已完成的工作不要重复。"
        },
        new()
        {
            Name = "生成单元测试",
            Prompt = "请针对上面的代码或片段补写单元测试（xUnit），覆盖正常路径、边界条件和失败路径；给出测试文件路径、必需的引用与可直接运行的测试代码。"
        },
        new()
        {
            Name = "压缩为简洁文案",
            Prompt = "请把附带片段中的正文改写成 3~5 句简洁文案，保留关键事实与数字，去掉营销套话；输出中文。"
        },
        new()
        {
            Name = "翻译为英文",
            Prompt = "请把附带片段中的正文翻译成地道英文，保留原有结构与专有名词；先给译文，再列出 3 个值得注意的术语对照。"
        },
        new()
        {
            Name = "压缩历史对话",
            Prompt = string.Empty,
            Action = "compress"
        }
    ];
}
