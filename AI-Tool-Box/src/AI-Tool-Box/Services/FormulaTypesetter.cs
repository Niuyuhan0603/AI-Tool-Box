using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using System;
using System.Text;

namespace AI_Tool_Box.Services;

/// <summary>
/// 把「类 LaTeX / 化学式」文本排成 “所见即所得” 的二维公式，便于像卡西欧那样直接看懂输入内容：
/// 支持 \frac{}{}（真分数）、\sqrt{}/\sqrt[n]{}（根号带上划线）、^{}（上标）、_{}（下标）、
/// \pi \times \rightarrow 等常用命令与 Unicode 符号。数学表达式与化学式共用同一套排版。
/// <para>
/// 采用横向 StackPanel + 垂直居中的排布方式，避免 RichTextBlock + InlineUIContainer 常见的基线错位问题，
/// 从而使分数、根号、上下标与普通文本严格对齐。
/// </para>
/// <para>
/// 传入 <c>onSlotActivate</c> 后，公式中每一个空的 <c>{}</c> 都会渲染成可点击的空位方块，
/// 点击时回调该空位在原始文本中的绝对索引，宿主即可把输入光标移动过去，实现 “点空格填数字”。
/// </para>
/// </summary>
public static class FormulaTypesetter
{
    /// <summary>构建一个类型化公式控件；文本为空时返回带占位提示的控件。</summary>
    /// <param name="input">类 LaTeX / 化学式文本。</param>
    /// <param name="fontSize">基准字号。</param>
    /// <param name="foreground">前景色（跟随主题）。</param>
    /// <param name="placeholder">文本为空时的提示语。</param>
    /// <param name="onSlotActivate">点击空位时回调（参数为该空位在原始文本中的绝对索引）；为 null 时不响应点击。</param>
    public static FrameworkElement Build(string? input, double fontSize = 20, Brush? foreground = null,
        string placeholder = "在此输入公式，例如 \\frac{1}{2}x^{2}+\\sqrt{x}", Action<int>? onSlotActivate = null)
    {
        var text = (input ?? string.Empty).Trim();
        var brush = foreground ?? new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x22, 0x2A, 0x33));

        if (text.Length == 0)
        {
            return new TextBlock
            {
                Text = placeholder,
                FontSize = fontSize * 0.85,
                Foreground = brush,
                Opacity = 0.6,
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center,
            };
        }

        var row = BuildRow(text, 0, fontSize, brush, onSlotActivate);
        return new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalScrollMode = ScrollMode.Enabled,
            VerticalScrollMode = ScrollMode.Disabled,
            VerticalAlignment = VerticalAlignment.Center,
            Content = row,
        };
    }

    /// <summary>把一段文本排成横向、垂直居中的一行（内部递归处理分数 / 根号 / 上下标）。</summary>
    private static StackPanel BuildRow(string text, int baseOffset, double fontSize, Brush brush, Action<int>? onSlot)
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var buf = new StringBuilder();

        void Flush()
        {
            if (buf.Length == 0) return;
            row.Children.Add(new TextBlock
            {
                Text = buf.ToString(),
                FontSize = fontSize,
                Foreground = brush,
                VerticalAlignment = VerticalAlignment.Center,
                IsTextSelectionEnabled = false,
            });
            buf.Clear();
        }

        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];

            if (c == '\\')
            {
                var j = i + 1;
                while (j < text.Length && char.IsLetter(text[j])) j++;
                var cmd = text[(i + 1)..j].ToLowerInvariant();
                if (cmd.Length == 0)
                {
                    buf.Append(c);
                    i++;
                    continue;
                }
                i = j;
                switch (cmd)
                {
                    case "frac":
                    case "dfrac":
                    case "tfrac":
                    {
                        var num = ReadGroup(text, ref i, out var numOff);
                        var den = ReadGroup(text, ref i, out var denOff);
                        Flush();
                        row.Children.Add(BuildFraction(num, baseOffset + numOff, den, baseOffset + denOff, fontSize, brush, onSlot));
                        break;
                    }
                    case "sqrt":
                    {
                        string? root = null;
                        var rootOff = i;
                        if (i < text.Length && text[i] == '[')
                        {
                            var close = text.IndexOf(']', i);
                            if (close > 0)
                            {
                                root = text[(i + 1)..close];
                                rootOff = i + 1;
                                i = close + 1;
                            }
                        }
                        var rad = ReadGroup(text, ref i, out var radOff);
                        Flush();
                        row.Children.Add(BuildSqrt(rad, baseOffset + radOff, root, baseOffset + rootOff, fontSize, brush, onSlot));
                        break;
                    }
                    case "mathrm":
                    case "text":
                    case "mathit":
                    case "operatorname":
                    case "ce":
                        buf.Append(ReadGroup(text, ref i, out _));
                        break;
                    case "left":
                    case "right":
                    case "!": case ",": case ";":
                        break;
                    case "pi": buf.Append('π'); break;
                    case "times": case "cdot": buf.Append('×'); break;
                    case "div": buf.Append('÷'); break;
                    case "pm": buf.Append("±"); break;
                    case "mp": buf.Append("∓"); break;
                    case "le": case "leq": buf.Append('≤'); break;
                    case "ge": case "geq": buf.Append('≥'); break;
                    case "neq": case "ne": buf.Append('≠'); break;
                    case "approx": buf.Append('≈'); break;
                    case "rightarrow": case "to": case "longrightarrow": case "rarr": buf.Append('→'); break;
                    case "leftarrow": case "gets": buf.Append('←'); break;
                    case "rightleftharpoons": case "leftrightarrow": case "lrarr": buf.Append('⇌'); break;
                    case "uparrow": case "uarr": buf.Append('↑'); break;
                    case "downarrow": case "darr": buf.Append('↓'); break;
                    case "degree": case "circ": buf.Append('°'); break;
                    case "infty": buf.Append('∞'); break;
                    case "sum": buf.Append('∑'); break;
                    case "prod": buf.Append('∏'); break;
                    case "int": buf.Append('∫'); break;
                    case "partial": buf.Append('∂'); break;
                    case "nabla": buf.Append('∇'); break;
                    case "alpha": buf.Append('α'); break;
                    case "beta": buf.Append('β'); break;
                    case "gamma": buf.Append('γ'); break;
                    case "delta": buf.Append('δ'); break;
                    case "Delta": buf.Append('Δ'); break;
                    case "epsilon": buf.Append('ε'); break;
                    case "theta": buf.Append('θ'); break;
                    case "lambda": buf.Append('λ'); break;
                    case "mu": buf.Append('μ'); break;
                    case "nu": buf.Append('ν'); break;
                    case "rho": buf.Append('ρ'); break;
                    case "sigma": buf.Append('σ'); break;
                    case "phi": buf.Append('φ'); break;
                    case "omega": buf.Append('ω'); break;
                    case "Omega": buf.Append('Ω'); break;
                    case "sin": case "cos": case "tan": case "cot": case "sec": case "csc":
                    case "ln": case "log": case "exp": case "lim": case "max": case "min":
                        buf.Append(cmd);
                        break;
                    default:
                        // 未知命令：直接丢弃反斜杠，保留命令名（尽量不丢信息）
                        buf.Append(cmd);
                        break;
                }
                continue;
            }

            if (c == '^' || c == '_')
            {
                i++;
                var inner = ReadGroup(text, ref i, out var off);
                Flush();
                row.Children.Add(BuildScript(inner, baseOffset + off, fontSize, brush, c == '^', onSlot));
                continue;
            }

            if (c == '{')
            {
                var g = ReadGroup(text, ref i, out var off);
                if (string.IsNullOrWhiteSpace(g))
                {
                    Flush();
                    row.Children.Add(BuildSlot(baseOffset + off, fontSize, brush, onSlot));
                }
                else
                {
                    buf.Append(g);
                }
                continue;
            }
            if (c == '}') { i++; continue; }

            // 箭头简写：-> / => → → ；<=> / <-> → ⇌（化学方程式常用）
            if (c == '-' && i + 1 < text.Length && text[i + 1] == '>')
            {
                buf.Append('→'); i += 2; continue;
            }
            if (c == '=' && i + 1 < text.Length && text[i + 1] == '>')
            {
                buf.Append('→'); i += 2; continue;
            }
            if (c == '<' && i + 2 < text.Length && text[i + 1] == '=' && text[i + 2] == '>')
            {
                buf.Append('⇌'); i += 3; continue;
            }
            if (c == '<' && i + 2 < text.Length && text[i + 1] == '-' && text[i + 2] == '>')
            {
                buf.Append('⇌'); i += 3; continue;
            }

            buf.Append(c);
            i++;
        }

        Flush();
        return row;
    }

    /// <summary>读取一个分组（{...} / 单字符 / \命令），并通过 <paramref name="contentOffset"/> 返回内容起点的绝对索引。</summary>
    private static string ReadGroup(string s, ref int i, out int contentOffset)
    {
        while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
        if (i >= s.Length)
        {
            contentOffset = i;
            return string.Empty;
        }

        if (s[i] == '{')
        {
            var depth = 0;
            var start = i + 1;
            for (var k = i; k < s.Length; k++)
            {
                if (s[k] == '{') depth++;
                else if (s[k] == '}')
                {
                    depth--;
                    if (depth == 0)
                    {
                        contentOffset = start;
                        i = k + 1;
                        return s[start..k];
                    }
                }
            }
            contentOffset = start;
            i = s.Length;
            return s[start..];
        }

        if (s[i] == '\\')
        {
            var start = i;
            var j = i + 1;
            while (j < s.Length && char.IsLetter(s[j])) j++;
            if (j == i + 1) j = Math.Min(s.Length, i + 2);
            var cmd = s[start..j];
            contentOffset = start;
            i = j;
            return cmd;
        }

        contentOffset = i;
        var ch = s[i];
        i++;
        return ch.ToString();
    }

    /// <summary>把一个分组内容排成控件；内容为空时渲染成可点击的空位方块。</summary>
    private static FrameworkElement BuildGroup(string text, int abs, double fontSize, Brush brush, Action<int>? onSlot)
    {
        return string.IsNullOrWhiteSpace(text)
            ? BuildSlot(abs, fontSize, brush, onSlot)
            : BuildRow(text, abs, fontSize, brush, onSlot);
    }

    private static FrameworkElement BuildFraction(string num, int numAbs, string den, int denAbs,
        double fontSize, Brush brush, Action<int>? onSlot)
    {
        var grid = new Grid { RowSpacing = 1, Padding = new Thickness(4, 1, 4, 1), VerticalAlignment = VerticalAlignment.Center };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var n = BuildGroup(num, numAbs, fontSize * 0.82, brush, onSlot);
        n.HorizontalAlignment = HorizontalAlignment.Center;
        var line = new Border
        {
            Height = 1.4,
            Margin = new Thickness(0, 1, 0, 1),
            Background = brush,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        var d = BuildGroup(den, denAbs, fontSize * 0.82, brush, onSlot);
        d.HorizontalAlignment = HorizontalAlignment.Center;

        Grid.SetRow(n, 0);
        Grid.SetRow(line, 1);
        Grid.SetRow(d, 2);
        grid.Children.Add(n);
        grid.Children.Add(line);
        grid.Children.Add(d);

        return new Border
        {
            Child = grid,
            Margin = new Thickness(2, 0, 2, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
    }

    private static FrameworkElement BuildSqrt(string rad, int radAbs, string? root, int rootAbs,
        double fontSize, Brush brush, Action<int>? onSlot)
    {
        var sp = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
        };

        if (!string.IsNullOrWhiteSpace(root))
        {
            var r = BuildRow(root, rootAbs, fontSize * 0.62, brush, onSlot);
            r.RenderTransform = new TranslateTransform { X = 1, Y = fontSize * 0.30 };
            r.Margin = new Thickness(0, 0, -5, 0);
            sp.Children.Add(r);
        }

        sp.Children.Add(new TextBlock
        {
            Text = "√",
            FontSize = fontSize * 1.4,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = brush,
            Margin = new Thickness(0, 0, -4, 0),
        });

        var radicand = BuildGroup(rad, radAbs, fontSize * 0.98, brush, onSlot);
        sp.Children.Add(new Border
        {
            BorderBrush = brush,
            BorderThickness = new Thickness(0, 1.3, 0, 0),
            Padding = new Thickness(3, 1, 3, 0),
            Child = radicand,
            VerticalAlignment = VerticalAlignment.Center,
        });

        return new Border { Child = sp, Margin = new Thickness(2, 0, 2, 0), VerticalAlignment = VerticalAlignment.Center };
    }

    private static FrameworkElement BuildScript(string inner, int abs, double fontSize, Brush brush,
        bool superscript, Action<int>? onSlot)
    {
        var content = BuildGroup(inner, abs, fontSize * 0.7, brush, onSlot);
        content.RenderTransform = new TranslateTransform
        {
            Y = superscript ? -fontSize * 0.42 : fontSize * 0.30,
        };
        content.VerticalAlignment = VerticalAlignment.Center;
        return new Border
        {
            Child = content,
            Margin = new Thickness(1, 0, 1, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
    }

    /// <summary>渲染一个可点击的空位方块（虚线框），点击后回调其在原始文本中的绝对索引。</summary>
    private static FrameworkElement BuildSlot(int abs, double fontSize, Brush brush, Action<int>? onSlot)
    {
        var host = new Grid
        {
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(1, 0, 1, 0),
            MinWidth = Math.Max(12, fontSize * 0.62),
            Height = fontSize * 0.88,
        };
        host.Children.Add(new Rectangle
        {
            Stroke = brush,
            StrokeThickness = 1,
            StrokeDashArray = new DoubleCollection { 2, 2 },
            RadiusX = 2,
            RadiusY = 2,
            Fill = new SolidColorBrush(Windows.UI.Color.FromArgb(20, 120, 140, 160)),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
        });

        var activate = onSlot;
        if (activate is not null)
        {
            // 需要一个非 null 的背景才能命中测试
            host.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(1, 0, 0, 0));
            host.IsTapEnabled = true;
            host.Tapped += (_, _) => activate(abs);
            ToolTipService.SetToolTip(host, "点击此处输入");
        }

        return host;
    }
}
