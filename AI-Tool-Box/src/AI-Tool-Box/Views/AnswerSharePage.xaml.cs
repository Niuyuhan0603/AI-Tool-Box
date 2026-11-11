using AI_Tool_Box.Models;
using AI_Tool_Box.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Serilog;

namespace AI_Tool_Box.Views;

/// <summary>分享题目答案页：我的题库（本地）+ 局域网搜题 + 共享设置 + 注入当前网页。</summary>
public sealed partial class AnswerSharePage : Page
{
    private readonly ILogger _logger;
    private readonly AnswerShareViewModel _viewModel;

    /// <summary>XAML x:Bind 绑定。</summary>
    public AnswerShareViewModel ViewModel => _viewModel;

    /// <summary>初始化 AnswerSharePage。</summary>
    public AnswerSharePage()
    {
        InitializeComponent();
        _logger = App.Services.GetService(typeof(ILogger)) as ILogger
                  ?? Serilog.Log.Logger;
        _viewModel = App.Services.GetRequiredService<AnswerShareViewModel>();
        DataContext = _viewModel;
        _logger.Information("AnswerSharePage 已加载（本地题库 {Count} 条）", _viewModel.LocalItems.Count);
    }

    /// <summary>我的题库：点击某个条目 → 进入编辑模式。</summary>
    private void OnAnswerItemTapped(object sender, TappedRoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is AnswerItem item)
        {
            _viewModel.SelectForEditCommand.Execute(item);
        }
    }
}
