using AI_Tool_Box.Models;
using AI_Tool_Box.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;

namespace AI_Tool_Box.ViewModels;

/// <summary>题库分享视图模型：我的题库（创建/编辑/删除/搜索）+ 局域网搜索 + 黑白名单设置 + 注入当前网页焦点。</summary>
public partial class AnswerShareViewModel : ObservableObject
{
    private readonly ILogger _logger;
    private readonly IAnswerShareService _service;
    private readonly IWebShellService _web;

    /// <summary>本地题库。</summary>
    public System.Collections.ObjectModel.ObservableCollection<AnswerItem> LocalItems => _service.LocalItems;

    /// <summary>本地题库过滤视图（列表绑定项；空关键词 = 全部，搜索后 = 命中排序结果）。</summary>
    public System.Collections.ObjectModel.ObservableCollection<AnswerItem> LocalView { get; } = new();

    /// <summary>局域网匹配结果。</summary>
    public System.Collections.ObjectModel.ObservableCollection<AnswerMatch> LanResults => _service.LanResults;

    /// <summary>题库在线设备（从 UDP Hello 维护）。</summary>
    public System.Collections.ObjectModel.ObservableCollection<LanPeer> OnlinePeers => _service.OnlinePeers;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCurrentCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeleteCurrentCommand))]
    [NotifyCanExecuteChangedFor(nameof(InjectSelectedAnswerCommand))]
    private AnswerItem? _editing;

    /// <summary>编辑中的题目题干。</summary>
    [ObservableProperty]
    private string _editQuestion = string.Empty;

    /// <summary>编辑中的答案。</summary>
    [ObservableProperty]
    private string _editAnswer = string.Empty;

    /// <summary>编辑中的题号（可选）。</summary>
    [ObservableProperty]
    private string _editNo = string.Empty;

    /// <summary>编辑中的标签（逗号分隔）。</summary>
    [ObservableProperty]
    private string _editTags = string.Empty;

    [ObservableProperty]
    private string _localSearchKeyword = string.Empty;

    [ObservableProperty]
    private string _lanSearchKeyword = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SearchLanCommand))]
    private bool _isSearchingLan;

    /// <summary>是否已开启题库共享（包装 Service 状态，供界面绑定）。</summary>
    public bool IsSharing => _service.IsSharing;

    /// <summary>共享开关按钮文字（随状态切换）。</summary>
    public string ShareButtonText => IsSharing ? "停止共享" : "开启共享";

    [ObservableProperty]
    private string _status = "题库：点击“开启共享”启用局域网搜索。";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(InjectSelectedAnswerCommand))]
    private AnswerMatch? _selectedMatch;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(InjectSelectedAnswerCommand))]
    private AnswerItem? _selectedLocalItem;

    /// <summary>共享设置引用（直接双向绑定）。</summary>
    public AnswerShareSettings Settings => _service.Settings;

    /// <summary>共享信息：本机 IP + TCP 端口。</summary>
    public string InfoText => $"{Environment.MachineName} @ {_service.LocalIp} : {_service.LocalTcpPort}";

    /// <summary>Mode 选择 ComboBox 绑定：0=黑名单模式，1=白名单模式。转换 int ↔ enum。</summary>
    public int ModeInt
    {
        get => (int)Settings.Mode;
        set
        {
            if (value < 0 || value > 1) return;
            Settings.Mode = (ListMode)value;
            OnPropertyChanged();
        }
    }

    /// <summary>逗号分隔黑名单 IP（双向绑定 TextBox）。</summary>
    public string BlacklistCsv
    {
        get => string.Join(',', Settings.Blacklist);
        set
        {
            Settings.Blacklist = value?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList() ?? new();
            OnPropertyChanged();
        }
    }

    /// <summary>逗号分隔白名单 IP。</summary>
    public string WhitelistCsv
    {
        get => string.Join(',', Settings.Whitelist);
        set
        {
            Settings.Whitelist = value?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList() ?? new();
            OnPropertyChanged();
        }
    }

    /// <summary>初始化。</summary>
    public AnswerShareViewModel(ILogger logger, IAnswerShareService service, IWebShellService web)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _web = web ?? throw new ArgumentNullException(nameof(web));
        // 题库集合变化（保存/删除/启动加载）→ 自动刷新过滤视图
        LocalItems.CollectionChanged += (_, _) => RefreshLocalView();
        RefreshLocalView();
        // 设置更改后需要立即让 Mode/List 同步。
    }

    partial void OnEditingChanged(AnswerItem? value)
    {
        if (value is null) return;
        EditQuestion = value.Question;
        EditAnswer = value.Answer;
        EditTags = value.Tags;
        EditNo = value.No;
    }

    private bool CanSaveCurrent() => !string.IsNullOrWhiteSpace(EditQuestion) && !string.IsNullOrWhiteSpace(EditAnswer);
    private bool CanDeleteCurrent() => Editing is not null;
    private bool CanInjectSelected()
    {
        var text = SelectedAnswerText();
        return !string.IsNullOrEmpty(text) && _web.IsAvailable;
    }

    private string? SelectedAnswerText()
    {
        if (SelectedMatch is not null) return SelectedMatch.Item.Answer;
        if (SelectedLocalItem is not null) return SelectedLocalItem.Answer;
        if (Editing is not null) return EditAnswer;
        return null;
    }

    partial void OnSelectedMatchChanged(AnswerMatch? value)
        => InjectSelectedAnswerCommand.NotifyCanExecuteChanged();
    partial void OnSelectedLocalItemChanged(AnswerItem? value)
        => InjectSelectedAnswerCommand.NotifyCanExecuteChanged();

    /// <summary>新建条目：立即写入题库并在"我的题库"列表显示（未填写题干不会被局域网共享），随后进入编辑。</summary>
    [RelayCommand]
    private void New()
    {
        var item = new AnswerItem
        {
            Id = Guid.NewGuid().ToString("N"),
            Question = string.Empty,
            Answer = string.Empty,
            Tags = string.Empty
        };
        _service.SaveLocal(item);
        Editing = item;
        Status = "已新建条目并加入“我的题库”：填写题干、答案后点击“保存”即可。未填写题干的条目不会被共享。";
    }

    /// <summary>保存当前 Editing（使用界面上的 EditNo/Question/Answer/Tags 同步写回）。</summary>
    [RelayCommand(CanExecute = nameof(CanSaveCurrent))]
    private void SaveCurrent()
    {
        if (!CanSaveCurrent()) return;
        var item = Editing ?? new AnswerItem { Id = Guid.NewGuid().ToString("N") };
        item.No = EditNo?.Trim() ?? string.Empty;
        item.Question = EditQuestion?.Trim() ?? string.Empty;
        item.Answer = EditAnswer ?? string.Empty;
        item.Tags = EditTags ?? string.Empty;
        _service.SaveLocal(item);
        Editing = item;  // 选中刚保存的
        Status = $"已保存条目：{Preview(item.FullQuestion)}。";
        _logger.Information("保存题库条目：{Id}", item.Id);
    }

    /// <summary>删除当前选中 Editing，并清空表单（不再自动新建空白条目）。</summary>
    [RelayCommand(CanExecute = nameof(CanDeleteCurrent))]
    private void DeleteCurrent()
    {
        if (Editing is null) return;
        _service.DeleteLocal(Editing.Id);
        ResetEditor();
        Status = "已删除条目。";
    }

    /// <summary>清空编辑：当前条目若为空白草稿（无题干且无答案）则一并移出题库；否则仅清空表单继续编辑该条目。</summary>
    [RelayCommand]
    private void ClearEdit()
    {
        if (Editing is not null &&
            string.IsNullOrWhiteSpace(Editing.Question) &&
            string.IsNullOrWhiteSpace(Editing.Answer))
        {
            _service.DeleteLocal(Editing.Id);
            ResetEditor();
            Status = "已清空编辑：空白草稿条目已从“我的题库”移除。";
        }
        else
        {
            ResetEditor();
            Status = "已清空编辑表单（原条目保留在题库中，可从列表重新选中）。";
        }
    }

    /// <summary>重置编辑器：清空 Editing 与全部输入框。</summary>
    private void ResetEditor()
    {
        Editing = null;
        EditNo = string.Empty;
        EditQuestion = string.Empty;
        EditAnswer = string.Empty;
        EditTags = string.Empty;
        DeleteCurrentCommand.NotifyCanExecuteChanged();
    }

    /// <summary>从本地题库选中某条 → 进入编辑模式。</summary>
    [RelayCommand]
    private void SelectForEdit(AnswerItem? item)
    {
        if (item is null) return;
        Editing = item;
        Status = $"进入编辑模式：{Preview(item.Question)}";
    }

    /// <summary>启动/停止共享。</summary>
    [RelayCommand]
    private void ToggleSharing()
    {
        if (!IsSharing)
        {
            var ok = _service.StartSharing();
            if (ok)
            {
                OnPropertyChanged(nameof(IsSharing));
                OnPropertyChanged(nameof(ShareButtonText));
                OnPropertyChanged(nameof(InfoText)); // TCP 端口冲突 +1 后刷新显示
                SearchLanCommand.NotifyCanExecuteChanged();
                Status = $"题库共享已开启：{InfoText}。UDP {UdpConstDisplay}, TCP {_service.LocalTcpPort}。";
                _logger.Information("用户开启题库共享");
            }
            else Status = "启动题库共享失败（端口占用）。";
        }
        else
        {
            _service.StopSharing();
            OnPropertyChanged(nameof(IsSharing));
            OnPropertyChanged(nameof(ShareButtonText));
            OnPropertyChanged(nameof(InfoText));
            SearchLanCommand.NotifyCanExecuteChanged();
            Status = "题库共享已停止。";
            _logger.Information("用户关闭题库共享");
        }
    }

    private const string UdpConstDisplay = "9529";

    /// <summary>保存共享设置。</summary>
    [RelayCommand]
    private void SaveSettings()
    {
        _service.SaveSettings();
        Status = "共享设置已保存（黑白名单）。";
    }

    /// <summary>关键词变化 → 实时过滤本地列表（空关键词显示全部）。</summary>
    partial void OnLocalSearchKeywordChanged(string value) => RefreshLocalView();

    /// <summary>按当前关键词重建过滤视图（命中排序，空关键词 = 全部按修改时间）。</summary>
    private void RefreshLocalView()
    {
        var kw = LocalSearchKeyword?.Trim();
        var items = string.IsNullOrWhiteSpace(kw)
            ? LocalItems.ToList()
            : _service.SearchLocal(kw, 200).ToList();
        LocalView.Clear();
        foreach (var i in items) LocalView.Add(i);
    }

    /// <summary>本地搜索：按关键词过滤"我的题库"列表（评分排序：题干 > 标签 > 答案）。</summary>
    [RelayCommand]
    private void SearchLocal()
    {
        RefreshLocalView();
        var kw = LocalSearchKeyword?.Trim();
        Status = string.IsNullOrWhiteSpace(kw)
            ? $"本地题库共 {LocalView.Count} 条（已清空筛选）。"
            : $"本地搜索完成：命中 {LocalView.Count} / {LocalItems.Count} 条。点击条目进入编辑。";
    }

    private bool CanSearchLan() => IsSharing && !IsSearchingLan && !string.IsNullOrWhiteSpace(LanSearchKeyword);

    /// <summary>局域网搜索（关键词广播 → 等待 N 秒 → 汇总结果到 LanResults）。</summary>
    [RelayCommand(CanExecute = nameof(CanSearchLan))]
    private async Task SearchLan()
    {
        if (!CanSearchLan()) return;
        IsSearchingLan = true;
        try
        {
            var kw = LanSearchKeyword.Trim();
            Status = $"局域网搜索 \"{kw}\"…（最多等待 5 秒，右侧将累积返回结果）";
            await _service.SearchLanAsync(kw, 5);
            Status = $"局域网搜索完成：\"{kw}\" 共 {LanResults.Count} 条。可选中结果后点击“注入到当前网页焦点输入框”。";
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "局域网搜索失败");
            Status = $"局域网搜索失败：{ex.Message}";
        }
        finally
        {
            IsSearchingLan = false;
        }
    }

    /// <summary>从文本文件（.txt/.md/.csv/....）导入答案到编辑框（保留换行与缩进）。</summary>
    [RelayCommand]
    private async Task ImportAnswerFile()
    {
        try
        {
            var picker = new Windows.Storage.Pickers.FileOpenPicker
            {
                ViewMode = Windows.Storage.Pickers.PickerViewMode.List,
                SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary
            };
            picker.FileTypeFilter.Add(".txt");
            picker.FileTypeFilter.Add(".md");
            picker.FileTypeFilter.Add(".csv");
            picker.FileTypeFilter.Add(".log");
            picker.FileTypeFilter.Add(".json");
            picker.FileTypeFilter.Add(".html");
            picker.FileTypeFilter.Add(".py");
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

            var file = await picker.PickSingleFileAsync();
            if (file is null) return;
            var text = await Windows.Storage.FileIO.ReadTextAsync(file);
            EditAnswer = text;
            Status = $"已导入答案文件：{file.Name}（{text.Length} 字符）。检查内容后点击“保存”。";
            _logger.Information("导入答案文件：{Name}（{Len} 字符）", file.Name, text.Length);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "导入答案文件失败");
            Status = $"导入失败：{ex.Message}";
        }
    }

    /// <summary>将选中答案（局域网匹配或本地或编辑中）注入当前网页焦点输入框。</summary>
    [RelayCommand(CanExecute = nameof(CanInjectSelected))]
    private async Task InjectSelectedAnswer()
    {
        var text = SelectedAnswerText();
        if (string.IsNullOrEmpty(text))
        {
            Status = "请先选择答案（编辑/本地列表/局域网列表）。";
            return;
        }
        if (!_web.IsAvailable)
        {
            Status = "无可用网页：请先在侧栏“网址导航”打开网页并激活输入框。";
            return;
        }
        try
        {
            var ok = await _web.InjectToActiveElementAsync(text);
            Status = ok
                ? $"答案已注入到当前网页焦点输入框（{text.Length} 字符）。"
                : "注入失败：当前网页焦点元素不支持写入，请先点击网页输入框激活后再试。";
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "注入答案失败");
            Status = $"注入失败：{ex.Message}";
        }
    }

    private static string Preview(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return string.Empty;
        var r = s.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return r.Length <= 40 ? r : r[..40] + "…";
    }
}
