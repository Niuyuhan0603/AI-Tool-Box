using AI_Tool_Box.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;

namespace AI_Tool_Box.ViewModels;

/// <summary>Markdown 思维导图 VM：Markdown 编辑 + AI 生成/扩写大纲（渲染与导出由页面与 WebView2 完成）。</summary>
public partial class MindMapViewModel : ObservableObject
{
    private readonly ILogger _logger;
    private readonly IAiQuickService _ai;

    [ObservableProperty] private string _markdown = DefaultMarkdown;
    [ObservableProperty] private string _topic = string.Empty;
    [ObservableProperty] private string _status = "左侧编辑 Markdown 后点「Markdown 转思维导图」渲染；右侧图上可直接增删改节点、拖拽改父级、设置节点/连线样式，改动会同步回 Markdown，可导出 SVG/PNG/HTML/MD。";
    [ObservableProperty] private bool _isBusy;

    private const string DefaultMarkdown = """
        # 思维导图示例
        ## 核心主题
        - 分支一
          - 子要点 A
          - 子要点 B
        - 分支二
          - 子要点 C
        ## 使用方式
        - 用 # 标题分级
        - 用 - 列表构建子节点
        ### 支持导出
        - SVG / PNG / HTML / MD
        """;

    /// <summary>构造：注入 AI 快捷服务与日志。</summary>
    public MindMapViewModel(ILogger logger, IAiQuickService ai)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _ai = ai ?? throw new ArgumentNullException(nameof(ai));
    }

    private bool CanRun => !IsBusy;

    [RelayCommand]
    private void Sample() => Markdown = DefaultMarkdown;

    [RelayCommand]
    private void Clear() => Markdown = string.Empty;

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task GenerateAsync()
    {
        if (string.IsNullOrWhiteSpace(Topic) && string.IsNullOrWhiteSpace(Markdown))
        {
            Status = "请输入主题（或先粘贴一段文字），再让 AI 生成大纲。";
            return;
        }
        IsBusy = true;
        Status = "AI 生成中…";
        try
        {
            var input = string.IsNullOrWhiteSpace(Topic) ? Markdown : Topic;
            var instruction = "你是知识结构专家。请把输入内容整理成 Markdown 层级大纲，用于绘制思维导图："
                + "以 1 个 # 一级标题作为中心主题，## 为一级分支，### 为二级分支，条目用 - 列表；"
                + "层级不超过 4 层，每个节点不超过 20 字，只输出 Markdown，不要解释。";
            Markdown = await _ai.AskAsync(instruction, input);
            Status = "已生成 Markdown 大纲。";
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "AI 生成思维导图失败");
            Status = $"生成失败：{ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    partial void OnIsBusyChanged(bool value) => GenerateCommand.NotifyCanExecuteChanged();
}
