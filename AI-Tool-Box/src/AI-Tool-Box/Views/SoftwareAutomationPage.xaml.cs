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

/// <summary>工程/3D 软件自动化页：脚本 + 命令行驱动外部软件，AI 生成脚本。</summary>
public sealed partial class SoftwareAutomationPage : Page
{
    private readonly ILogger _logger;
    private readonly ISoftwareAutomationService _automation;
    private readonly IAiQuickService _ai;
    private readonly DispatcherQueue _dispatcher;

    /// <summary>初始化页面。</summary>
    public SoftwareAutomationPage()
    {
        InitializeComponent();
        _logger = App.Services.GetService(typeof(ILogger)) as ILogger ?? Serilog.Log.Logger;
        _automation = App.Services.GetRequiredService<ISoftwareAutomationService>();
        _ai = App.Services.GetRequiredService<IAiQuickService>();
        _dispatcher = DispatcherQueue.GetForCurrentThread() ?? DispatcherQueueController.CreateOnDedicatedThread().DispatcherQueue;
        ApplyPreset(0);
        _logger.Information("SoftwareAutomationPage 已加载");
    }

    private string SelectedPreset => PresetBox.SelectedItem as string ?? "自定义";

    private void OnPresetChanged(object sender, SelectionChangedEventArgs e)
    {
        // 构造期 SelectedIndex=0 会先于其它控件初始化触发，需判空
        if (ScriptBox is null || ArgsBox is null) return;
        ApplyPreset(PresetBox.SelectedIndex);
    }

    private void ApplyPreset(int index)
    {
        switch (index)
        {
            case 0:
                ArgsBox.Text = "--background --python \"{script}\"";
                ScriptBox.Text = SampleBlender;
                break;
            case 1:
                ArgsBox.Text = "\"{script}\"";
                ScriptBox.Text = SampleFreeCad;
                break;
            case 2:
                ArgsBox.Text = "/s \"{script}\"";
                ScriptBox.Text = SampleAutoCad;
                break;
            case 3:
                ArgsBox.Text = "\"{script}\"";
                ScriptBox.Text = "print('hello from AI-Tool-Box automation')\n";
                break;
            default:
                ArgsBox.Text = "\"{script}\"";
                break;
        }
    }

    private void AppendOutput(string line) => _dispatcher.TryEnqueue(() =>
    {
        OutputBox.Text += line + Environment.NewLine;
    });

    // ==================== 运行 ====================

    private async void OnRunClick(object sender, RoutedEventArgs e)
    {
        var exe = ExeBox.Text?.Trim() ?? string.Empty;
        if (exe.Length == 0)
        {
            StatusText.Text = "请先指定可执行文件路径。";
            return;
        }
        if (string.IsNullOrWhiteSpace(ScriptBox.Text))
        {
            StatusText.Text = "脚本内容为空。";
            return;
        }

        var ext = SelectedPreset switch
        {
            "AutoCAD" => ".scr",
            "Blender" or "FreeCAD" or "Python" => ".py",
            _ => ".py",
        };

        RunButton.IsEnabled = false;
        BusyRing.IsActive = true;
        OutputBox.Text = string.Empty;
        StatusText.Text = $"正在运行（{SelectedPreset}）…";
        try
        {
            var result = await _automation.RunAsync(exe, ArgsBox.Text, ScriptBox.Text, ext, AppendOutput);
            StatusText.Text = result.ExitCode == 0
                ? "执行完成（退出码 0）。"
                : $"执行结束，退出码 {result.ExitCode}（详见输出）。";
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "工程软件自动化失败");
            StatusText.Text = $"执行失败：{ex.Message}";
            AppendOutput($"错误：{ex.Message}");
        }
        finally
        {
            RunButton.IsEnabled = true;
            BusyRing.IsActive = false;
        }
    }

    // ==================== 文件 ====================

    private async void OnBrowseExeClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FileOpenPicker();
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
            picker.FileTypeFilter.Add(".exe");
            picker.SuggestedStartLocation = PickerLocationId.ComputerFolder;
            var file = await picker.PickSingleFileAsync();
            if (file is not null) ExeBox.Text = file.Path;
        }
        catch (Exception ex)
        {
            StatusText.Text = $"选择失败：{ex.Message}";
        }
    }

    private async void OnOpenClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FileOpenPicker();
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
            picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
            foreach (var ext in new[] { ".py", ".scr", ".lsp", ".txt", ".js" }) picker.FileTypeFilter.Add(ext);
            var file = await picker.PickSingleFileAsync();
            if (file is null) return;
            ScriptBox.Text = await FileIO.ReadTextAsync(file);
            StatusText.Text = $"已打开：{file.Name}";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"打开失败：{ex.Message}";
        }
    }

    private async void OnSaveClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FileSavePicker();
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
            picker.FileTypeChoices.Add("Python 脚本", new[] { ".py" });
            picker.FileTypeChoices.Add("AutoCAD 脚本", new[] { ".scr" });
            picker.FileTypeChoices.Add("文本", new[] { ".txt" });
            picker.SuggestedFileName = $"automation-{DateTime.Now:yyyyMMdd-HHmmss}";
            var file = await picker.PickSaveFileAsync();
            if (file is null) return;
            await File.WriteAllTextAsync(file.Path, ScriptBox.Text, new UTF8Encoding(false));
            StatusText.Text = $"已保存：{file.Path}";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"保存失败：{ex.Message}";
        }
    }

    private void OnSampleClick(object sender, RoutedEventArgs e) => ApplyPreset(PresetBox.SelectedIndex);

    // ==================== AI ====================

    private async void OnAiGenerateClick(object sender, RoutedEventArgs e)
    {
        var requirement = ScriptBox.Text;
        if (string.IsNullOrWhiteSpace(requirement))
        {
            StatusText.Text = "请先在脚本框中写下需求（例如：批量创建立方体阵列并导出 STL），再让 AI 生成脚本。";
            return;
        }

        BusyRing.IsActive = true;
        try
        {
            StatusText.Text = $"AI（{_ai.Describe()}）生成 {SelectedPreset} 脚本中…";
            var instruction = SelectedPreset switch
            {
                "Blender" => "你是 Blender Python 专家。根据需求生成可直接用 `blender --background --python` 执行的 Python 脚本，使用 bpy API；只输出代码，不要解释、不要 Markdown 代码块标记。",
                "FreeCAD" => "你是 FreeCAD Python 专家。根据需求生成可用 freecadcmd 执行的 Python 脚本（import FreeCAD, Part 等）；只输出代码，不要解释。",
                "AutoCAD" => "你是 AutoCAD 脚本专家。根据需求生成 AutoCAD 命令行脚本（.scr，逐行命令）；只输出脚本内容，不要解释。",
                _ => "你是编程助手。根据需求生成可直接运行的 Python 脚本；只输出代码，不要解释、不要 Markdown 代码块标记。",
            };
            var code = await _ai.AskAsync(instruction, requirement);
            code = code.Replace("```python", string.Empty).Replace("```", string.Empty).Trim();
            ScriptBox.Text = code;
            StatusText.Text = "AI 已生成脚本，请检查后运行。";
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "AI 生成脚本失败");
            StatusText.Text = $"AI 失败：{ex.Message}";
        }
        finally
        {
            BusyRing.IsActive = false;
        }
    }

    // ==================== 示例 ====================

    private const string SampleBlender = """
import bpy

# 清空场景
bpy.ops.object.select_all(action='SELECT')
bpy.ops.object.delete(use_global=False)

# 创建立方体阵列
for i in range(5):
    bpy.ops.mesh.primitive_cube_add(location=(i * 2.5, 0, 1))
    bpy.context.object.name = f"Cube_{i}"

# 导出 STL（路径按需修改）
bpy.ops.export_mesh.stl(filepath="C:/Temp/array.stl")
print("done")
""";

    private const string SampleFreeCad = """
import FreeCAD, Part

doc = FreeCAD.newDocument("AutoBox")
box = doc.addObject("Part::Box", "Box")
box.Length = 20
box.Width = 10
box.Height = 5
doc.recompute()
Part.export([box], "C:/Temp/box.step")
print("done")
""";

    private const string SampleAutoCad = """
; AutoCAD 脚本示例：绘制矩形并缩放
RECTANG
0,0
100,60
ZOOM
E
""";
}
