using AI_Tool_Box.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;
using Serilog;

namespace AI_Tool_Box.Views;

/// <summary>音视频 AI 智能总结页。</summary>
public sealed partial class MediaSummaryPage : Page
{
    private readonly ILogger _logger;
    private readonly MediaSummaryViewModel _viewModel;

    /// <summary>暴露 ViewModel 供 XAML x:Bind 绑定。</summary>
    public MediaSummaryViewModel ViewModel => _viewModel;

    /// <summary>初始化页面并注入 ViewModel。</summary>
    public MediaSummaryPage()
    {
        InitializeComponent();
        _logger = App.Services.GetService(typeof(ILogger)) as ILogger ?? Serilog.Log.Logger;
        _viewModel = App.Services.GetRequiredService<MediaSummaryViewModel>();
        DataContext = _viewModel;
        _logger.Information("MediaSummaryPage 已加载");
    }
}
