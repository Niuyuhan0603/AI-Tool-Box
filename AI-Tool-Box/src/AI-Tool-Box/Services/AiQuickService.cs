using AI_Tool_Box.Models;
using Serilog;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;

namespace AI_Tool_Box.Services;

/// <summary>一次性 AI 调用（复用右侧栏 AI 辅助的提供商档案与 DPAPI 密钥）；供各功能页做"AI 辅助"。</summary>
public interface IAiQuickService
{
    /// <summary>解析当前可用档案（选中档案 → 有 Key/免鉴权档案 → 第一个档案）。</summary>
    AiAssistProfile? ResolveProfile();

    /// <summary>读取档案对应的 API-Key（免鉴权档案返回 null）。</summary>
    string? ResolveApiKey(AiAssistProfile profile);

    /// <summary>当前档案描述，如 "OpenAI / gpt-4o-mini"；不可用时为提示文本。</summary>
    string Describe();

    /// <summary>一次性问答：返回完整回答文本（流式回调可选）。</summary>
    Task<string> AskAsync(string instruction, string content, IProgress<string>? stream = null, CancellationToken ct = default);

    /// <summary>语音转写（POST {base}/audio/transcriptions，model=whisper-1）。</summary>
    Task<string> TranscribeAudioAsync(string audioPath, string? language = null, CancellationToken ct = default);
}

/// <summary>一次性 AI 调用实现。</summary>
public sealed class AiQuickService : IAiQuickService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(10) };

    private readonly ILogger _logger;
    private readonly IAiAssistSettingsService _settings;
    private readonly IAiAssistChatService _chat;
    private readonly IApiKeyService _keys;

    /// <summary>构造：注入设置、聊天服务与密钥库。</summary>
    public AiQuickService(ILogger logger, IAiAssistSettingsService settings, IAiAssistChatService chat, IApiKeyService keys)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _chat = chat ?? throw new ArgumentNullException(nameof(chat));
        _keys = keys ?? throw new ArgumentNullException(nameof(keys));
    }

    /// <inheritdoc/>
    public AiAssistProfile? ResolveProfile()
    {
        var list = _settings.Settings.Profiles;
        if (list.Count == 0) return null;

        var selectedId = _settings.Settings.SelectedProfileId;
        var selected = list.FirstOrDefault(p => p.Id == selectedId);
        if (selected is not null && (selected.NoKeyRequired || !string.IsNullOrWhiteSpace(ResolveApiKey(selected))))
            return selected;

        var withKey = list.FirstOrDefault(p => p.NoKeyRequired || !string.IsNullOrWhiteSpace(ResolveApiKey(p)));
        return withKey ?? selected ?? list[0];
    }

    /// <inheritdoc/>
    public string? ResolveApiKey(AiAssistProfile profile) => _keys.Load("assist:" + profile.Id);

    /// <inheritdoc/>
    public string Describe()
    {
        var p = ResolveProfile();
        return p is null ? "未配置 AI 档案" : $"{p.DisplayName} / {p.DefaultModel}";
    }

    /// <inheritdoc/>
    public async Task<string> AskAsync(string instruction, string content, IProgress<string>? stream = null, CancellationToken ct = default)
    {
        var profile = ResolveProfile() ?? throw new InvalidOperationException("未配置 AI 提供商，请到「设置 → 右侧栏AI辅助」添加档案并填写 API-Key。");
        var key = profile.NoKeyRequired ? null : ResolveApiKey(profile);
        if (!profile.NoKeyRequired && string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException($"提供商「{profile.DisplayName}」未填写 API-Key，请到「设置 → 右侧栏AI辅助」填写。");

        var turns = profile.Kind == "anthropic"
            ? new List<ChatTurn> { new() { Role = "user", Content = instruction + "\n\n" + content } }
            : new List<ChatTurn>
            {
                new() { Role = "system", Content = instruction },
                new() { Role = "user", Content = content },
            };

        var sb = new StringBuilder();
        await _chat.StreamAsync(profile, key, profile.DefaultModel, turns, delta =>
        {
            sb.Append(delta);
            stream?.Report(delta);
        }, ct);

        _logger.Information("AI 一次性调用完成：{Provider}，输出 {Chars} 字", profile.DisplayName, sb.Length);
        return sb.ToString();
    }

    /// <inheritdoc/>
    public async Task<string> TranscribeAudioAsync(string audioPath, string? language = null, CancellationToken ct = default)
    {
        var profile = ResolveProfile() ?? throw new InvalidOperationException("未配置 AI 提供商（语音转写需要 OpenAI 兼容的 /audio/transcriptions）。");
        var key = profile.NoKeyRequired ? null : ResolveApiKey(profile);
        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException("语音转写需要 OpenAI（或兼容服务）的 API-Key，请在设置中填写；也可直接导入已有字幕文件（.txt/.srt/.vtt）。");

        var baseUrl = (profile.BaseUrl ?? string.Empty).Trim().TrimEnd('/');
        var url = baseUrl + "/audio/transcriptions";
        _logger.Information("语音转写请求：{Url} 文件={File}", url, audioPath);

        using var form = new MultipartFormDataContent();
        var fileBytes = await File.ReadAllBytesAsync(audioPath, ct);
        var fileContent = new ByteArrayContent(fileBytes);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("audio/mpeg");
        form.Add(fileContent, "file", Path.GetFileName(audioPath));
        form.Add(new StringContent("whisper-1"), "model");
        if (!string.IsNullOrWhiteSpace(language)) form.Add(new StringContent(language), "language");

        using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = form };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        using var resp = await Http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"语音转写失败 HTTP {(int)resp.StatusCode}：{(body.Length > 300 ? body[..300] : body)}");
        return body;
    }
}
