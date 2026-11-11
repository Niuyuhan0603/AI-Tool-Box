using AI_Tool_Box.Models;
using AI_Tool_Box.Services;
using AI_Tool_Box.ViewModels;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Serilog;
using System.Collections.Specialized;

namespace AI_Tool_Box.Views;

/// <summary>右侧栏 AI 辅助面板：多提供商 BYOK 聊天，支持附带当前网页正文问答。</summary>
public sealed partial class AiAssistPanel : UserControl
{
    private readonly ILogger _logger;

    /// <summary>正在进行的页面元素拾取（再次点击“页面拾取”即取消）。</summary>
    private CancellationTokenSource? _pickCts;

    /// <summary>是否已排队一次滚动（合并高频刷新）。</summary>
    private bool _scrollQueued;

    /// <summary>面板视图模型（App 单例，跨页共享会话）。</summary>
    public AiAssistViewModel ViewModel { get; }

    /// <summary>初始化面板并解析依赖。</summary>
    public AiAssistPanel()
    {
        InitializeComponent();
        _logger = App.Services.GetService(typeof(ILogger)) as ILogger ?? Serilog.Log.Logger;
        ViewModel = App.Services.GetRequiredService<AiAssistViewModel>();
        ViewModel.MissingKeyRequested += OnMissingKeyRequested;
        ViewModel.CloseRequested += (_, _) => OnCloseClick(this, new RoutedEventArgs());
        ViewModel.Messages.CollectionChanged += OnMessagesChanged;
        ViewModel.AnswerUpdated += OnAnswerUpdated;
    }

    /// <summary>消息列表变化时滚动到最新一条（流式回复持续可见）。</summary>
    private void OnMessagesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (ViewModel.Messages.Count == 0) return;
        var last = ViewModel.Messages[^1];
        DispatcherQueue.TryEnqueue(() => MessageList.ScrollIntoView(last));
    }

    /// <summary>流式刷新时跟随滚动：仅当用户本来就停在底部附近，避免打断向上翻阅历史。</summary>
    private void OnAnswerUpdated(object? sender, EventArgs e)
    {
        if (_scrollQueued || ViewModel.Messages.Count == 0) return;
        var sv = FindScrollViewer(MessageList);
        if (sv is null) return;
        if (sv.ScrollableHeight - sv.VerticalOffset > 96) return;

        _scrollQueued = true;
        DispatcherQueue.TryEnqueue(() =>
        {
            _scrollQueued = false;
            if (ViewModel.Messages.Count > 0) MessageList.ScrollIntoView(ViewModel.Messages[^1]);
        });
    }

    /// <summary>在可视化树中查找 ListView 内部的 ScrollViewer。</summary>
    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        if (root is ScrollViewer sv) return sv;
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            if (FindScrollViewer(VisualTreeHelper.GetChild(root, i)) is { } found) return found;
        }
        return null;
    }

    /// <summary>打开“选取网页内容”弹窗（区块勾选 / 截图框选）。</summary>
    private async void OnPickPageContentClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var shell = App.Services.GetRequiredService<IWebShellService>();
            var dlg = new PageContextDialog { XamlRoot = XamlRoot };
            await dlg.PrepareAsync(shell);
            var result = await dlg.ShowAsync();
            if (result == ContentDialogResult.Primary && !string.IsNullOrEmpty(dlg.ResultHtml))
            {
                ViewModel.AttachPageContext(dlg.ResultHtml, dlg.ResultSummary);
            }
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "右侧栏AI辅助：打开网页内容选取弹窗失败");
        }
    }

    /// <summary>页面拾取：进入“开发者工具”式选元素模式（再次点击即取消）。</summary>
    private async void OnPickElementClick(object sender, RoutedEventArgs e)
    {
        if (_pickCts is not null)
        {
            _pickCts.Cancel();
            ViewModel.StatusHint = "正在取消页面拾取…";
            return;
        }

        var shell = App.Services.GetRequiredService<IWebShellService>();
        if (!shell.IsAvailable)
        {
            ViewModel.StatusHint = "当前没有打开的网页：请先在网页标签里打开一个页面。";
            return;
        }

        _pickCts = new CancellationTokenSource();
        PickElementButton.Content = "取消拾取";
        ViewModel.StatusHint = "页面拾取已开启：在网页上移动鼠标高亮目标区域，左键点击即选取，Esc 取消。";
        try
        {
            var html = await shell.PickElementOnPageAsync(_pickCts.Token);
            if (!string.IsNullOrEmpty(html))
            {
                ViewModel.AttachPageContext(html, $"页面拾取 1 个区域，约 {html.Length} 字符");
            }
            else
            {
                ViewModel.StatusHint = "页面拾取已取消（未取到内容）。";
            }
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "右侧栏AI辅助：页面拾取失败");
            ViewModel.StatusHint = $"页面拾取失败：{ex.Message}";
        }
        finally
        {
            _pickCts?.Dispose();
            _pickCts = null;
            PickElementButton.Content = "页面拾取";
        }
    }

    /// <summary>清除已附带的网页片段。</summary>
    private void OnClearAttachedClick(object sender, RoutedEventArgs e) => ViewModel.ClearAttachedCommand.Execute(null);

    /// <summary>用户点击收起按钮。</summary>
    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        CollapsedRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>向宿主（WebShell）请求收起面板。</summary>
    public event EventHandler? CollapsedRequested;

    /// <summary>从本地服务（Ollama）重新读取已安装模型。</summary>
    private void OnRefreshModelsClick(object sender, RoutedEventArgs e) => ViewModel.RefreshModels();

    /// <summary>Enter 发送 / Shift+Enter 换行。</summary>
    private void OnInputKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter &&
            !Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Shift)
                .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down))
        {
            e.Handled = true;
            if (ViewModel.SendCommand.CanExecute(null))
            {
                ViewModel.SendCommand.Execute(null);
            }
        }
    }

    /// <summary>API-Key 缺失：弹窗引导前往设置。</summary>
    private async void OnMissingKeyRequested(object? sender, AiAssistProfile profile)
    {
        try
        {
            var dlg = new ContentDialog
            {
                Title = "缺少 API-Key",
                Content = $"“{profile.DisplayName}”尚未填写 API-Key。\n请在 设置 → 右侧栏AI辅助 中填写后再发起请求。",
                PrimaryButtonText = "前往设置",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = XamlRoot
            };
            var result = await dlg.ShowAsync();
            if (result == ContentDialogResult.Primary)
            {
                WeakReferenceMessenger.Default.Send(new NavigateToTagMessage("Settings"));
            }
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "右侧栏AI辅助：缺少 Key 弹窗展示失败");
        }
    }
}

/// <summary>按消息角色选择气泡模板。</summary>
public sealed class AiAssistRoleSelector : DataTemplateSelector
{
    /// <summary>用户消息模板。</summary>
    public DataTemplate? UserTemplate { get; set; }

    /// <summary>AI 回复模板。</summary>
    public DataTemplate? AssistantTemplate { get; set; }

    /// <summary>按 Role 选择模板。</summary>
    protected override DataTemplate? SelectTemplateCore(object item)
        => item is AiAssistMsgRow { IsUser: true } ? UserTemplate : AssistantTemplate;

    /// <summary>按 Role 选择模板（容器复用路径）。</summary>
    protected override DataTemplate? SelectTemplateCore(object item, DependencyObject container)
        => SelectTemplateCore(item);
}
