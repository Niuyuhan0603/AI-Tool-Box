using AI_Tool_Box.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Serilog;
using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace AI_Tool_Box.Views;

/// <summary>Autoscript 页：Lua 脚本编辑器 + 沙箱运行 + 加密打包。</summary>
public sealed partial class AutoscriptPage : Page
{
    private const string SampleScript = """
-- Autoscript 示例（Lua）：调用工具箱 API
toolbox.log("=== Autoscript 示例开始 ===")
toolbox.log("当前时间：" .. toolbox.now())

local sum = 0
for i = 1, 10 do
  sum = sum + i
end
toolbox.log("1+2+...+10 = " .. sum)

-- 调用 AI（需在 设置 → 右侧栏AI辅助 配置提供商）
-- local answer = toolbox.ai("用一句话解释什么是 Lua 沙箱")
-- toolbox.log("AI：" .. answer)

-- 写文件（示例路径：程序目录）
local path = toolbox.app_dir() .. "autoscript-demo.txt"
toolbox.file_write(path, "hello from autoscript " .. toolbox.now())
toolbox.log("已写入：" .. path)
toolbox.log("=== 示例结束 ===")
""";

    private readonly ILogger _logger;
    private readonly IAutoscriptService _scripts;
    private readonly DispatcherQueue _dispatcher;

    /// <summary>初始化页面。</summary>
    public AutoscriptPage()
    {
        InitializeComponent();
        _logger = App.Services.GetService(typeof(ILogger)) as ILogger ?? Serilog.Log.Logger;
        _scripts = App.Services.GetRequiredService<IAutoscriptService>();
        _dispatcher = DispatcherQueue.GetForCurrentThread() ?? DispatcherQueueController.CreateOnDedicatedThread().DispatcherQueue;
        ScriptBox.Text = SampleScript;
        _logger.Information("AutoscriptPage 已加载");
    }

    private void AppendLog(string line) => _dispatcher.TryEnqueue(() =>
    {
        LogBox.Text += $"[{DateTime.Now:HH:mm:ss}] {line}{Environment.NewLine}";
    });

    private void OnSampleClick(object sender, RoutedEventArgs e) => ScriptBox.Text = SampleScript;

    private void OnClearLogClick(object sender, RoutedEventArgs e) => LogBox.Text = string.Empty;

    private async void OnRunClick(object sender, RoutedEventArgs e)
    {
        var code = ScriptBox.Text;
        if (string.IsNullOrWhiteSpace(code))
        {
            StatusText.Text = "脚本为空。";
            return;
        }

        RunButton.IsEnabled = false;
        BusyRing.IsActive = true;
        StatusText.Text = "运行中…";
        AppendLog("开始运行脚本");
        try
        {
            var result = await _scripts.RunAsync(code, AppendLog);
            if (result.Success)
            {
                if (!string.IsNullOrEmpty(result.ReturnValue)) AppendLog($"返回值：{result.ReturnValue}");
                StatusText.Text = "运行完成。";
            }
            else
            {
                AppendLog($"错误：{result.Error}");
                StatusText.Text = $"运行失败：{result.Error}";
            }
        }
        finally
        {
            RunButton.IsEnabled = true;
            BusyRing.IsActive = false;
        }
    }

    // ==================== 文件 ====================

    private async void OnOpenClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FileOpenPicker();
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
            picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
            picker.FileTypeFilter.Add(".lua");
            picker.FileTypeFilter.Add(".luaenc");
            picker.FileTypeFilter.Add(".txt");
            var file = await picker.PickSingleFileAsync();
            if (file is null) return;
            var content = await FileIO.ReadTextAsync(file);
            ScriptBox.Text = _scripts.Unprotect(content);
            StatusText.Text = $"已打开：{file.Name}";
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "打开脚本失败");
            StatusText.Text = $"打开失败：{ex.Message}";
        }
    }

    private async void OnSaveClick(object sender, RoutedEventArgs e) => await SaveAsync(encrypt: false);

    private async void OnEncryptSaveClick(object sender, RoutedEventArgs e) => await SaveAsync(encrypt: true);

    private async Task SaveAsync(bool encrypt)
    {
        try
        {
            var picker = new FileSavePicker();
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
            picker.FileTypeChoices.Add(encrypt ? "加密脚本" : "Lua 脚本", new[] { encrypt ? ".luaenc" : ".lua" });
            picker.SuggestedFileName = encrypt ? "script" : "script";
            var file = await picker.PickSaveFileAsync();
            if (file is null) return;
            var text = encrypt ? _scripts.Protect(ScriptBox.Text) : ScriptBox.Text;
            await File.WriteAllTextAsync(file.Path, text, new UTF8Encoding(false));
            StatusText.Text = $"已保存：{file.Path}";
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "保存脚本失败");
            StatusText.Text = $"保存失败：{ex.Message}";
        }
    }
}
