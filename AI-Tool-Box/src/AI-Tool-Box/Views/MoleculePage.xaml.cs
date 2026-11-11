using AI_Tool_Box.Controls;
using AI_Tool_Box.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.Web.WebView2.Core;
using Serilog;
using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace AI_Tool_Box.Views;

/// <summary>分子模拟器页：SMILES → 2D 结构（本地 SmilesDrawer）+ AI 名称互转 / 性质解释。</summary>
public sealed partial class MoleculePage : Page
{
    private readonly ILogger _logger;
    private readonly IAiQuickService _ai;
    private bool _webReady;

    /// <summary>初始化页面。</summary>
    public MoleculePage()
    {
        InitializeComponent();
        _logger = App.Services.GetService(typeof(ILogger)) as ILogger ?? Serilog.Log.Logger;
        _ai = App.Services.GetRequiredService<IAiQuickService>();
        Loaded += OnLoaded;
        _logger.Information("MoleculePage 已加载");
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            var env = await WebShell.GetSharedEnvironmentAsync();
            await Preview.EnsureCoreWebView2Async(env);
            Preview.DefaultBackgroundColor = Windows.UI.Color.FromArgb(255, 10, 15, 20);
            Preview.CoreWebView2.WebMessageReceived += OnWebMessage;
            Preview.Source = new Uri(Path.Combine(AppContext.BaseDirectory, "Assets", "Web", "molecule", "index.html"));
            RefreshReactionTypeset();
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "分子结构预览初始化失败");
            StatusText.Text = $"预览初始化失败：{ex.Message}";
        }
    }

    private void OnDrawClick(object sender, RoutedEventArgs e) => Render(SmilesBox.Text);

    /// <summary>化学反应预测：ΔH / ΔS / ΔG / 平衡常数 / Arrhenius 速率（教学级估算）。</summary>
    private void OnPredictReactionClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var result = ReactionPredictService.Predict(
                ReactantsBox.Text, ProductsBox.Text, TempBox.Text, EaBox.Text);
            ReactionResultText.Text = result.Text;
            StatusText.Text = result.Ok
                ? "反应预测完成（估算）：可修改温度 / 活化能后重新预测。"
                : "反应预测完成，但反应式未配平，请检查输入。";
            _logger.Information("化学反应预测：{Reactants} -> {Products}", ReactantsBox.Text, ProductsBox.Text);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "化学反应预测失败");
            ReactionResultText.Text = $"预测失败：{ex.Message}";
            StatusText.Text = "预测失败：请检查 SMILES 是否正确。";
        }
    }

    /// <summary>反应物/产物输入变化时，刷新二维（卡西欧式）排版预览。</summary>
    private void OnReactionInputChanged(object sender, TextChangedEventArgs e) => RefreshReactionTypeset();

    private void RefreshReactionTypeset()
    {
        if (ReactionTypesetHost is null) return;
        ReactionTypesetHost.Children.Clear();
        var left = ReactantsBox?.Text ?? string.Empty;
        var right = ProductsBox?.Text ?? string.Empty;
        var brush = ReactantsBox?.Foreground
                    ?? new SolidColorBrush(Windows.UI.Color.FromArgb(255, 40, 48, 58));
        var text = string.IsNullOrWhiteSpace(right) ? left : $"{left} + {right}";
        ReactionTypesetHost.Children.Add(FormulaTypesetter.Build(text, 20, brush,
            "例：2H_{2} + O_{2} -> 2H_{2}O  或  C_{2}H_{4} + H_{2} -> C_{2}H_{6}"));
    }

    private void Render(string? smiles)
    {
        if (!_webReady || Preview.CoreWebView2 is null) return;
        Preview.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new { type = "render", smiles = smiles ?? string.Empty }));
        StatusText.Text = "渲染中…";
    }

    private void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var doc = JsonDocument.Parse(e.WebMessageAsJson);
            var root = doc.RootElement;
            var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;

            switch (type)
            {
                case "ready":
                    _webReady = true;
                    Render(SmilesBox.Text);
                    break;

                case "meta":
                    UpdateMeta(root);
                    break;

                case "error":
                    StatusText.Text = $"渲染失败：{GetString(root, "message")}";
                    break;

                case "export":
                    _ = SaveExportAsync(GetString(root, "format"), GetString(root, "data"));
                    break;

                case "smiles":
                    var built = GetString(root, "smiles");
                    if (!string.IsNullOrWhiteSpace(built))
                    {
                        SmilesBox.Text = built;
                        StatusText.Text = $"已根据画布结构生成 SMILES：{built}";
                    }
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "处理分子预览消息失败");
        }
    }

    private void UpdateMeta(JsonElement root)
    {
        var formula = GetString(root, "formula");
        var smiles = GetString(root, "smiles");
        if (root.TryGetProperty("info", out var info))
        {
            var mw = info.TryGetProperty("mw", out var mwEl) ? mwEl.GetDouble() : 0;
            var atoms = info.TryGetProperty("atoms", out var atEl) ? atEl.GetInt32() : 0;
            var head = string.IsNullOrEmpty(smiles) ? "原子拼装" : $"SMILES：{smiles}";
            InfoText.Text = $"{head}\n分子式：{(string.IsNullOrEmpty(formula) ? "—" : formula)}\n"
                            + $"分子量：{mw:0.##} g/mol\n原子总数（含氢）：{atoms}";
        }
        else
        {
            InfoText.Text = $"SMILES：{smiles}\n分子式：{(string.IsNullOrEmpty(formula) ? "—" : formula)}";
        }
        StatusText.Text = "渲染完成。";
    }

    private static string GetString(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) ? (v.GetString() ?? string.Empty) : string.Empty;

    // ==================== 导出 ====================

    private void OnExportSvgClick(object sender, RoutedEventArgs e)
    {
        if (!_webReady || Preview.CoreWebView2 is null) return;
        Preview.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new { type = "export-svg" }));
        StatusText.Text = "正在导出 SVG…";
    }

    private void OnExportPngClick(object sender, RoutedEventArgs e)
    {
        if (!_webReady || Preview.CoreWebView2 is null) return;
        Preview.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new { type = "export-png" }));
        StatusText.Text = "正在导出 PNG…";
    }

    private async Task SaveExportAsync(string format, string data)
    {
        try
        {
            if (string.IsNullOrEmpty(data))
            {
                StatusText.Text = "没有可导出的结构（请先成功绘制）。";
                return;
            }
            var picker = new FileSavePicker();
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
            if (format == "png") picker.FileTypeChoices.Add("PNG 图片", new[] { ".png" });
            else picker.FileTypeChoices.Add("SVG 图片", new[] { ".svg" });
            picker.SuggestedFileName = $"molecule-{DateTime.Now:yyyyMMdd-HHmmss}";
            var file = await picker.PickSaveFileAsync();
            if (file is null) return;

            if (format == "png") await FileIO.WriteBytesAsync(file, Convert.FromBase64String(data));
            else await FileIO.WriteTextAsync(file, data);
            StatusText.Text = $"已导出：{file.Path}";
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "导出分子结构失败");
            StatusText.Text = $"导出失败：{ex.Message}";
        }
    }

    // ==================== AI ====================

    private async void OnAiNameToSmilesClick(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text;
        if (string.IsNullOrWhiteSpace(name))
        {
            StatusText.Text = "请先输入化学名称。";
            return;
        }
        BusyRing.IsActive = true;
        try
        {
            StatusText.Text = $"AI（{_ai.Describe()}）转换中…";
            var smiles = await _ai.AskAsync(
                "你是化学信息学助手。把用户给出的化学名称（中文/英文/IUPAC）转换为标准 SMILES。只输出 SMILES 字符串本身，不要解释、不要代码块。若无法确定，输出 UNKNOWN。",
                name);
            smiles = smiles.Trim().Trim('`').Replace("```", string.Empty).Trim();
            if (smiles.Equals("UNKNOWN", StringComparison.OrdinalIgnoreCase) || smiles.Length == 0)
            {
                StatusText.Text = "AI 无法确定该名称对应的 SMILES，请换用更规范的名称或直接输入 SMILES。";
                return;
            }
            SmilesBox.Text = smiles;
            Render(smiles);
            StatusText.Text = $"AI 生成 SMILES：{smiles}";
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "AI 名称转 SMILES 失败");
            StatusText.Text = $"AI 失败：{ex.Message}";
        }
        finally
        {
            BusyRing.IsActive = false;
        }
    }

    private async void OnAiExplainClick(object sender, RoutedEventArgs e)
    {
        var smiles = SmilesBox.Text;
        if (string.IsNullOrWhiteSpace(smiles))
        {
            StatusText.Text = "请先输入 SMILES。";
            return;
        }
        BusyRing.IsActive = true;
        try
        {
            AiOutput.Text = $"AI（{_ai.Describe()}）分析中…";
            var content = $"SMILES：{smiles}\n名称：{NameBox.Text}\n分子信息：{InfoText.Text}";
            AiOutput.Text = await _ai.AskAsync(
                "你是化学老师。请根据 SMILES 说明：1) 中文名称与常见用途；2) 官能团与结构特征；3) 主要物理化学性质；4) 安全注意事项。用中文 Markdown 简要输出。",
                content);
            StatusText.Text = "AI 解释完成。";
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "AI 解释分子失败");
            AiOutput.Text = $"AI 失败：{ex.Message}";
        }
        finally
        {
            BusyRing.IsActive = false;
        }
    }
}
