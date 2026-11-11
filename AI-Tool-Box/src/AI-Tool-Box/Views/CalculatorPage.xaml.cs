using AI_Tool_Box.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Serilog;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Color = Windows.UI.Color;

namespace AI_Tool_Box.Views;

/// <summary>
/// 科学计算器 + 函数绘图页：支持 LaTeX / 自然书写表达式（\frac、\sqrt、^{}、\pi），
/// 白色坐标系（刻度 + 网格）与可拖动的曲线取点（类几何画板）。
/// </summary>
public sealed partial class CalculatorPage : Page
{
    private readonly ILogger _logger;
    private readonly IAiQuickService _ai;
    private readonly System.Collections.ObjectModel.ObservableCollection<string> _history = new();

    private double _xMin = -10, _xMax = 10;
    private double _yMin = -1, _yMax = 1;

    // 绘图区几何（含留白，用于绘制刻度标签）
    private double _plotLeft, _plotTop, _plotWidth, _plotHeight;

    private List<(double X, double Y)> _plotPoints = new();
    private List<(double X, double Y)> _derivPoints = new();
    private string _normalizedPlot = string.Empty;
    private double _markerX = double.NaN;
    private bool _draggingMarker;

    // 缩放 / 平移：滚轮围绕光标缩放，按住右键拖动平移
    private bool _manualRange;
    private bool _panningPlot;
    private Windows.Foundation.Point _panLast;
    private const double MinPlotSpan = 1e-4;
    private const double MaxPlotSpan = 1e7;

    // 「滚轮居中缩放」开关：开 → 滚轮以绘图区中心缩放；关 → 滚轮围绕光标缩放。
    // 左键拖动红点取点、拖动其他位置平移图像（左右 + 上下）与右键拖动平移始终有效。
    private bool _panMode;

    /// <summary>初始化页面。</summary>
    public CalculatorPage()
    {
        InitializeComponent();
        _logger = App.Services.GetService(typeof(ILogger)) as ILogger ?? Serilog.Log.Logger;
        _ai = App.Services.GetRequiredService<IAiQuickService>();
        HistoryList.ItemsSource = _history;
        Loaded += (_, _) =>
        {
            _logger.Information("CalculatorPage 已加载");
            RefreshExprTypeset();
            RefreshPlotTypeset();
        };
    }

    // ==================== 计算 ====================

    private void OnExprKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter) OnCalculateClick(sender, e);
    }

    private void OnCalculateClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            var normalized = ExpressionEvaluator.NormalizeMathInput(ExprBox.Text);
            var x = ParseDouble(XBox.Text, 0);
            var y = ParseDouble(YBox.Text, 0);
            var value = ExpressionEvaluator.Evaluate(ExprBox.Text, x, y);
            var display = FormatNumber(value);
            var hasVar = normalized.Contains('x') || normalized.Contains('y');
            var label = hasVar
                ? $"x={FormatNumber(x)}, y={FormatNumber(y)} → {display}"
                : display;
            ResultText.Text = label;
            _history.Insert(0, $"{ExprBox.Text}   =   {label}");
            if (_history.Count > 200) _history.RemoveAt(_history.Count - 1);
            StatusText.Text = normalized == (ExprBox.Text ?? string.Empty).Trim()
                ? "计算成功。"
                : $"计算成功。已解析为：{normalized}";
        }
        catch (Exception ex)
        {
            ResultText.Text = "错误";
            StatusText.Text = $"计算失败：{ex.Message}";
        }
    }

    // ==================== 微积分：求导 / 二阶导 / 积分 / 偏导 ====================

    private void OnCalculusClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button) return;
        var tag = button.Tag as string ?? string.Empty;
        var expr = ExprBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(expr))
        {
            StatusText.Text = "请先在表达式框输入被求导/积分的函数。";
            return;
        }

        var x = ParseDouble(XBox.Text, 0);
        var y = ParseDouble(YBox.Text, 0);
        try
        {
            string headline;
            switch (tag)
            {
                case "deriv":
                {
                    var v = ExpressionEvaluator.Derivative(expr, x, y, "x");
                    headline = $"f′({FormatNumber(x)}) = {FormatNumber(v)}";
                    break;
                }
                case "deriv2":
                {
                    var v = ExpressionEvaluator.SecondDerivative(expr, x, y);
                    headline = $"f″({FormatNumber(x)}) = {FormatNumber(v)}";
                    break;
                }
                case "partialX":
                {
                    var v = ExpressionEvaluator.Derivative(expr, x, y, "x");
                    headline = $"∂f/∂x|_{FormatNumber(x)},{FormatNumber(y)} = {FormatNumber(v)}";
                    break;
                }
                case "partialY":
                {
                    var v = ExpressionEvaluator.Derivative(expr, x, y, "y");
                    headline = $"∂f/∂y|_{FormatNumber(x)},{FormatNumber(y)} = {FormatNumber(v)}";
                    break;
                }
                case "gradient":
                {
                    var fx = ExpressionEvaluator.Derivative(expr, x, y, "x");
                    var fy = ExpressionEvaluator.Derivative(expr, x, y, "y");
                    headline = $"∇f = ({FormatNumber(fx)}, {FormatNumber(fy)})";
                    break;
                }
                case "diffTotal":
                {
                    var fx = ExpressionEvaluator.Derivative(expr, x, y, "x");
                    var fy = ExpressionEvaluator.Derivative(expr, x, y, "y");
                    headline = $"df = {FormatNumber(fx)}·dx + {FormatNumber(fy)}·dy";
                    break;
                }
                case "integral":
                {
                    var body = expr;
                    var a = ParseDouble(ABox.Text, 0);
                    var b = ParseDouble(BBox.Text, 1);
                    if (TryParseIntegral(expr, out var inner, out var lo, out var hi))
                    {
                        body = inner;
                        a = lo ?? a;
                        b = hi ?? b;
                    }
                    var v = ExpressionEvaluator.Integrate(body, a, b);
                    headline = $"∫[{FormatNumber(a)}, {FormatNumber(b)}] {body} dx = {FormatNumber(v)}";
                    break;
                }
                case "integralX":
                {
                    var v = ExpressionEvaluator.Integrate(expr, 0, x);
                    headline = $"∫[0, {FormatNumber(x)}] {expr} dx = {FormatNumber(v)}";
                    break;
                }
                default:
                    return;
            }
            ResultText.Text = headline;
            _history.Insert(0, headline);
            if (_history.Count > 200) _history.RemoveAt(_history.Count - 1);
            StatusText.Text = $"微积分计算完成（数值方法）：{headline}";
        }
        catch (Exception ex)
        {
            ResultText.Text = "错误";
            StatusText.Text = $"微积分计算失败：{ex.Message}";
        }
    }

    /// <summary>识别整段 LaTeX 积分式：\int_{a}^{b} f(x) dx / ∫_a^b f dx。</summary>
    private static bool TryParseIntegral(string input, out string body, out double? lower, out double? upper)
    {
        body = input;
        lower = null;
        upper = null;
        var match = System.Text.RegularExpressions.Regex.Match(
            input,
            @"(?:\\int|∫)\s*(?:_\{([^}]*)\}|_([^\s^]+))?\s*(?:\^\{([^}]*)\}|\^([^\s]+))?\s*(.*?)\s*(?:\\,)?(?:\\mathrm\{d\}|d)\s*[a-z]?\s*$",
            System.Text.RegularExpressions.RegexOptions.Singleline);
        if (!match.Success) return false;
        body = match.Groups[5].Value.Trim();
        if (string.IsNullOrWhiteSpace(body)) return false;
        var lo = match.Groups[1].Success && match.Groups[1].Value.Length > 0
            ? match.Groups[1].Value : match.Groups[2].Value;
        var hi = match.Groups[3].Success && match.Groups[3].Value.Length > 0
            ? match.Groups[3].Value : match.Groups[4].Value;
        if (double.TryParse(lo, NumberStyles.Float, CultureInfo.InvariantCulture, out var a)) lower = a;
        if (double.TryParse(hi, NumberStyles.Float, CultureInfo.InvariantCulture, out var b)) upper = b;
        return true;
    }

    private void OnClearHistoryClick(object sender, RoutedEventArgs e) => _history.Clear();

    private void OnHistoryDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (HistoryList.SelectedItem is not string s) return;
        var expr = s.Split("   =   ")[0];
        ExprBox.Text = expr;
        PlotExprBox.Text = expr.Contains('x') ? expr : PlotExprBox.Text;
    }

    private async void OnAiParseClick(object sender, RoutedEventArgs e)
    {
        var input = ExprBox.Text;
        if (string.IsNullOrWhiteSpace(input))
        {
            StatusText.Text = "请先在表达式框中输入题目或描述，再让 AI 转成表达式。";
            return;
        }
        try
        {
            StatusText.Text = $"AI（{_ai.Describe()}）解析中…";
            var expr = await _ai.AskAsync(
                "你是数学表达式转换器。把用户输入的中文题目/描述转换为一个可计算的表达式，"
                + "只输出表达式本身（不要等号、不要解释、不要代码块）。可用：+ - * / ^ ! 括号、sqrt ln log log2 abs exp sin cos tan cot asin acos atan min max pow、常量 pi e、变量 x。"
                + "也可以使用 LaTeX（\\frac{}{}、\\sqrt{}、^{}、\\pi）。",
                input);
            expr = expr.Trim().Trim('`').Replace("```", string.Empty).Trim();
            ExprBox.Text = expr;
            StatusText.Text = $"AI 已生成表达式：{expr}";
            OnCalculateClick(null, new RoutedEventArgs());
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "AI 解析表达式失败");
            StatusText.Text = $"AI 解析失败：{ex.Message}";
        }
    }

    // ==================== 数学键盘（卡西欧式模板） ====================

    private void OnKeypadClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button) return;
        var tag = button.Tag as string ?? string.Empty;
        if (tag == "clear")
        {
            ExprBox.Text = string.Empty;
            ExprBox.Focus(FocusState.Programmatic);
            return;
        }
        if (tag == "del")
        {
            var current = ExprBox.Text ?? string.Empty;
            var delCaret = Math.Clamp(ExprBox.SelectionStart, 0, current.Length);
            var selectedLength = Math.Clamp(ExprBox.SelectionLength, 0, current.Length - delCaret);
            if (selectedLength > 0)
            {
                ExprBox.Text = current[..delCaret] + current[(delCaret + selectedLength)..];
                ExprBox.SelectionStart = delCaret;
            }
            else if (delCaret > 0)
            {
                ExprBox.Text = current[..(delCaret - 1)] + current[delCaret..];
                ExprBox.SelectionStart = delCaret - 1;
            }
            ExprBox.SelectionLength = 0;
            ExprBox.Focus(FocusState.Programmatic);
            return;
        }
        var text = ExprBox.Text ?? string.Empty;
        var start = Math.Clamp(ExprBox.SelectionStart, 0, text.Length);
        var length = Math.Clamp(ExprBox.SelectionLength, 0, text.Length - start);
        var selected = text.Substring(start, length);

        // "@" 表示插入后的光标位置
        var template = tag switch
        {
            "frac" => "\\frac{@}{}",
            "sqrt" => "\\sqrt{@}",
            "root3" => "\\sqrt[3]{@}",
            "rootn" => "\\sqrt[@]{}",
            "pow" => "^{@}",
            "^" => "^{@}",
            "epow" => "e^{@}",
            "pi" => "\\pi",
            _ => tag,
        };

        var caretInTemplate = template.IndexOf('@');
        var insert = template.Replace("@", selected);
        var caret = caretInTemplate >= 0
            ? start + caretInTemplate + selected.Length
            : start + insert.Length;

        ExprBox.Text = text[..start] + insert + text[(start + length)..];
        ExprBox.SelectionStart = caret;
        ExprBox.SelectionLength = 0;
        ExprBox.Focus(FocusState.Programmatic);
    }

    // ==================== 绘图 ====================

    private void OnCanvasSizeChanged(object sender, SizeChangedEventArgs e) => DrawPlot();

    private void OnPlotClick(object sender, RoutedEventArgs e)
    {
        _xMin = ParseDouble(XMinBox.Text, -10);
        _xMax = ParseDouble(XMaxBox.Text, 10);
        if (_xMax <= _xMin)
        {
            StatusText.Text = "x 范围无效：最大值必须大于最小值。";
            return;
        }
        _markerX = double.NaN;
        _manualRange = false;
        DrawPlot();
    }

    private void OnResetViewClick(object sender, RoutedEventArgs e)
    {
        XMinBox.Text = "-10";
        XMaxBox.Text = "10";
        PlotExprBox.Text = "sin(x)";
        _markerX = double.NaN;
        _manualRange = false;
        OnPlotClick(sender, e);
    }

    private void OnGridToggleClick(object sender, RoutedEventArgs e) => DrawPlot();

    /// <summary>清除函数表达式与当前绘图内容。</summary>
    private void OnPlotClearClick(object sender, RoutedEventArgs e)
    {
        PlotExprBox.Text = string.Empty;
        _plotPoints = new List<(double X, double Y)>();
        _derivPoints = new List<(double X, double Y)>();
        _markerX = double.NaN;
        PlotCanvas.Children.Clear();
        TraceText.Text = "已清除。输入 f(x) 后点击「绘制」。";
        StatusText.Text = "已清除函数表达式与绘图。";
    }

    /// <summary>
    /// 「滚轮居中缩放」开关：开启后滚轮以绘图区中心缩放；关闭后滚轮围绕鼠标位置缩放。
    /// 左键拖动曲线上的红点取点、拖动其他位置平移图像（左右 + 上下）始终有效。
    /// </summary>
    private void OnPanToggleClick(object sender, RoutedEventArgs e)
    {
        _panMode = PanToggle.IsChecked == true;
        TraceText.Text = _panMode
            ? "滚轮以绘图区中心缩放。左键拖动红点取点，拖动其他位置平移图像（左右 + 上下）。"
            : "滚轮围绕鼠标位置缩放。左键拖动红点取点，拖动其他位置平移图像（左右 + 上下）。";
    }

    /// <summary>把 LaTeX 模板插入绘图表达式框（"@" 表示插入后的光标位置）。</summary>
    private void OnPlotKeypadClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button) return;
        var tag = button.Tag as string ?? string.Empty;
        var text = PlotExprBox.Text ?? string.Empty;
        var start = Math.Clamp(PlotExprBox.SelectionStart, 0, text.Length);
        var length = Math.Clamp(PlotExprBox.SelectionLength, 0, text.Length - start);
        var selected = text.Substring(start, length);

        var template = tag switch
        {
            "frac" => "\\frac{@}{}",
            "sqrt" => "\\sqrt{@}",
            "rootn" => "\\sqrt[@]{}",
            "pow" => "^{@}",
            "pi" => "\\pi",
            "exp" => "e^{@}",
            _ => tag,
        };

        var caretInTemplate = template.IndexOf('@');
        var insert = template.Replace("@", selected);
        var caret = caretInTemplate >= 0
            ? start + caretInTemplate + selected.Length
            : start + insert.Length;

        PlotExprBox.Text = text[..start] + insert + text[(start + length)..];
        PlotExprBox.SelectionStart = caret;
        PlotExprBox.SelectionLength = 0;
        PlotExprBox.Focus(FocusState.Programmatic);
    }

    /// <summary>计算器表达式文本变化时刷新二维类型化预览（真分数、根号、上下标）。</summary>
    private void OnExprTextChanged(object sender, TextChangedEventArgs e) => RefreshExprTypeset();

    /// <summary>绘图表达式文本变化时刷新二维类型化预览（真分数、根号、上下标）。</summary>
    private void OnPlotExprChanged(object sender, TextChangedEventArgs e) => RefreshPlotTypeset();

    /// <summary>把计算器表达式排成“看得懂”的二维公式；公式里的空位可点击后直接把光标移入输入框。</summary>
    private void RefreshExprTypeset()
    {
        if (ExprTypesetHost is null) return;
        ExprTypesetHost.Children.Clear();
        ExprTypesetHost.Children.Add(FormulaTypesetter.Build(
            ExprBox?.Text, 22, TextBrush(ExprBox),
            "在这里输入表达式，会用二维排版显示（真分数、根号、上下标），像卡西欧那样直接看懂。",
            FocusExprAt));
    }

    /// <summary>把绘图表达式排成“看得懂”的二维公式；公式里的空位可点击后直接把光标移入输入框。</summary>
    private void RefreshPlotTypeset()
    {
        if (PlotTypesetHost is null) return;
        PlotTypesetHost.Children.Clear();
        PlotTypesetHost.Children.Add(FormulaTypesetter.Build(
            PlotExprBox?.Text, 22, TextBrush(PlotExprBox),
            "输入 f(x) 后，这里用二维排版显示（真分数、根号、上下标），像卡西欧那样直接看懂。",
            FocusPlotAt));
    }

    /// <summary>把输入光标移动到表达式的指定位置（用于点击公式空位后直接输入）。</summary>
    private static void MoveCaret(TextBox box, int offset)
    {
        var len = box.Text?.Length ?? 0;
        box.Focus(FocusState.Programmatic);
        box.SelectionStart = Math.Clamp(offset, 0, len);
        box.SelectionLength = 0;
    }

    private void FocusExprAt(int offset) => MoveCaret(ExprBox, offset);

    private void FocusPlotAt(int offset) => MoveCaret(PlotExprBox, offset);

    private static Brush TextBrush(Control? anchor)
    {
        // 跟随当前主题的文本色，避免在深色主题下看不清公式
        return anchor?.Foreground ?? new SolidColorBrush(Color.FromArgb(255, 40, 48, 58));
    }

    /// <summary>
    /// 滚轮缩放：默认围绕鼠标位置缩放；开启「滚轮居中缩放」后改为围绕绘图区域中心缩放，便于整体平移观察。
    /// </summary>
    private void OnPlotWheel(object sender, PointerRoutedEventArgs e)
    {
        if (_plotWidth <= 1 || _plotHeight <= 1) return;
        var pt = e.GetCurrentPoint(PlotCanvas);
        var pos = pt.Position;

        double wx, wy;
        if (_panMode)
        {
            // 拖动模式：以绘图区域中心为缩放锚点
            wx = (_xMin + _xMax) / 2;
            wy = (_yMin + _yMax) / 2;
        }
        else
        {
            if (pos.X < _plotLeft || pos.X > _plotLeft + _plotWidth) return;
            if (pos.Y < _plotTop || pos.Y > _plotTop + _plotHeight) return;
            wx = _xMin + (pos.X - _plotLeft) / _plotWidth * (_xMax - _xMin);
            wy = _yMax - (pos.Y - _plotTop) / _plotHeight * (_yMax - _yMin);
        }

        var factor = pt.Properties.MouseWheelDelta > 0 ? 0.8 : 1.25;
        var spanX = Math.Clamp((_xMax - _xMin) * factor, MinPlotSpan, MaxPlotSpan);
        var spanY = Math.Clamp((_yMax - _yMin) * factor, MinPlotSpan, MaxPlotSpan);
        var ratioX = Math.Clamp((wx - _xMin) / (_xMax - _xMin), 0, 1);
        var ratioY = Math.Clamp((_yMax - wy) / (_yMax - _yMin), 0, 1);

        _xMin = wx - ratioX * spanX;
        _xMax = _xMin + spanX;
        _yMax = wy + ratioY * spanY;
        _yMin = _yMax - spanY;
        _manualRange = true;
        SyncRangeInputs();
        e.Handled = true;
        DrawPlot();
    }

    /// <summary>把当前视图范围同步回 x 范围输入框，便于用户直接看到数值。</summary>
    private void SyncRangeInputs()
    {
        XMinBox.Text = FormatNumber(_xMin);
        XMaxBox.Text = FormatNumber(_xMax);
    }

    private void DrawPlot()
    {
        var w = PlotCanvas.ActualWidth;
        var h = PlotCanvas.ActualHeight;
        PlotCanvas.Children.Clear();
        if (w < 80 || h < 60) return;

        var expr = PlotExprBox.Text;
        if (string.IsNullOrWhiteSpace(expr)) return;

        _normalizedPlot = ExpressionEvaluator.NormalizeMathInput(expr);
        _plotPoints = ExpressionEvaluator.Sample(expr, _xMin, _xMax, 500);
        if (_plotPoints.Count < 2)
        {
            StatusText.Text = "该函数在给定区间内没有有效取值。";
            return;
        }

        _yMin = _plotPoints.Min(p => p.Y);
        _yMax = _plotPoints.Max(p => p.Y);

        // 可选叠加导函数 f′(x)
        _derivPoints = DerivToggle.IsChecked == true
            ? ExpressionEvaluator.SampleDerivative(expr, _xMin, _xMax, 500)
            : new List<(double X, double Y)>();
        if (_derivPoints.Count > 0)
        {
            var limit = Math.Max(Math.Abs(_yMin), Math.Abs(_yMax)) * 6 + 10;
            _derivPoints = _derivPoints.Where(p => Math.Abs(p.Y) <= limit).ToList();
            if (_derivPoints.Count > 0)
            {
                _yMin = Math.Min(_yMin, _derivPoints.Min(p => p.Y));
                _yMax = Math.Max(_yMax, _derivPoints.Max(p => p.Y));
            }
        }

        // 缩放/平移后保留用户指定的 y 范围，不再按采样自动适配
        if (!_manualRange)
        {
            if (_yMax - _yMin < 1e-9)
            {
                _yMin -= 1;
                _yMax += 1;
            }
            var padY = (_yMax - _yMin) * 0.08;
            _yMin -= padY;
            _yMax += padY;
        }
        else if (_yMax <= _yMin)
        {
            _yMin = _yMax - 1;
        }

        // 首次绘制时把取点放在有效位置，保证图像上始终有一个可拖动的红点
        if (double.IsNaN(_markerX))
        {
            foreach (var candidate in new[] { 0.0, (_xMin + _xMax) / 2, _xMin + (_xMax - _xMin) * 0.25 })
            {
                if (candidate < _xMin || candidate > _xMax) continue;
                try
                {
                    var sample = ExpressionEvaluator.Evaluate(_normalizedPlot, candidate);
                    if (!double.IsNaN(sample) && !double.IsInfinity(sample))
                    {
                        _markerX = candidate;
                        break;
                    }
                }
                catch
                {
                    // 该点无定义，尝试下一个候选位置
                }
            }
            if (double.IsNaN(_markerX)) _markerX = Math.Clamp(0, _xMin, _xMax);
        }

        // 留白：左侧放 y 轴刻度，底部放 x 轴刻度
        const double leftPad = 48, rightPad = 16, topPad = 14, bottomPad = 30;
        _plotLeft = leftPad;
        _plotTop = topPad;
        _plotWidth = Math.Max(20, w - leftPad - rightPad);
        _plotHeight = Math.Max(20, h - topPad - bottomPad);

        double MapX(double x) => _plotLeft + (x - _xMin) / (_xMax - _xMin) * _plotWidth;
        double MapY(double y) => _plotTop + (_yMax - y) / (_yMax - _yMin) * _plotHeight;

        var gridBrush = new SolidColorBrush(Color.FromArgb(255, 232, 237, 242));
        var axisBrush = new SolidColorBrush(Color.FromArgb(255, 90, 110, 125));
        var curveBrush = new SolidColorBrush(Color.FromArgb(255, 21, 101, 192));
        var labelBrush = new SolidColorBrush(Color.FromArgb(255, 96, 112, 128));

        // 网格 + 刻度标签
        var showGrid = GridToggle.IsChecked != false;
        var stepX = NiceStep((_xMax - _xMin) / 10);
        var stepY = NiceStep((_yMax - _yMin) / 8);

        for (var gx = Math.Ceiling(_xMin / stepX) * stepX; gx <= _xMax + stepX * 1e-6; gx += stepX)
        {
            var px = MapX(gx);
            if (px < _plotLeft - 1 || px > _plotLeft + _plotWidth + 1) continue;
            if (showGrid)
            {
                PlotCanvas.Children.Add(new Line
                {
                    X1 = px, X2 = px, Y1 = _plotTop, Y2 = _plotTop + _plotHeight,
                    Stroke = gridBrush, StrokeThickness = 1
                });
            }
            if (Math.Abs(gx) > 1e-9)
            {
                AddLabel(FormatTick(gx), px - 26, _plotTop + _plotHeight + 5, 52,
                    TextAlignment.Center, labelBrush);
            }
        }

        for (var gy = Math.Ceiling(_yMin / stepY) * stepY; gy <= _yMax + stepY * 1e-6; gy += stepY)
        {
            var py = MapY(gy);
            if (py < _plotTop - 1 || py > _plotTop + _plotHeight + 1) continue;
            if (showGrid)
            {
                PlotCanvas.Children.Add(new Line
                {
                    X1 = _plotLeft, X2 = _plotLeft + _plotWidth, Y1 = py, Y2 = py,
                    Stroke = gridBrush, StrokeThickness = 1
                });
            }
            if (Math.Abs(gy) > 1e-9)
            {
                AddLabel(FormatTick(gy), 0, py - 9, _plotLeft - 6, TextAlignment.Right, labelBrush);
            }
        }

        // 坐标轴
        var axisY = (_yMin <= 0 && _yMax >= 0) ? MapY(0) : _plotTop + _plotHeight;
        var axisX = (_xMin <= 0 && _xMax >= 0) ? MapX(0) : _plotLeft;
        PlotCanvas.Children.Add(new Line
        {
            X1 = _plotLeft, X2 = _plotLeft + _plotWidth, Y1 = axisY, Y2 = axisY,
            Stroke = axisBrush, StrokeThickness = 1.5
        });
        PlotCanvas.Children.Add(new Line
        {
            X1 = axisX, X2 = axisX, Y1 = _plotTop, Y2 = _plotTop + _plotHeight,
            Stroke = axisBrush, StrokeThickness = 1.5
        });
        AddLabel("x", _plotLeft + _plotWidth + 2, axisY - 18, 16, TextAlignment.Left, axisBrush);
        AddLabel("y", axisX + 4, _plotTop - 2, 16, TextAlignment.Left, axisBrush);
        AddLabel("0", _plotLeft - 16, axisY + 2, 14, TextAlignment.Right, labelBrush);

        // 曲线（遇到 NaN 或大幅跳变时断开）
        Polyline? line = null;
        double? prevY = null;
        var jumpLimit = (_yMax - _yMin) * 0.9;
        foreach (var (x, y) in _plotPoints)
        {
            if (prevY is not null && Math.Abs(y - prevY.Value) > jumpLimit)
            {
                line = null;
            }
            if (line is null)
            {
                line = new Polyline { Stroke = curveBrush, StrokeThickness = 2, StrokeLineJoin = PenLineJoin.Round };
                PlotCanvas.Children.Add(line);
            }
            line.Points.Add(new Windows.Foundation.Point(MapX(x), MapY(y)));
            prevY = y;
        }

        // 导函数曲线（橙色虚线）
        if (_derivPoints.Count > 1)
        {
            var derivBrush = new SolidColorBrush(Color.FromArgb(255, 230, 126, 34));
            Polyline? dLine = null;
            double? prevDy = null;
            foreach (var (x, y) in _derivPoints)
            {
                if (prevDy is not null && Math.Abs(y - prevDy.Value) > jumpLimit) dLine = null;
                if (dLine is null)
                {
                    dLine = new Polyline
                    {
                        Stroke = derivBrush,
                        StrokeThickness = 1.6,
                        StrokeDashArray = new DoubleCollection { 5, 3 },
                    };
                    PlotCanvas.Children.Add(dLine);
                }
                dLine.Points.Add(new Windows.Foundation.Point(MapX(x), MapY(y)));
                prevDy = y;
            }
        }

        // 可拖动的取点
        if (!double.IsNaN(_markerX))
        {
            DrawMarker(MapX, MapY);
        }

        StatusText.Text = $"已绘制 y = {expr}，x ∈ [{FormatNumber(_xMin)}, {FormatNumber(_xMax)}]，"
                          + $"y ∈ [{FormatNumber(_yMin)}, {FormatNumber(_yMax)}]。"
                          + (_panMode
                              ? "滚轮以绘图区中心缩放；左键拖动红点取点，拖动其他位置平移（左右 + 上下）。"
                              : "滚轮围绕鼠标位置缩放；左键拖动红点取点，拖动其他位置平移（左右 + 上下）。");
    }

    private void DrawMarker(Func<double, double> mapX, Func<double, double> mapY)
    {
        var x = Math.Clamp(_markerX, _xMin, _xMax);
        double y;
        try
        {
            y = ExpressionEvaluator.Evaluate(_normalizedPlot, x);
            if (double.IsNaN(y) || double.IsInfinity(y)) return;
        }
        catch
        {
            return;
        }

        var px = mapX(x);
        var py = mapY(y);
        if (py < _plotTop - 20 || py > _plotTop + _plotHeight + 20) return;

        var dashed = new SolidColorBrush(Color.FromArgb(160, 229, 57, 53));
        PlotCanvas.Children.Add(new Line
        {
            X1 = px, X2 = px, Y1 = py, Y2 = _plotTop + _plotHeight,
            Stroke = dashed, StrokeThickness = 1, StrokeDashArray = new DoubleCollection { 4, 3 }
        });
        PlotCanvas.Children.Add(new Line
        {
            X1 = _plotLeft, X2 = px, Y1 = py, Y2 = py,
            Stroke = dashed, StrokeThickness = 1, StrokeDashArray = new DoubleCollection { 4, 3 }
        });

        var dot = new Ellipse
        {
            Width = 13, Height = 13,
            Fill = new SolidColorBrush(Color.FromArgb(255, 229, 57, 53)),
            Stroke = new SolidColorBrush(Color.FromArgb(255, 255, 255, 255)),
            StrokeThickness = 2,
            IsHitTestVisible = false,
        };
        Canvas.SetLeft(dot, px - 6.5);
        Canvas.SetTop(dot, py - 6.5);
        PlotCanvas.Children.Add(dot);

        var labelText = $"({FormatNumber(x)}, {FormatNumber(y)})";
        var label = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(240, 255, 255, 255)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(255, 224, 224, 224)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(6, 2, 6, 2),
            IsHitTestVisible = false,
            Child = new TextBlock
            {
                Text = labelText,
                FontSize = 12,
                Foreground = new SolidColorBrush(Color.FromArgb(255, 40, 48, 58)),
            },
        };
        var lx = Math.Min(Math.Max(px + 12, _plotLeft), _plotLeft + _plotWidth - 130);
        var ly = Math.Min(Math.Max(py - 30, _plotTop), _plotTop + _plotHeight - 26);
        Canvas.SetLeft(label, lx);
        Canvas.SetTop(label, ly);
        PlotCanvas.Children.Add(label);

        var trace = $"取点：x = {FormatNumber(x)}，f(x) = {FormatNumber(y)}";
        if (DerivToggle.IsChecked == true)
        {
            try
            {
                var slope = ExpressionEvaluator.Derivative(_normalizedPlot, x);
                if (!double.IsNaN(slope) && !double.IsInfinity(slope))
                    trace += $"，f′(x) = {FormatNumber(slope)}";
            }
            catch
            {
                // 导数不可用时忽略
            }
        }
        TraceText.Text = trace + "（拖动红点连续取点）";
    }

    private void AddLabel(string text, double left, double top, double width,
        TextAlignment alignment, Brush foreground)
    {
        var block = new TextBlock
        {
            Text = text,
            FontSize = 11,
            Width = width,
            TextAlignment = alignment,
            Foreground = foreground,
            IsHitTestVisible = false,
        };
        Canvas.SetLeft(block, left);
        Canvas.SetTop(block, top);
        PlotCanvas.Children.Add(block);
    }

    // ==================== 取点交互 ====================

    private void OnPlotPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_plotPoints.Count == 0) return;
        var pt = e.GetCurrentPoint(PlotCanvas);
        var p = pt.Position;
        if (p.X < _plotLeft - 10 || p.X > _plotLeft + _plotWidth + 10) return;
        if (p.Y < _plotTop - 10 || p.Y > _plotTop + _plotHeight + 10) return;

        // 左键按在红点上 → 拖动取点；按在其他位置 → 平移视图（左右 + 上下）。右键拖动同样平移。
        if (pt.Properties.IsLeftButtonPressed)
        {
            if (IsNearMarker(p))
            {
                _draggingMarker = true;
                PlotCanvas.CapturePointer(e.Pointer);
                UpdateMarkerFromPoint(p.X);
                return;
            }
            StartPlotPan(p, e);
            return;
        }

        if (pt.Properties.IsRightButtonPressed)
        {
            StartPlotPan(p, e);
        }
    }

    /// <summary>判断指针是否落在曲线上的红点附近（用于区分“拖动取点”与“平移图像”）。</summary>
    private bool IsNearMarker(Windows.Foundation.Point p)
    {
        if (double.IsNaN(_markerX) || _plotWidth <= 1 || _plotHeight <= 1) return false;
        double y;
        try
        {
            y = ExpressionEvaluator.Evaluate(_normalizedPlot, _markerX);
        }
        catch
        {
            return false;
        }
        if (double.IsNaN(y) || double.IsInfinity(y)) return false;
        var px = _plotLeft + (_markerX - _xMin) / (_xMax - _xMin) * _plotWidth;
        var py = _plotTop + (_yMax - y) / (_yMax - _yMin) * _plotHeight;
        return Math.Abs(p.X - px) <= 16 && Math.Abs(p.Y - py) <= 16;
    }

    private void StartPlotPan(Windows.Foundation.Point p, PointerRoutedEventArgs e)
    {
        _panningPlot = true;
        _panLast = p;
        PlotCanvas.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void OnPlotPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        var p = e.GetCurrentPoint(PlotCanvas).Position;

        if (_panningPlot)
        {
            if (_plotWidth <= 1 || _plotHeight <= 1) return;
            var dx = p.X - _panLast.X;
            var dy = p.Y - _panLast.Y;
            var shiftX = dx / _plotWidth * (_xMax - _xMin);
            var shiftY = dy / _plotHeight * (_yMax - _yMin);
            _xMin -= shiftX;
            _xMax -= shiftX;
            _yMin += shiftY;
            _yMax += shiftY;
            _panLast = p;
            _manualRange = true;
            SyncRangeInputs();
            DrawPlot();
            return;
        }

        if (!_draggingMarker) return;
        UpdateMarkerFromPoint(p.X);
    }

    private void OnPlotPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_panningPlot)
        {
            _panningPlot = false;
            PlotCanvas.ReleasePointerCapture(e.Pointer);
            return;
        }
        if (!_draggingMarker) return;
        _draggingMarker = false;
        PlotCanvas.ReleasePointerCapture(e.Pointer);
    }

    private void OnPlotPointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        _draggingMarker = false;
        _panningPlot = false;
    }

    private void UpdateMarkerFromPoint(double px)
    {
        if (_plotWidth <= 1) return;
        var ratio = Math.Clamp((px - _plotLeft) / _plotWidth, 0, 1);
        _markerX = _xMin + ratio * (_xMax - _xMin);
        DrawPlot();
    }

    /// <summary>取 1/2/5×10^n 的"好看"网格步长。</summary>
    private static double NiceStep(double raw)
    {
        if (raw <= 0 || double.IsNaN(raw)) return 1;
        var exp = Math.Floor(Math.Log10(raw));
        var baseValue = Math.Pow(10, exp);
        var f = raw / baseValue;
        var nice = f <= 1 ? 1 : f <= 2 ? 2 : f <= 5 ? 5 : 10;
        return nice * baseValue;
    }

    private static string FormatTick(double v)
    {
        if (Math.Abs(v) < 1e-12) return "0";
        if (Math.Abs(v) >= 1e6 || Math.Abs(v) < 1e-4) return v.ToString("0.##e+0", CultureInfo.InvariantCulture);
        return Math.Round(v, 6).ToString("0.######", CultureInfo.InvariantCulture);
    }

    private static double ParseDouble(string? text, double fallback) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : fallback;

    private static string FormatNumber(double v)
    {
        if (double.IsNaN(v) || double.IsInfinity(v)) return v.ToString(CultureInfo.InvariantCulture);
        if (Math.Abs(v) >= 1e10 || (Math.Abs(v) > 0 && Math.Abs(v) < 1e-6)) return v.ToString("0.######e+0", CultureInfo.InvariantCulture);
        return Math.Round(v, 10).ToString("0.##########", CultureInfo.InvariantCulture);
    }
}
