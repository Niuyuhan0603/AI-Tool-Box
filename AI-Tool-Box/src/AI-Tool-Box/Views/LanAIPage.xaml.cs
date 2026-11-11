using AI_Tool_Box.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Serilog;

namespace AI_Tool_Box.Views;

/// <summary>局域网 AI 服务页：本机 AI 代理 + 发现/保存远端 AI 端点。</summary>
public sealed partial class LanAIPage : Page
{
    private readonly ILogger _logger;
    private readonly LanAiViewModel _viewModel;

    public LanAiViewModel ViewModel => _viewModel;

    public LanAIPage()
    {
        InitializeComponent();
        _logger = App.Services.GetService(typeof(ILogger)) as ILogger
                  ?? Serilog.Log.Logger;
        _viewModel = App.Services.GetRequiredService<LanAiViewModel>();
        DataContext = _viewModel;
        _logger.Information("LanAIPage 已加载");
    }
}
