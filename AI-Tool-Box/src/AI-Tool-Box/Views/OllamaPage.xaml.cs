using AI_Tool_Box.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Serilog;

namespace AI_Tool_Box.Views;

/// <summary>本地 Ollama 一键运行页：检测安装 / 启动 serve / 拉取与删除模型。</summary>
public sealed partial class OllamaPage : Page
{
    private readonly ILogger _logger;
    private readonly OllamaViewModel _viewModel;

    /// <summary>暴露 ViewModel 供 XAML x:Bind 绑定。</summary>
    public OllamaViewModel ViewModel => _viewModel;

    /// <summary>初始化页面并注入 ViewModel。</summary>
    public OllamaPage()
    {
        InitializeComponent();

        _logger = App.Services.GetService(typeof(ILogger)) as ILogger ?? Serilog.Log.Logger;
        _viewModel = App.Services.GetRequiredService<OllamaViewModel>();
        DataContext = _viewModel;

        Loaded += OnLoaded;
        _logger.Information("OllamaPage 已加载");
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            await _viewModel.InitializeAsync();
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Ollama 页面初始化异常");
        }
    }

    /// <summary>删除列表中选中的模型。</summary>
    private void OnDeleteSelectedClick(object sender, RoutedEventArgs e)
    {
        var model = ModelList.SelectedItem as string;
        _viewModel.DeleteModelCommand.Execute(model);
    }
}
