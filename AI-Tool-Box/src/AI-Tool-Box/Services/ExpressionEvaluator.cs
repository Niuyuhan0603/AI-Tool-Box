using System.Globalization;

namespace AI_Tool_Box.Services;

/// <summary>轻量数学表达式求值器（支持 + - * / ^ % ! 括号、常量和常用函数，变量 x）。</summary>
public static class ExpressionEvaluator
{
    private static readonly Dictionary<string, double> Constants = new(StringComparer.OrdinalIgnoreCase)
    {
        ["pi"] = Math.PI,
        ["e"] = Math.E,
        ["tau"] = Math.Tau,
    };

    private static readonly Dictionary<string, Func<double[], double>> Functions = new(StringComparer.OrdinalIgnoreCase)
    {
        ["sin"] = a => Math.Sin(a[0]),
        ["cos"] = a => Math.Cos(a[0]),
        ["tan"] = a => Math.Tan(a[0]),
        ["cot"] = a => 1.0 / Math.Tan(a[0]),
        ["sec"] = a => 1.0 / Math.Cos(a[0]),
        ["csc"] = a => 1.0 / Math.Sin(a[0]),
        ["asin"] = a => Math.Asin(a[0]),
        ["acos"] = a => Math.Acos(a[0]),
        ["atan"] = a => Math.Atan(a[0]),
        ["sinh"] = a => Math.Sinh(a[0]),
        ["cosh"] = a => Math.Cosh(a[0]),
        ["tanh"] = a => Math.Tanh(a[0]),
        ["sqrt"] = a => Math.Sqrt(a[0]),
        ["cbrt"] = a => Math.Cbrt(a[0]),
        ["abs"] = a => Math.Abs(a[0]),
        ["ln"] = a => Math.Log(a[0]),
        ["log"] = a => Math.Log10(a[0]),
        ["log2"] = a => Math.Log2(a[0]),
        ["exp"] = a => Math.Exp(a[0]),
        ["floor"] = a => Math.Floor(a[0]),
        ["ceil"] = a => Math.Ceiling(a[0]),
        ["round"] = a => Math.Round(a[0]),
        ["sign"] = a => Math.Sign(a[0]),
        ["deg"] = a => a[0] * Math.PI / 180.0,
        ["rad"] = a => a[0] * 180.0 / Math.PI,
        ["min"] = a => a.Min(),
        ["max"] = a => a.Max(),
        ["pow"] = a => Math.Pow(a[0], a[1]),
        ["atan2"] = a => Math.Atan2(a[0], a[1]),
    };

    /// <summary>
    /// 把 LaTeX / 自然书写形式的数学式转换为求值器可识别的普通表达式。
    /// 支持：\frac{a}{b}、\sqrt{x}、\sqrt[n]{x}、x^{2}、\pi、\times、\cdot、\div、\left/\right、
    /// 常用函数命令（\sin 等）、Unicode 符号（π × ÷ √ ² ³ −）。
    /// </summary>
    public static string NormalizeMathInput(string input)
    {
        var text = (input ?? string.Empty).Trim();
        if (text.Length == 0) return text;
        // 去掉 $...$ / \[...\] 数学环境标记
        text = text.Replace("$$", string.Empty).Replace("$", string.Empty)
            .Replace("\\[", string.Empty).Replace("\\]", string.Empty)
            .Replace("\\(", string.Empty).Replace("\\)", string.Empty);
        // Unicode 运算符与符号
        text = text.Replace("×", "*").Replace("·", "*").Replace("÷", "/")
            .Replace("−", "-").Replace("–", "-").Replace("π", "pi")
            .Replace("²", "^2").Replace("³", "^3").Replace("√", "sqrt");
        text = text.Replace("\\left", string.Empty).Replace("\\right", string.Empty)
            .Replace("\\!", string.Empty).Replace("\\,", string.Empty).Replace("\\;", string.Empty)
            .Replace("\\ ", " ");

        var sb = new System.Text.StringBuilder(text.Length + 16);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\\')
            {
                var j = i + 1;
                while (j < text.Length && char.IsLetter(text[j])) j++;
                var cmd = text[(i + 1)..j].ToLowerInvariant();
                i = j - 1;
                switch (cmd)
                {
                    case "frac":
                    case "dfrac":
                    case "tfrac":
                    {
                        var (a, next) = ReadGroup(text, i + 1);
                        var (b, next2) = ReadGroup(text, next);
                        sb.Append("((").Append(NormalizeMathInput(a)).Append(")/(")
                          .Append(NormalizeMathInput(b)).Append("))");
                        i = next2 - 1;
                        break;
                    }
                    case "sqrt":
                    {
                        string? root = null;
                        if (i + 1 < text.Length && text[i + 1] == '[')
                        {
                            var close = text.IndexOf(']', i + 1);
                            if (close > 0)
                            {
                                root = text[(i + 2)..close];
                                i = close;
                            }
                        }
                        var (a, next) = ReadGroup(text, i + 1);
                        var inner = NormalizeMathInput(a);
                        if (string.IsNullOrWhiteSpace(root))
                        {
                            sb.Append("sqrt(").Append(inner).Append(')');
                        }
                        else
                        {
                            sb.Append("pow(").Append(inner).Append(",1/(").Append(root).Append("))");
                        }
                        i = next - 1;
                        break;
                    }
                    case "pi":
                        sb.Append("pi");
                        break;
                    case "cdot":
                    case "times":
                        sb.Append('*');
                        break;
                    case "div":
                        sb.Append('/');
                        break;
                    case "arcsin":
                        sb.Append("asin");
                        break;
                    case "arccos":
                        sb.Append("acos");
                        break;
                    case "arctan":
                        sb.Append("atan");
                        break;
                    case "exp":
                        sb.Append("exp");
                        break;
                    case "":
                        break;
                    default:
                        sb.Append(cmd);
                        break;
                }
                continue;
            }

            if (c == '^')
            {
                if (i + 1 < text.Length && text[i + 1] == '{')
                {
                    var (g, next) = ReadGroup(text, i + 1);
                    sb.Append("^(").Append(NormalizeMathInput(g)).Append(')');
                    i = next - 1;
                    continue;
                }
                sb.Append('^');
                continue;
            }

            // 其余花括号充当普通括号（如 \frac 之外的 {x+1}）
            if (c == '{') sb.Append('(');
            else if (c == '}') sb.Append(')');
            else sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>读取 LaTeX 参数组：{...}、\命令 或单个字符。</summary>
    private static (string Content, int Next) ReadGroup(string s, int index)
    {
        while (index < s.Length && char.IsWhiteSpace(s[index])) index++;
        if (index >= s.Length) return (string.Empty, index);

        if (s[index] == '{')
        {
            var depth = 0;
            var start = index + 1;
            for (var i = index; i < s.Length; i++)
            {
                if (s[i] == '{') depth++;
                else if (s[i] == '}')
                {
                    depth--;
                    if (depth == 0) return (s[start..i], i + 1);
                }
            }
            return (s[start..], s.Length);
        }

        if (s[index] == '\\')
        {
            var j = index + 1;
            while (j < s.Length && char.IsLetter(s[j])) j++;
            if (j == index + 1) j = Math.Min(s.Length, index + 2);
            return (s[index..j], j);
        }

        return (s[index].ToString(), index + 1);
    }

    /// <summary>计算表达式；x 为变量值（不含 x 时忽略）。</summary>
    /// <exception cref="ArgumentException">表达式非法。</exception>
    public static double Evaluate(string expression, double x = 0)
        => Evaluate(expression, new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
        {
            ["x"] = x,
            ["y"] = 0,
            ["z"] = 0,
        });

    /// <summary>计算表达式（可同时使用 x、y 两个变量）。</summary>
    public static double Evaluate(string expression, double x, double y, double z = 0)
        => Evaluate(expression, new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
        {
            ["x"] = x,
            ["y"] = y,
            ["z"] = z,
        });

    /// <summary>计算表达式；未在字典中的变量按 0 处理。</summary>
    public static double Evaluate(string expression, IReadOnlyDictionary<string, double> variables)
    {
        if (string.IsNullOrWhiteSpace(expression)) throw new ArgumentException("表达式为空。");
        var parser = new Parser(NormalizeMathInput(expression), variables);
        var value = parser.ParseExpression();
        parser.EnsureEnd();
        return value;
    }

    // ==================== 数值微积分（求导 / 偏导 / 积分） ====================

    /// <summary>一阶数值导数（5 点中心差分），变量可为 x / y / z。</summary>
    public static double Derivative(string expression, double x, double y = 0, string variable = "x")
    {
        var name = string.IsNullOrWhiteSpace(variable) ? "x" : variable.Trim().ToLowerInvariant();
        double F(double vx, double vy)
        {
            var vars = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
            {
                ["x"] = vx,
                ["y"] = vy,
                ["z"] = 0,
            };
            vars[name] = name == "y" ? vy : vx;
            return Evaluate(expression, vars);
        }

        var scale = Math.Max(1.0, Math.Abs(name == "y" ? y : x));
        var h = 1e-5 * scale;
        var f1 = name == "y" ? F(x, y - 2 * h) : F(x - 2 * h, y);
        var f2 = name == "y" ? F(x, y - h) : F(x - h, y);
        var f3 = name == "y" ? F(x, y + h) : F(x + h, y);
        var f4 = name == "y" ? F(x, y + 2 * h) : F(x + 2 * h, y);
        return (-f1 + 8 * f2 - 8 * f3 + f4) / (12 * h);
    }

    /// <summary>对 x 的二阶数值导数（5 点中心差分）。</summary>
    public static double SecondDerivative(string expression, double x, double y = 0)
    {
        var h = 1e-4 * Math.Max(1.0, Math.Abs(x));
        var f1 = Evaluate(expression, x - 2 * h, y);
        var f2 = Evaluate(expression, x - h, y);
        var f0 = Evaluate(expression, x, y);
        var f3 = Evaluate(expression, x + h, y);
        var f4 = Evaluate(expression, x + 2 * h, y);
        return (-f1 + 16 * f2 - 30 * f0 + 16 * f3 - f4) / (12 * h * h);
    }

    /// <summary>定积分（自适应 Simpson，端点奇异时自动内收）。</summary>
    public static double Integrate(string expression, double a, double b, double tolerance = 1e-9)
    {
        if (Math.Abs(b - a) < 1e-15) return 0;
        double F(double t) => Evaluate(expression, t, 0);
        var lo = Math.Min(a, b);
        var hi = Math.Max(a, b);
        if (!IsFinite(SafeEval(F, lo))) lo += (hi - lo) * 1e-9;
        if (!IsFinite(SafeEval(F, hi))) hi -= (hi - lo) * 1e-9;
        var fa = SafeEval(F, lo);
        var fb = SafeEval(F, hi);
        var mid = (lo + hi) / 2;
        var fm = SafeEval(F, mid);
        if (!IsFinite(fa) || !IsFinite(fb) || !IsFinite(fm)) return double.NaN;
        var sign = b >= a ? 1 : -1;
        return sign * Simpson(F, lo, hi, fa, fm, fb, tolerance, 40);
    }

    private static double Simpson(Func<double, double> f, double a, double b,
        double fa, double fm, double fb, double tol, int depth)
    {
        var m = (a + b) / 2;
        var lm = (a + m) / 2;
        var rm = (m + b) / 2;
        var flm = SafeEval(f, lm);
        var frm = SafeEval(f, rm);
        if (!IsFinite(flm) || !IsFinite(frm)) return (b - a) / 6 * (fa + 4 * fm + fb);

        var whole = (b - a) / 6 * (fa + 4 * fm + fb);
        var left = (m - a) / 6 * (fa + 4 * flm + fm);
        var right = (b - m) / 6 * (fm + 4 * frm + fb);
        var delta = left + right - whole;
        if (depth <= 0 || Math.Abs(delta) < 15 * tol) return left + right + delta / 15;
        return Simpson(f, a, m, fa, flm, fm, tol / 2, depth - 1)
             + Simpson(f, m, b, fm, frm, fb, tol / 2, depth - 1);
    }

    private static double SafeEval(Func<double, double> f, double t)
    {
        try
        {
            var v = f(t);
            return double.IsNaN(v) || double.IsInfinity(v) ? double.NaN : v;
        }
        catch
        {
            return double.NaN;
        }
    }

    private static bool IsFinite(double v) => !double.IsNaN(v) && !double.IsInfinity(v);

    /// <summary>生成导函数图像采样点。</summary>
    public static List<(double X, double Y)> SampleDerivative(string expression, double xMin, double xMax, int count = 800)
    {
        var points = new List<(double, double)>(count);
        if (count < 2 || xMax <= xMin) return points;
        var step = (xMax - xMin) / (count - 1);
        for (var i = 0; i < count; i++)
        {
            var x = xMin + step * i;
            try
            {
                var y = Derivative(expression, x);
                if (!IsFinite(y) || Math.Abs(y) > 1e12) continue;
                points.Add((x, y));
            }
            catch
            {
                // 单点失败忽略
            }
        }
        return points;
    }

    /// <summary>生成函数图像采样点（跳过非法值区间）。</summary>
    public static List<(double X, double Y)> Sample(string expression, double xMin, double xMax, int count = 800)
    {
        var points = new List<(double, double)>(count);
        if (count < 2 || xMax <= xMin) return points;
        var step = (xMax - xMin) / (count - 1);
        for (var i = 0; i < count; i++)
        {
            var x = xMin + step * i;
            try
            {
                var y = Evaluate(expression, x);
                if (double.IsNaN(y) || double.IsInfinity(y) || Math.Abs(y) > 1e12) continue;
                points.Add((x, y));
            }
            catch
            {
                // 单点失败忽略
            }
        }
        return points;
    }

    private sealed class Parser
    {
        private readonly string _text;
        private readonly IReadOnlyDictionary<string, double> _vars;
        private int _pos;

        public Parser(string text, IReadOnlyDictionary<string, double> variables)
        {
            _text = text;
            _vars = variables;
        }

        public double ParseExpression()
        {
            var value = ParseTerm();
            while (true)
            {
                SkipSpaces();
                if (Match('+')) value += ParseTerm();
                else if (Match('-')) value -= ParseTerm();
                else return value;
            }
        }

        private double ParseTerm()
        {
            var value = ParseUnary();
            while (true)
            {
                SkipSpaces();
                if (Match('*')) value *= ParseUnary();
                else if (Match('/')) value /= ParseUnary();
                else if (Match('%')) value %= ParseUnary();
                else if (IsImplicitMulStart())
                {
                    // 隐式乘法：2x、3(pi+1)、)( 等
                    value *= ParseUnary();
                }
                else return value;
            }
        }

        private bool IsImplicitMulStart()
        {
            if (_pos >= _text.Length) return false;
            var c = _text[_pos];
            return char.IsLetter(c) || c == '(' || c == '.' || char.IsDigit(c);
        }

        private double ParseUnary()
        {
            SkipSpaces();
            if (Match('-')) return -ParseUnary();
            if (Match('+')) return ParseUnary();
            return ParsePower();
        }

        private double ParsePower()
        {
            var value = ParsePostfix();
            SkipSpaces();
            if (Match('^'))
            {
                var exp = ParseUnary(); // 右结合
                value = Math.Pow(value, exp);
            }
            return value;
        }

        private double ParsePostfix()
        {
            var value = ParsePrimary();
            while (true)
            {
                SkipSpaces();
                if (!Match('!')) return value;
                value = Factorial(value);
            }
        }

        private double ParsePrimary()
        {
            SkipSpaces();
            if (_pos >= _text.Length) throw new ArgumentException("表达式意外结束。");

            if (Match('('))
            {
                var v = ParseExpression();
                SkipSpaces();
                if (!Match(')')) throw new ArgumentException("缺少右括号。");
                return v;
            }

            var c = _text[_pos];
            if (char.IsDigit(c) || c == '.')
            {
                var start = _pos;
                while (_pos < _text.Length && (char.IsDigit(_text[_pos]) || _text[_pos] == '.')) _pos++;
                var slice = _text[start.._pos];
                if (!double.TryParse(slice, NumberStyles.Float, CultureInfo.InvariantCulture, out var num))
                    throw new ArgumentException($"非法数字：{slice}");
                return num;
            }

            if (char.IsLetter(c))
            {
                var start = _pos;
                while (_pos < _text.Length && (char.IsLetterOrDigit(_text[_pos]) || _text[_pos] == '_')) _pos++;
                var name = _text[start.._pos];

                // 函数调用
                SkipSpaces();
                if (_pos < _text.Length && _text[_pos] == '(')
                {
                    if (!Functions.TryGetValue(name, out var fn))
                        throw new ArgumentException($"未知函数：{name}");
                    _pos++; // (
                    var args = new List<double>();
                    SkipSpaces();
                    if (Match(')')) return fn(args.ToArray());
                    while (true)
                    {
                        args.Add(ParseExpression());
                        SkipSpaces();
                        if (Match(',')) continue;
                        if (Match(')')) break;
                        throw new ArgumentException($"函数 {name} 参数缺少右括号。");
                    }
                    return fn(args.ToArray());
                }

                if (_vars.TryGetValue(name, out var variableValue)) return variableValue;
                if (Constants.TryGetValue(name, out var constant)) return constant;
                throw new ArgumentException($"未知标识符：{name}");
            }

            throw new ArgumentException($"非法字符：{c}");
        }

        private static double Factorial(double v)
        {
            if (v < 0 || v > 170 || Math.Abs(v - Math.Round(v)) > 1e-9)
                throw new ArgumentException("阶乘仅支持 0–170 的整数。");
            var result = 1.0;
            for (var i = 2; i <= (int)Math.Round(v); i++) result *= i;
            return result;
        }

        private void SkipSpaces()
        {
            while (_pos < _text.Length && char.IsWhiteSpace(_text[_pos])) _pos++;
        }

        private bool Match(char c)
        {
            SkipSpaces();
            if (_pos < _text.Length && _text[_pos] == c)
            {
                _pos++;
                return true;
            }
            return false;
        }

        public void EnsureEnd()
        {
            SkipSpaces();
            if (_pos < _text.Length) throw new ArgumentException($"表达式尾部有无法识别的内容：{_text[_pos..]}");
        }
    }
}
