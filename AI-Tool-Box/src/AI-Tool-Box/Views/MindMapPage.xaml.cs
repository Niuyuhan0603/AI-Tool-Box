using AI_Tool_Box.Controls;
using AI_Tool_Box.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using Serilog;
using System;
using System.ComponentModel;
using System.IO;
using System.Text.Json;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace AI_Tool_Box.Views;

/// <summary>Markdown → 思维导图页：左侧 Markdown，右侧离线渲染（Assets/Web/mindmap/index.html）。</summary>
public sealed partial class MindMapPage : Page
{
    private readonly ILogger _logger;
    private readonly MindMapViewModel _viewModel;
    private bool _webReady;
    /// <summary>图内编辑回传 Markdown 时置位，避免再次回灌渲染造成循环。</summary>
    private bool _suppressMdRender;

    /// <summary>暴露 ViewModel 供 XAML x:Bind 绑定。</summary>
    public MindMapViewModel ViewModel => _viewModel;

    /// <summary>初始化页面并挂载 WebView2。</summary>
    public MindMapPage()
    {
        InitializeComponent();
        _logger = App.Services.GetService(typeof(ILogger)) as ILogger ?? Serilog.Log.Logger;
        _viewModel = App.Services.GetRequiredService<MindMapViewModel>();
        DataContext = _viewModel;

        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        Loaded += OnLoaded;
        Unloaded += (_, _) => _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _logger.Information("MindMapPage 已加载");
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            // 页面实例被导航缓存复用：每次进入都重新订阅（Unloaded 时已注销）
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;

            var env = await WebShell.GetSharedEnvironmentAsync();
            await Preview.EnsureCoreWebView2Async(env);
            Preview.DefaultBackgroundColor = Windows.UI.Color.FromArgb(255, 10, 15, 20);
            Preview.CoreWebView2.WebMessageReceived -= OnWebMessage;
            Preview.CoreWebView2.WebMessageReceived += OnWebMessage;
            Preview.Source = new Uri(Path.Combine(AppContext.BaseDirectory, "Assets", "Web", "mindmap", "index.html"));
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "思维导图 WebView2 初始化失败");
            _viewModel.Status = $"预览初始化失败：{ex.Message}";
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MindMapViewModel.Markdown) && _webReady && !_suppressMdRender)
            Render(_viewModel.Markdown);
    }

    private void OnRenderClick(object sender, RoutedEventArgs e) => Render(_viewModel.Markdown);

    private void Render(string markdown)
    {
        if (!_webReady || Preview.CoreWebView2 is null) return;
        var json = JsonSerializer.Serialize(new { type = "render", markdown });
        Preview.CoreWebView2.PostWebMessageAsJson(json);
    }

    private async void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var doc = JsonDocument.Parse(e.WebMessageAsJson);
            var root = doc.RootElement;
            if (!root.TryGetProperty("type", out var typeEl)) return;
            var type = typeEl.GetString();

            if (type == "ready")
            {
                _webReady = true;
                Render(_viewModel.Markdown);
                return;
            }

            // 图内编辑（增/删/改/改父级/改样式）→ 回写 Markdown，实现「图 → Markdown」同步
            if (type == "changed")
            {
                var md = root.TryGetProperty("markdown", out var mdEl) ? mdEl.GetString() ?? "" : "";
                _suppressMdRender = true;
                try { _viewModel.Markdown = md; }
                finally { _suppressMdRender = false; }
                _viewModel.Status = "思维导图已更新，Markdown 已同步。";
                return;
            }

            if (type == "status")
            {
                var s = root.TryGetProperty("message", out var sEl) ? sEl.GetString() ?? "" : "";
                if (!string.IsNullOrEmpty(s)) _viewModel.Status = s;
                return;
            }

            if (type != "export") return;
            var format = root.TryGetProperty("format", out var fEl) ? fEl.GetString() ?? "" : "";
            var data = root.TryGetProperty("data", out var dEl) ? dEl.GetString() ?? "" : "";

            var picker = new FileSavePicker();
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
            switch (format)
            {
                case "svg": picker.FileTypeChoices.Add("SVG 图片", new[] { ".svg" }); break;
                case "png": picker.FileTypeChoices.Add("PNG 图片", new[] { ".png" }); break;
                case "html": picker.FileTypeChoices.Add("HTML 网页", new[] { ".html" }); break;
                default: picker.FileTypeChoices.Add("Markdown", new[] { ".md" }); break;
            }
            picker.SuggestedFileName = $"mindmap-{DateTime.Now:yyyyMMdd-HHmmss}";
            var file = await picker.PickSaveFileAsync();
            if (file is null) return;

            if (format == "png")
            {
                var bytes = Convert.FromBase64String(data);
                await FileIO.WriteBytesAsync(file, bytes);
            }
            else
            {
                await FileIO.WriteTextAsync(file, data);
            }
            _viewModel.Status = $"已导出：{file.Path}";
            _logger.Information("思维导图导出 {Format}：{Path}", format, file.Path);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "思维导图导出失败");
            _viewModel.Status = $"导出失败：{ex.Message}";
        }
    }
}
