using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text;
using AI_Tool_Box.Models;
using AI_Tool_Box.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Serilog;

namespace AI_Tool_Box.ViewModels;

/// <summary>导航消息：请求主窗口导航到指定 Tag（用于"前往设置"等跨层跳转）。</summary>
public sealed record NavigateToTagMessage(string Tag);

/// <summary>侧栏聊天消息行（UI 展示用）。</summary>
public sealed partial class AiAssistMsgRow : ObservableObject
{
    /// <summary>角色："user" / "assistant"。</summary>
    public string Role { get; init; } = "user";

    /// <summary>是否用户消息（控制气泡对齐）。</summary>
    public bool IsUser => Role == "user";

    [ObservableProperty] private string _text = string.Empty;

    /// <summary>是否正在流式生成（控制「AI 生成中」动画）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GeneratingVisibility))]
    private bool _isGenerating;

    /// <summary>生成中提示文案（随接收字数实时变化，证明内容正在到达）。</summary>
    [ObservableProperty] private string _receivingText = "AI 生成中…";

    /// <summary>生成中指示器的可见性（生成结束即隐藏）。</summary>
    public Visibility GeneratingVisibility => IsGenerating ? Visibility.Visible : Visibility.Collapsed;
}

/// <summary>右侧栏 AI 辅助视图模型：多模型 BYOK 聊天 + 当前网页正文问答。</summary>
public sealed partial class AiAssistViewModel : ObservableObject
{
    private readonly ILogger _logger;
    private readonly IAiAssistSettingsService _settingsService;
    private readonly IAiAssistChatService _chat;
    private readonly IApiKeyService _keyStore;
    private readonly IUsageTracker _usage;
    private readonly DispatcherQueue _dispatcher;
    private CancellationTokenSource? _cts;

    /// <summary>API-Key 缺失时触发（View 弹窗引导前往设置）。</summary>
    public event EventHandler<AiAssistProfile>? MissingKeyRequested;

    /// <summary>用户请求关闭侧栏（View 收起面板并复位开关）。</summary>
    public event EventHandler? CloseRequested;

    /// <summary>流式内容已刷新到 UI（View 据此在用户接近底部时跟随滚动）。</summary>
    public event EventHandler? AnswerUpdated;

    /// <summary>聊天消息列表。</summary>
    public ObservableCollection<AiAssistMsgRow> Messages { get; } = new();

    /// <summary>提供商档案列表（ObservableCollection；设置页增删档案后调用 RefreshProfiles 同步）。</summary>
    public ObservableCollection<AiAssistProfile> Profiles { get; } = new();

    /// <summary>当前提供商可用的模型候选：本地免鉴权服务（Ollama）会从 /api/tags 读取实际已安装模型。</summary>
    public ObservableCollection<string> AvailableModels { get; } = new();

    /// <summary>操作反馈（输入框上方一行提示）。</summary>
    [ObservableProperty] private string _statusHint = "选择提供商与模型即可开始对话；API-Key 在 设置 → 右侧栏AI辅助 中填写。";

    /// <summary>当前选中档案。</summary>
    [ObservableProperty] private AiAssistProfile? _selectedProfile;

    /// <summary>当前模型标识（可临时修改，发送后按档案记忆）。</summary>
    [ObservableProperty] private string _model = string.Empty;

    /// <summary>用户从网页选取的结构化 HTML 片段（随提问附加，空则不附加）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AttachedVisibility))]
    private string _attachedHtml = string.Empty;

    /// <summary>已附带片段的摘要文案（侧栏显示）。</summary>
    [ObservableProperty] private string _attachedSummary = "尚未附带网页片段。";

    /// <summary>是否已有附带片段（控制“清除”按钮可见性）。</summary>
    public Visibility AttachedVisibility => string.IsNullOrEmpty(AttachedHtml) ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>常用提示词预设列表。</summary>
    public ObservableCollection<AiAssistPromptPreset> PromptPresets { get; } = new();

    /// <summary>当前选中的提示词预设（选中即插入输入框；动作项直接执行）。</summary>
    [ObservableProperty] private AiAssistPromptPreset? _selectedPromptPreset;

    /// <summary>输入框内容。</summary>
    [ObservableProperty] private string _input = string.Empty;

    /// <summary>是否正在流式接收（控制发送/停止按钮态）。</summary>
    [ObservableProperty] private bool _isBusy;

    /// <summary>当前档案是否已保存 Key。</summary>
    [ObservableProperty] private bool _hasSavedKey;

    /// <summary>Key 状态提示文案（侧栏显示）。</summary>
    [ObservableProperty] private string _keyStatusText = string.Empty;

    /// <summary>初始化：从设置服务载入档案与上次选择。</summary>
    public AiAssistViewModel(
        ILogger logger,
        IAiAssistSettingsService settingsService,
        IAiAssistChatService chat,
        IApiKeyService keyStore,
        IUsageTracker usage)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
        _chat = chat ?? throw new ArgumentNullException(nameof(chat));
        _keyStore = keyStore ?? throw new ArgumentNullException(nameof(keyStore));
        _usage = usage ?? throw new ArgumentNullException(nameof(usage));
        _dispatcher = DispatcherQueue.GetForCurrentThread();

        var s = _settingsService.Settings;
        foreach (var preset in AiAssistPromptPresets.Common) PromptPresets.Add(preset);
        LoadProfiles();
        SelectedProfile = Profiles.FirstOrDefault(p => p.Id == s.SelectedProfileId) ?? Profiles.FirstOrDefault();
        if (SelectedProfile is not null) Model = SelectedProfile.DefaultModel;
        RefreshHasSavedKey();
    }

    private void LoadProfiles()
    {
        Profiles.Clear();
        foreach (var p in _settingsService.Settings.Profiles)
        {
            Profiles.Add(p);
        }
    }

    /// <summary>设置页新增/删除/保存档案后调用：重载下拉列表并校正选中项。</summary>
    public void RefreshProfiles()
    {
        LoadProfiles();
        if (SelectedProfile is null || Profiles.All(p => p.Id != SelectedProfile.Id))
        {
            SelectedProfile = Profiles.FirstOrDefault();
        }
        else
        {
            _ = RefreshModelsAsync(SelectedProfile);
        }
    }

    partial void OnSelectedProfileChanged(AiAssistProfile? value)
    {
        if (value is null) return;
        Model = value.DefaultModel;
        RefreshHasSavedKey();
        // 记住用户上次选择
        _settingsService.Settings.SelectedProfileId = value.Id;
        _settingsService.Save();
        // 本地 Ollama 等免鉴权服务：读实际已安装模型，并纠正档案里不存在的默认模型（如预填的 qwen3:8b）
        _ = RefreshModelsAsync(value);
    }

    /// <summary>读取当前提供商的实际模型列表（仅本地免鉴权服务有效；失败时静默保留手填值）。</summary>
    private async Task RefreshModelsAsync(AiAssistProfile profile)
    {
        IReadOnlyList<string> models = Array.Empty<string>();
        if (profile.NoKeyRequired)
        {
            models = await OllamaService.ListModelsAtAsync(profile.BaseUrl);
        }
        if (!_dispatcher.HasThreadAccess)
        {
            _dispatcher.TryEnqueue(() => ApplyModels(profile, models));
            return;
        }
        ApplyModels(profile, models);
    }

    /// <summary>手动刷新当前提供商的实际模型列表（侧栏“刷新”按钮）。</summary>
    public void RefreshModels()
    {
        if (SelectedProfile is null) return;
        _ = RefreshModelsAsync(SelectedProfile);
    }

    private void ApplyModels(AiAssistProfile profile, IReadOnlyList<string> models)
    {
        if (SelectedProfile is null || SelectedProfile.Id != profile.Id) return;
        AvailableModels.Clear();
        foreach (var m in models) AvailableModels.Add(m);
        if (models.Count == 0) return;

        var changed = false;
        if (string.IsNullOrWhiteSpace(Model) || !models.Contains(Model))
        {
            Model = models[0];
            StatusHint = $"已从本机服务读取 {models.Count} 个模型，当前使用 {Model}。";
            changed = true;
        }
        if (profile.DefaultModel != Model)
        {
            profile.DefaultModel = Model;
            changed = true;
        }
        if (changed) _settingsService.Save();
        _logger.Information("本地模型列表已加载：{Count} 个（{Profile}）", models.Count, profile.DisplayName);
    }

    /// <summary>重算 Key 状态提示（设置页保存/清除 Key 后由外部调用以同步侧栏显示）。</summary>
    public void RefreshHasSavedKey()
    {
        HasSavedKey = SelectedProfile is null || SelectedProfile.NoKeyRequired ||
                      !string.IsNullOrEmpty(_keyStore.Load(KeyName(SelectedProfile)));
        KeyStatusText = SelectedProfile switch
        {
            null => "未选择提供商。",
            { NoKeyRequired: true } => $"当前提供商“{SelectedProfile.DisplayName}”免鉴权，无需 API-Key。",
            _ => HasSavedKey
                ? $"当前提供商“{SelectedProfile.DisplayName}”已保存 API-Key。"
                : $"当前提供商“{SelectedProfile.DisplayName}”尚未填写 API-Key（发起请求时会提示）。"
        };
    }

    private static string KeyName(AiAssistProfile p) => $"assist:{p.Id}";

    partial void OnSelectedPromptPresetChanged(AiAssistPromptPreset? value)
    {
        // 复位选中项，便于重复点选同一预设
        SelectedPromptPreset = null;
        if (value is null) return;

        if (value.IsAction)
        {
            if (value.Action == "compress") CompressHistoryCommand.Execute(null);
            return;
        }
        Input = value.Prompt;
        StatusHint = $"已插入提示词「{value.Name}」，可补充细节后发送。";
    }

    /// <summary>附带用户从网页选取的结构化片段（由“选取网页内容”弹窗回填）。</summary>
    /// <param name="html">已截断的结构化 HTML 片段。</param>
    /// <param name="summary">摘要文案（如“2 个区块，约 3.2k 字符”）。</param>
    public void AttachPageContext(string html, string summary)
    {
        AttachedHtml = html ?? string.Empty;
        AttachedSummary = string.IsNullOrEmpty(AttachedHtml) ? "尚未附带网页片段。" : $"已附带：{summary}";
        StatusHint = string.IsNullOrEmpty(AttachedHtml) ? "未附带网页片段。" : "已附带网页片段，下次发送时一并传给 AI。";
    }

    /// <summary>清除已附带的网页片段。</summary>
    [RelayCommand]
    private void ClearAttached()
    {
        AttachedHtml = string.Empty;
        AttachedSummary = "尚未附带网页片段。";
        StatusHint = "已清除附带的网页片段。";
    }

    /// <summary>发送当前输入（Ctrl+Enter 或点击发送）。</summary>
    [RelayCommand]
    private async Task SendAsync()
    {
        if (IsBusy) return;
        var profile = SelectedProfile;
        if (profile is null)
        {
            StatusHint = "没有可用提供商：请在 设置 → 右侧栏AI辅助 中添加。";
            return;
        }
        if (string.IsNullOrWhiteSpace(Input))
        {
            StatusHint = "请输入内容后再发送。";
            return;
        }

        // 未填写 Key：弹窗引导（不发请求）
        if (!profile.NoKeyRequired && string.IsNullOrEmpty(_keyStore.Load(KeyName(profile))))
        {
            StatusHint = $"“{profile.DisplayName}”尚未填写 API-Key。";
            MissingKeyRequested?.Invoke(this, profile);
            return;
        }
        if (string.IsNullOrWhiteSpace(Model))
        {
            StatusHint = "请填写模型标识（如 gpt-4o-mini）。";
            return;
        }

        // 组装本轮用户消息：只附带用户选取的结构化片段（不再把整页内容倒给 AI）
        var userText = Input.Trim();
        Input = string.Empty;
        if (!string.IsNullOrEmpty(AttachedHtml))
        {
            userText += "\n\n[用户选取的网页片段（结构化 HTML）]\n" + AttachedHtml;
        }

        Messages.Add(new AiAssistMsgRow { Role = "user", Text = userText });

        var turns = Messages.Select(m => new ChatTurn { Role = m.Role, Content = m.Text }).ToList();
        var answer = new AiAssistMsgRow { Role = "assistant", Text = string.Empty, IsGenerating = true };
        Messages.Add(answer);

        IsBusy = true;
        StatusHint = $"正在请求 {profile.DisplayName} · {Model}…";
        _cts = new CancellationTokenSource();
        var promptChars = turns.Sum(t => (long)t.Content.Length);
        var completionChars = 0L;

        // 流式呈现：后台线程只追加缓冲，UI 线程按“是否已有待刷新”做即时合并刷新。
        // 不用固定 80ms 定时器：增量一到就把刷新排进队（同一时刻最多一个待执行），
        // 既避免逐 token 拥塞 UI 队列，也让文字像网页端那样持续滚动出现。
        var builder = new StringBuilder();
        var flushGate = new object();
        var flushPending = false;
        var firstDeltaMs = -1L;
        var deltaBlocks = 0;
        var sw = Stopwatch.StartNew();

        void Flush()
        {
            string snapshot;
            int blocks;
            lock (builder)
            {
                snapshot = builder.ToString();
                blocks = deltaBlocks;
            }
            if (!string.Equals(answer.Text, snapshot, StringComparison.Ordinal)) answer.Text = snapshot;
            answer.ReceivingText = blocks == 0
                ? "AI 生成中 · 等待模型响应…"
                : $"AI 生成中 · 已接收 {snapshot.Length} 字 · {blocks} 段";
            AnswerUpdated?.Invoke(this, EventArgs.Empty);
        }

        void ScheduleFlush()
        {
            lock (flushGate)
            {
                if (flushPending) return;
                flushPending = true;
            }
            if (!_dispatcher.TryEnqueue(() =>
                {
                    lock (flushGate) flushPending = false;
                    Flush();
                }))
            {
                lock (flushGate) flushPending = false;
            }
        }

        // 慢首包提示：本地模型首次加载可能十几秒，明确告诉用户“在等，不是卡死”。
        var slowTimer = _dispatcher.CreateTimer();
        slowTimer.Interval = TimeSpan.FromSeconds(3);
        slowTimer.IsRepeating = false;
        slowTimer.Tick += (_, _) =>
        {
            bool hasFirst;
            lock (builder) hasFirst = firstDeltaMs >= 0;
            if (hasFirst || !IsBusy) return;
            answer.ReceivingText = "AI 生成中 · 等待模型响应…（本地模型首次加载可能较慢）";
            StatusHint = $"仍在等待 {profile.DisplayName} 返回首个增量…";
            AnswerUpdated?.Invoke(this, EventArgs.Empty);
        };
        slowTimer.Start();

        try
        {
            await _chat.StreamAsync(
                profile,
                profile.NoKeyRequired ? null : _keyStore.Load(KeyName(profile)),
                Model,
                turns,
                delta =>
                {
                    lock (builder)
                    {
                        builder.Append(delta);
                        if (firstDeltaMs < 0) firstDeltaMs = sw.ElapsedMilliseconds;
                        deltaBlocks++;
                        completionChars += delta.Length;
                    }
                    ScheduleFlush();
                },
                _cts.Token);

            slowTimer.Stop();
            Flush();
            StatusHint = $"已完成（首个增量 {firstDeltaMs} ms，共 {deltaBlocks} 段）。";
            _usage.RecordApiCall(Model,
                (int)Math.Max(1, promptChars / 4),
                (int)Math.Max(1, completionChars / 4));
            // 记住本次使用的模型
            profile.DefaultModel = Model;
            _settingsService.Save();
            _logger.Information("流式回复完成：首个增量 {FirstMs} ms，{Blocks} 段，{Chars} 字",
                firstDeltaMs, deltaBlocks, completionChars);
        }
        catch (OperationCanceledException)
        {
            slowTimer.Stop();
            StatusHint = "已停止。";
            lock (builder) { if (builder.Length == 0) builder.Append("（已取消）"); }
        }
        catch (MissingApiKeyException mk)
        {
            slowTimer.Stop();
            StatusHint = mk.Message;
        }
        catch (Exception ex)
        {
            slowTimer.Stop();
            StatusHint = $"失败：{ex.Message}";
            _logger.Error(ex, "右侧栏AI辅助请求失败：{Name}", profile.DisplayName);
            lock (builder) { if (builder.Length == 0) builder.Append($"（请求失败：{ex.Message}）"); }
        }
        finally
        {
            slowTimer.Stop();
            Flush();
            answer.IsGenerating = false;
            if (answer.Text.Length == 0)
            {
                answer.Text = "（本次没有返回内容）";
            }
            IsBusy = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    /// <summary>停止当前流式请求。</summary>
    [RelayCommand]
    private void Stop()
    {
        _cts?.Cancel();
        StatusHint = "正在停止…";
    }

    /// <summary>压缩历史对话：请模型把已有对话总结成摘要，并用摘要重建上下文（节省 token 后继续对话）。</summary>
    [RelayCommand]
    private async Task CompressHistoryAsync()
    {
        if (IsBusy)
        {
            StatusHint = "正在生成回复：先点“停止”或等完成后再压缩。";
            return;
        }
        var profile = SelectedProfile;
        if (profile is null)
        {
            StatusHint = "没有可用提供商。";
            return;
        }
        if (!profile.NoKeyRequired && string.IsNullOrEmpty(_keyStore.Load(KeyName(profile))))
        {
            StatusHint = $"“{profile.DisplayName}”尚未填写 API-Key。";
            MissingKeyRequested?.Invoke(this, profile);
            return;
        }
        if (string.IsNullOrWhiteSpace(Model))
        {
            StatusHint = "请先填写或刷新模型标识。";
            return;
        }
        var history = Messages.Where(m => m.Text.Length > 0).ToList();
        if (history.Count < 2)
        {
            StatusHint = "对话太少，暂时不需要压缩。";
            return;
        }

        var transcript = string.Join("\n\n", history.Select(m => (m.IsUser ? "用户：" : "AI：") + m.Text));
        if (transcript.Length > 20000) transcript = "…（更早内容已省略）\n" + transcript[^20000..];

        var prompt = "请把下面的多轮对话压缩成结构化中文摘要，供后续对话继续使用。必须保留：\n"
                   + "1) 用户目标、约束与偏好；2) 已确认的结论/决定；3) 关键代码、文件路径、配置或数据；\n"
                   + "4) 未完成事项与下一步；5) 已失败、不要重复的尝试。\n"
                   + "不要寒暄、不要臆造；信息缺失就标注“未提及”。600 字以内，用要点列表。\n\n[对话记录]\n"
                   + transcript;

        var temp = new AiAssistMsgRow { Role = "assistant", Text = string.Empty, IsGenerating = true };
        Messages.Add(temp);
        IsBusy = true;
        StatusHint = "正在压缩历史对话…";
        _cts = new CancellationTokenSource();

        var sb = new StringBuilder();
        var flushGate = new object();
        var flushPending = false;
        void Flush()
        {
            string snapshot;
            lock (sb) snapshot = sb.ToString();
            if (!string.Equals(temp.Text, snapshot, StringComparison.Ordinal)) temp.Text = snapshot;
            temp.ReceivingText = $"AI 生成中 · 已接收 {snapshot.Length} 字";
            AnswerUpdated?.Invoke(this, EventArgs.Empty);
        }
        void ScheduleFlush()
        {
            lock (flushGate)
            {
                if (flushPending) return;
                flushPending = true;
            }
            if (!_dispatcher.TryEnqueue(() =>
                {
                    lock (flushGate) flushPending = false;
                    Flush();
                }))
            {
                lock (flushGate) flushPending = false;
            }
        }

        var promptChars = (long)prompt.Length;
        var completionChars = 0L;
        try
        {
            await _chat.StreamAsync(
                profile,
                profile.NoKeyRequired ? null : _keyStore.Load(KeyName(profile)),
                Model,
                new[] { new ChatTurn { Role = "user", Content = prompt } },
                delta =>
                {
                    lock (sb)
                    {
                        sb.Append(delta);
                        completionChars += delta.Length;
                    }
                    ScheduleFlush();
                },
                _cts.Token);

            Flush();
            var summary = temp.Text.Trim();
            if (summary.Length == 0)
            {
                StatusHint = "压缩没有返回内容，已保留原对话。";
                return;
            }

            Messages.Clear();
            Messages.Add(new AiAssistMsgRow { Role = "user", Text = "【历史对话摘要】\n" + summary });
            Messages.Add(new AiAssistMsgRow { Role = "assistant", Text = "已记录上述摘要作为上下文，请继续提问。" });
            StatusHint = $"已压缩历史对话（摘要约 {summary.Length} 字）。";
            _usage.RecordApiCall(Model,
                (int)Math.Max(1, promptChars / 4),
                (int)Math.Max(1, completionChars / 4));
        }
        catch (OperationCanceledException)
        {
            StatusHint = "已取消压缩，原对话保留。";
        }
        catch (Exception ex)
        {
            StatusHint = $"压缩失败：{ex.Message}";
            _logger.Error(ex, "右侧栏AI辅助压缩历史对话失败");
        }
        finally
        {
            temp.IsGenerating = false;
            if (Messages.Contains(temp)) Messages.Remove(temp);
            IsBusy = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    /// <summary>清空对话。</summary>
    [RelayCommand]
    private void Clear()
    {
        if (IsBusy) _cts?.Cancel();
        Messages.Clear();
        StatusHint = "已清空对话。";
    }

    /// <summary>请求关闭侧栏。</summary>
    [RelayCommand]
    private void Close() => CloseRequested?.Invoke(this, EventArgs.Empty);
}
