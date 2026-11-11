using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace AI_Tool_Box.Services;

/// <summary>
/// 化学反应预测（教学级估算）：解析 SMILES → 原子/键 → 用标准生成焓表或键能估算 ΔH，
/// 用原子贡献近似 ΔS，进而得到 ΔG、平衡常数；速率用 Arrhenius 方程（Ea 可自定义）。
/// 结论明确标注为「估算」，不替代专业量子化学计算。
/// </summary>
public static class ReactionPredictService
{
    private const double RGas = 8.314; // J/(mol·K)

    /// <summary>预测结果。</summary>
    /// <param name="Ok">输入是否有效（元素是否守恒等）。</param>
    /// <param name="Text">面向用户的多行结果文本。</param>
    public sealed record Prediction(bool Ok, string Text);

    /// <summary>
    /// 预测反应：反应物与产物均使用 SMILES，多个物质用 “+” 分隔。
    /// </summary>
    /// <param name="reactantsText">反应物，如 "C2H4 + H2"。</param>
    /// <param name="productsText">产物，如 "C2H6"。</param>
    /// <param name="temperatureText">温度（K），空则 298.15。</param>
    /// <param name="activationEnergyText">活化能（kJ/mol），空则按 80 估算。</param>
    public static Prediction Predict(string reactantsText, string productsText,
        string? temperatureText, string? activationEnergyText)
    {
        // 若用户把整条方程式（含箭头）写在反应物框里，且产物框为空，则自动按箭头拆分为左右两侧。
        if (string.IsNullOrWhiteSpace(productsText)
            && TrySplitOnArrow(reactantsText ?? string.Empty, out var leftSide, out var rightSide))
        {
            reactantsText = leftSide;
            productsText = rightSide;
        }

        var reactants = ParseSide(reactantsText, "反应物");
        var products = ParseSide(productsText, "产物");
        if (reactants.Count == 0 || products.Count == 0)
        {
            return new Prediction(false, "请分别填写反应物与产物的 SMILES（多个物质用 + 分隔）。");
        }

        var temperature = 298.15;
        if (!string.IsNullOrWhiteSpace(temperatureText)
            && !double.TryParse(temperatureText, NumberStyles.Float, CultureInfo.InvariantCulture, out temperature))
        {
            return new Prediction(false, "温度格式无效，请输入数值（单位 K）。");
        }
        temperature = Math.Clamp(temperature, 1, 5000);

        double? activation = null;
        if (!string.IsNullOrWhiteSpace(activationEnergyText))
        {
            if (!double.TryParse(activationEnergyText, NumberStyles.Float, CultureInfo.InvariantCulture, out var ea))
            {
                return new Prediction(false, "活化能格式无效，请输入数值（单位 kJ/mol），或留空使用默认值。");
            }
            activation = Math.Max(0, ea);
        }

        var sb = new StringBuilder();
        var left = Combine(reactants);
        var right = Combine(products);

        // ===== 元素守恒检查 =====
        var balanced = left.Elements.Count == right.Elements.Count
                       && left.Elements.All(kv => right.Elements.TryGetValue(kv.Key, out var n) && n == kv.Value);
        sb.AppendLine("【配平检查】");
        sb.AppendLine($"反应物：{left.Formula}    产物：{right.Formula}");
        if (balanced)
        {
            sb.AppendLine("元素守恒：✓ 已配平");
        }
        else
        {
            sb.AppendLine("元素守恒：✗ 未配平（以下热力学量仍按输入直接估算，建议先配平）");
        }
        sb.AppendLine();

        // ===== ΔH =====
        var allKnown = reactants.Concat(products).All(s => KnownSpecies.ContainsKey(s.Formula));
        double deltaH; // kJ/mol
        string enthalpyMethod;
        if (allKnown)
        {
            deltaH = products.Sum(s => KnownSpecies[s.Formula].Hf) - reactants.Sum(s => KnownSpecies[s.Formula].Hf);
            enthalpyMethod = "标准生成焓表";
        }
        else
        {
            var broken = reactants.Sum(s => s.BondEnergy);
            var formed = products.Sum(s => s.BondEnergy);
            deltaH = (broken - formed) / 1000.0;
            enthalpyMethod = "键能估算";
        }

        // ===== ΔS =====
        double deltaS; // J/(mol·K)
        string entropyMethod;
        if (allKnown)
        {
            deltaS = products.Sum(s => KnownSpecies[s.Formula].S) - reactants.Sum(s => KnownSpecies[s.Formula].S);
            entropyMethod = "标准熵表";
        }
        else
        {
            deltaS = products.Sum(s => EstimateEntropy(s)) - reactants.Sum(s => EstimateEntropy(s));
            entropyMethod = "原子贡献近似";
        }

        var deltaG = deltaH - temperature * deltaS / 1000.0; // kJ/mol
        var lnK = -deltaG * 1000 / (RGas * temperature);
        var kEquilibrium = lnK > 700 ? double.PositiveInfinity : Math.Exp(lnK);

        sb.AppendLine("【热力学】");
        sb.AppendLine($"ΔH° ≈ {Format(deltaH)} kJ/mol（{enthalpyMethod}）");
        sb.AppendLine($"ΔS° ≈ {Format(deltaS)} J/(mol·K)（{entropyMethod}）");
        sb.AppendLine($"T = {Format(temperature)} K");
        sb.AppendLine($"ΔG° = ΔH° − TΔS° ≈ {Format(deltaG)} kJ/mol");
        sb.AppendLine(deltaG < 0
            ? "判定：ΔG° < 0，该温度下热力学自发（平衡偏向产物）。"
            : "判定：ΔG° > 0，该温度下热力学非自发（平衡偏向反应物）。");
        sb.AppendLine(kEquilibrium == double.PositiveInfinity
            ? "平衡常数 K ≈ 极大（几乎完全反应）"
            : $"平衡常数 K ≈ {FormatK(kEquilibrium)}（lnK = {Format(lnK)}）");
        sb.AppendLine();

        // ===== 速率（Arrhenius）=====
        var eaKj = activation ?? 80.0;
        var eaNote = activation is null ? "默认估算值，可在上方修改" : "用户输入值";
        var kRate = 1e11 * Math.Exp(-eaKj * 1000 / (RGas * temperature));
        var k298 = 1e11 * Math.Exp(-eaKj * 1000 / (RGas * 298.15));
        sb.AppendLine("【反应速率（Arrhenius 估算）】");
        sb.AppendLine($"活化能 Ea = {Format(eaKj)} kJ/mol（{eaNote}）；指前因子 A = 1.0×10¹¹ s⁻¹");
        sb.AppendLine($"速率常数 k({Format(temperature)} K) ≈ {FormatK(kRate)} s⁻¹");
        if (Math.Abs(temperature - 298.15) > 0.5 && k298 > 0 && !double.IsInfinity(kRate))
        {
            sb.AppendLine($"相对 298 K 的加速倍数 ≈ {FormatK(kRate / k298)}×");
        }
        sb.AppendLine(kRate > 0 && !double.IsInfinity(kRate)
            ? $"一级反应半衰期 t₁/₂ = ln2/k ≈ {FormatTime(Math.Log(2) / kRate)}"
            : "半衰期：—");
        sb.AppendLine($"速率等级：{RateGrade(kRate)}");
        sb.AppendLine();

        // ===== 物质明细 =====
        sb.AppendLine("【物质明细】");
        foreach (var s in reactants) AppendSpecies(sb, s, "反应物");
        foreach (var s in products) AppendSpecies(sb, s, "产物");
        sb.AppendLine();
        sb.Append("提示：以上为教学级估算（键能/生成焓 + 熵近似 + Arrhenius），精确值需查热力学数据表或用量子化学计算。");

        return new Prediction(balanced, sb.ToString());
    }

    private static void AppendSpecies(StringBuilder sb, Species s, string side)
    {
        sb.AppendLine($"{side} {s.Smiles} → {s.Formula}，M = {Format(s.MolecularWeight)} g/mol，"
                      + $"键能合计 {Format(s.BondEnergy / 1000)} kJ/mol");
    }

    // ==================== 输入解析 ====================

    private static List<Species> ParseSide(string? text, string sideName)
    {
        if (string.IsNullOrWhiteSpace(text)) return new List<Species>();
        var list = new List<Species>();
        foreach (var raw in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (string.IsNullOrEmpty(raw)) continue;
            try
            {
                list.Add(ParseSpecies(raw));
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"{sideName} “{raw}” 解析失败：{ex.Message}");
            }
        }
        return list;
    }

    private static Combined Combine(List<Species> species)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var s in species)
        {
            foreach (var kv in s.Elements)
            {
                counts[kv.Key] = counts.TryGetValue(kv.Key, out var n) ? n + kv.Value : kv.Value;
            }
        }
        return new Combined(counts, HillFormula(counts));
    }

    private sealed record Combined(Dictionary<string, int> Elements, string Formula);

    // ==================== 化学式 / 类 LaTeX 输入处理 ====================

    /// <summary>把整式按箭头（-&gt; / =&gt; / → / ⇌ 等）拆成左右两侧。</summary>
    private static bool TrySplitOnArrow(string text, out string left, out string right)
    {
        left = right = string.Empty;
        if (string.IsNullOrWhiteSpace(text)) return false;
        string[] arrows =
        {
            "\\rightleftharpoons", "\\leftrightarrow", "\\longrightarrow", "\\rightarrow",
            "\\to", "<=>", "<->", "->", "=>", "→", "⇌",
        };
        foreach (var arrow in arrows)
        {
            var idx = text.IndexOf(arrow, StringComparison.Ordinal);
            if (idx < 0) continue;
            left = text[..idx].Trim();
            right = text[(idx + arrow.Length)..].Trim();
            return left.Length > 0 && right.Length > 0;
        }
        return false;
    }

    private static readonly Dictionary<char, char> SubSupDigits = new()
    {
        ['₀'] = '0', ['₁'] = '1', ['₂'] = '2', ['₃'] = '3', ['₄'] = '4',
        ['₅'] = '5', ['₆'] = '6', ['₇'] = '7', ['₈'] = '8', ['₉'] = '9',
        ['⁰'] = '0', ['¹'] = '1', ['²'] = '2', ['³'] = '3', ['⁴'] = '4',
        ['⁵'] = '5', ['⁶'] = '6', ['⁷'] = '7', ['⁸'] = '8', ['⁹'] = '9',
    };

    /// <summary>规范化「类 LaTeX / 卡西欧式」输入：去掉 _{下标}、Unicode 上下标、\ce{} 外壳与空白。</summary>
    private static string NormalizeFormulaInput(string s)
    {
        if (string.IsNullOrEmpty(s)) return string.Empty;
        var t = Regex.Replace(s, @"_\{([^}]*)\}", "$1");
        t = Regex.Replace(t, @"_(\w)", "$1");
        t = Regex.Replace(t, @"\\(?:ce|mathrm|text|mathit)\{([^}]*)\}", "$1");

        var sb = new StringBuilder(t.Length);
        foreach (var ch in t)
        {
            if (SubSupDigits.TryGetValue(ch, out var d)) sb.Append(d);
            else if (ch == '$') continue;
            else sb.Append(ch);
        }
        return sb.ToString().Replace(" ", string.Empty).Replace("\t", string.Empty).Trim();
    }

    /// <summary>尝试把字符串整体按化学式文法解析（元素 + 计数 + 括号组 + 前导系数）。</summary>
    private static bool TryParseFormula(string s, out Dictionary<string, int> elements, out int coefficient)
    {
        elements = new Dictionary<string, int>(StringComparer.Ordinal);
        coefficient = 1;
        if (string.IsNullOrEmpty(s)) return false;

        var i = 0;
        var start = i;
        while (i < s.Length && char.IsDigit(s[i])) i++;
        if (i > start)
        {
            if (!int.TryParse(s[start..i], NumberStyles.None, CultureInfo.InvariantCulture, out coefficient)
                || coefficient <= 0)
            {
                coefficient = 1;
                return false;
            }
        }

        var topSeen = new HashSet<string>(StringComparer.Ordinal);
        if (!ParseFormulaGroup(s, ref i, elements, topSeen, true)) return false;
        if (i != s.Length) return false;
        if (elements.Count == 0) return false;

        // 仅靠单个元素符号无法区分（如 C 既是甲烷 SMILES 又可看作化学式），要求具备明显特征。
        var hasDigit = s.Any(char.IsDigit);
        var hasParen = s.Contains('(');
        if (!hasDigit && !hasParen && elements.Count < 2) return false;

        return true;
    }

    private static bool ParseFormulaGroup(string s, ref int i, Dictionary<string, int> elements,
        HashSet<string> topSeen, bool top)
    {
        var any = false;
        while (i < s.Length)
        {
            var c = s[i];
            if (c == '(')
            {
                i++;
                var inner = new Dictionary<string, int>(StringComparer.Ordinal);
                var innerSeen = new HashSet<string>(StringComparer.Ordinal);
                if (!ParseFormulaGroup(s, ref i, inner, innerSeen, false)) return false;
                if (i >= s.Length || s[i] != ')') return false;
                i++;
                var mult = ReadCount(s, ref i, 1);
                foreach (var kv in inner) AddCount(elements, kv.Key, kv.Value * mult);
                any = true;
                continue;
            }
            if (c == ')') return any;
            if (char.IsUpper(c))
            {
                var symbol = c.ToString();
                i++;
                if (i < s.Length && char.IsLower(s[i]))
                {
                    var two = symbol + s[i];
                    if (AllElements.Contains(two)) { symbol = two; i++; }
                }
                if (!AllElements.Contains(symbol)) return false;
                var count = ReadCount(s, ref i, 1);
                if (top && !topSeen.Add(symbol)) return false; // 顶层同一元素重复 → 更可能是 SMILES
                AddCount(elements, symbol, count);
                any = true;
                continue;
            }
            return false;
        }
        return any;
    }

    private static int ReadCount(string s, ref int i, int fallback)
    {
        var start = i;
        while (i < s.Length && char.IsDigit(s[i])) i++;
        if (i == start) return fallback;
        return int.TryParse(s[start..i], NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0
            ? n : fallback;
    }

    private static void AddCount(Dictionary<string, int> el, string symbol, int count)
        => el[symbol] = el.TryGetValue(symbol, out var n) ? n + count : count;

    private static Species SpeciesFromFormula(Dictionary<string, int> elements)
    {
        var formula = HillFormula(elements);
        return new Species
        {
            Smiles = formula,
            Elements = elements,
            Formula = formula,
            MolecularWeight = elements.Sum(kv => AtomicMass(kv.Key) * kv.Value),
            HeavyCount = elements.Where(kv => kv.Key != "H").Sum(kv => kv.Value),
            HydrogenCount = elements.TryGetValue("H", out var h) ? h : 0,
            BondEnergy = 0,
        };
    }

    private static readonly HashSet<string> AllElements = new(StringComparer.Ordinal)
    {
        "H","He","Li","Be","B","C","N","O","F","Ne","Na","Mg","Al","Si","P","S","Cl","Ar",
        "K","Ca","Sc","Ti","V","Cr","Mn","Fe","Co","Ni","Cu","Zn","Ga","Ge","As","Se","Br","Kr",
        "Rb","Sr","Y","Zr","Nb","Mo","Tc","Ru","Rh","Pd","Ag","Cd","In","Sn","Sb","Te","I","Xe",
        "Cs","Ba","La","Ce","Pr","Nd","Pm","Sm","Eu","Gd","Tb","Dy","Ho","Er","Tm","Yb","Lu",
        "Hf","Ta","W","Re","Os","Ir","Pt","Au","Hg","Tl","Pb","Bi","Po","At","Rn",
        "Fr","Ra","Ac","Th","Pa","U","Np","Pu","Am","Cm","Bk","Cf","Es","Fm","Md","No","Lr",
    };

    // ==================== SMILES 解析 ====================

    private sealed class Atom
    {
        public string Element = "C";
        public bool Aromatic;
        public int? ExplicitH;
    }

    private sealed class BondRef
    {
        public int A;
        public int B;
        public double Order = 1;
    }

    private sealed class Species
    {
        public string Smiles = string.Empty;
        public List<Atom> Atoms = new();
        public List<BondRef> Bonds = new();
        public Dictionary<string, int> Elements = new(StringComparer.Ordinal);
        public string Formula = string.Empty;
        public double BondEnergy; // kJ/mol
        public double MolecularWeight;
        public int HeavyCount;
        public int HydrogenCount;
        public int DoubleBonds;
        public int TripleBonds;
        public int AromaticBonds;
    }

    private static Species ParseSpecies(string rawInput)
    {
        var smiles = NormalizeFormulaInput(rawInput);
        if (string.IsNullOrEmpty(smiles)) throw new InvalidOperationException("输入为空");

        // 先尝试按「化学式」解析（Hill 记法 / 括号组 / 前导系数，如 C2H4、Ca(OH)2、2H2O）。
        if (TryParseFormula(smiles, out var formulaElements, out var coefficient))
        {
            if (coefficient != 1)
            {
                var scaled = new Dictionary<string, int>(StringComparer.Ordinal);
                foreach (var kv in formulaElements) scaled[kv.Key] = kv.Value * coefficient;
                formulaElements = scaled;
            }
            return SpeciesFromFormula(formulaElements);
        }

        // 否则按 SMILES 解析。
        var atoms = new List<Atom>();
        var bonds = new List<BondRef>();
        var ringOpen = new Dictionary<string, int>(StringComparer.Ordinal);
        var branchStack = new Stack<int>();
        var prev = -1;
        var pendingOrder = 1.0;
        var pendingAromatic = false;

        for (var i = 0; i < smiles.Length; i++)
        {
            var c = smiles[i];
            switch (c)
            {
                case '(':
                    if (prev >= 0) branchStack.Push(prev);
                    continue;
                case ')':
                    if (branchStack.Count > 0) prev = branchStack.Pop();
                    continue;
                case '.':
                    prev = -1;
                    continue;
                case '-':
                    pendingOrder = 1;
                    continue;
                case '=':
                    pendingOrder = 2;
                    continue;
                case '#':
                    pendingOrder = 3;
                    continue;
                case ':':
                    pendingOrder = 1.5;
                    pendingAromatic = true;
                    continue;
                case '/':
                case '\\':
                    pendingOrder = 1;
                    continue;
            }

            if (c == '[')
            {
                var end = smiles.IndexOf(']', i + 1);
                if (end < 0) throw new InvalidOperationException("方括号未闭合");
                var content = smiles.Substring(i + 1, end - i - 1);
                var atom = ParseBracketAtom(content);
                atoms.Add(atom);
                var index = atoms.Count - 1;
                if (prev >= 0) AddBond(bonds, prev, index, pendingOrder, pendingAromatic);
                prev = index;
                pendingOrder = 1;
                pendingAromatic = false;
                i = end;
                continue;
            }

            if (char.IsDigit(c) || c == '%')
            {
                string label;
                if (c == '%')
                {
                    if (i + 2 >= smiles.Length) throw new InvalidOperationException("环闭合编号不完整");
                    label = smiles.Substring(i + 1, 2);
                    i += 2;
                }
                else
                {
                    label = c.ToString();
                }
                if (prev < 0) throw new InvalidOperationException($"环闭合 {label} 前没有原子");
                if (ringOpen.TryGetValue(label, out var open))
                {
                    AddBond(bonds, open, prev, pendingOrder, pendingAromatic);
                    ringOpen.Remove(label);
                }
                else
                {
                    ringOpen[label] = prev;
                }
                pendingOrder = 1;
                pendingAromatic = false;
                continue;
            }

            var element = ReadElement(smiles, ref i, out var aromatic);
            if (element is null) throw new InvalidOperationException($"无法识别的字符 “{c}”");
            atoms.Add(new Atom { Element = element, Aromatic = aromatic });
            var newIndex = atoms.Count - 1;
            if (prev >= 0)
            {
                AddBond(bonds, prev, newIndex, aromatic || pendingAromatic ? 1.5 : pendingOrder, pendingAromatic);
            }
            prev = newIndex;
            pendingOrder = 1;
            pendingAromatic = false;
        }

        if (ringOpen.Count > 0) throw new InvalidOperationException("存在未闭合的环编号");

        var species = new Species { Smiles = smiles, Atoms = atoms, Bonds = bonds };
        BuildProperties(species);
        return species;
    }

    private static Atom ParseBracketAtom(string content)
    {
        if (string.IsNullOrWhiteSpace(content)) throw new InvalidOperationException("空方括号原子");
        var i = 0;
        while (i < content.Length && char.IsDigit(content[i])) i++; // 跳过同位素
        if (i >= content.Length) throw new InvalidOperationException("方括号内缺少元素");
        var element = content[i].ToString();
        var aromatic = char.IsLower(content[i]);
        if (aromatic) element = element.ToUpperInvariant();
        if (i + 1 < content.Length && char.IsLower(content[i + 1]))
        {
            var two = element + content[i + 1].ToString().ToUpperInvariant();
            if (TwoLetterElements.Contains(two))
            {
                element = two;
                i++;
            }
        }

        int? explicitH = null;
        for (var j = i + 1; j < content.Length; j++)
        {
            if (content[j] != 'H') continue;
            var k = j + 1;
            var digits = string.Empty;
            while (k < content.Length && char.IsDigit(content[k])) digits += content[k++];
            explicitH = digits.Length == 0 ? 1 : int.Parse(digits, CultureInfo.InvariantCulture);
            break;
        }
        return new Atom { Element = element, Aromatic = aromatic, ExplicitH = explicitH ?? 0 };
    }

    private static string? ReadElement(string smiles, ref int i, out bool aromatic)
    {
        aromatic = false;
        var c = smiles[i];
        if (char.IsLetter(c))
        {
            aromatic = char.IsLower(c);
            var e = char.ToUpperInvariant(c).ToString();
            if (!aromatic && i + 1 < smiles.Length && char.IsLower(smiles[i + 1]))
            {
                var two = e + char.ToUpperInvariant(smiles[i + 1]);
                if (TwoLetterElements.Contains(two))
                {
                    e = two;
                    i++;
                }
            }
            return e;
        }
        if (c == '*') return "X";
        return null;
    }

    private static readonly HashSet<string> TwoLetterElements = new(StringComparer.Ordinal)
    {
        "Cl", "Br", "Si", "Na", "Li", "Mg", "Al", "Ca", "Fe", "Zn", "Cu", "Mn",
        "Ni", "Co", "Cr", "Sn", "Pb", "Ag", "Au", "Pt", "Hg", "Ba", "Se", "As", "Sb", "Bi"
    };

    private static void AddBond(List<BondRef> bonds, int a, int b, double order, bool aromatic)
    {
        if (a == b) return;
        var o = aromatic && Math.Abs(order - 1) < 1e-9 ? 1.5 : order;
        bonds.Add(new BondRef { A = a, B = b, Order = o });
    }

    private static void BuildProperties(Species s)
    {
        var hydrogenCounts = new int[s.Atoms.Count];

        for (var i = 0; i < s.Atoms.Count; i++)
        {
            var atom = s.Atoms[i];
            if (atom.ExplicitH.HasValue)
            {
                hydrogenCounts[i] = atom.ExplicitH.Value;
                continue;
            }
            var used = 0.0;
            foreach (var b in s.Bonds) if (b.A == i || b.B == i) used += b.Order;
            var valence = Valence(atom.Element);
            if (valence <= 0) continue;
            hydrogenCounts[i] = Math.Max(0, (int)Math.Floor(valence - used + 0.2));
        }

        s.HeavyCount = s.Atoms.Count(a => a.Element != "H");
        s.HydrogenCount = hydrogenCounts.Sum();

        var elements = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var a in s.Atoms)
        {
            elements[a.Element] = elements.TryGetValue(a.Element, out var n) ? n + 1 : 1;
        }
        if (s.HydrogenCount > 0)
        {
            elements["H"] = elements.TryGetValue("H", out var h) ? h + s.HydrogenCount : s.HydrogenCount;
        }
        s.Elements = elements;
        s.Formula = HillFormula(elements);
        s.MolecularWeight = elements.Sum(kv => AtomicMass(kv.Key) * kv.Value);

        // 键能合计（含 C-H 等隐式氢键）
        var energy = 0.0;
        foreach (var b in s.Bonds)
        {
            energy += BondEnergy(s.Atoms[b.A].Element, s.Atoms[b.B].Element, b.Order);
            if (b.Order >= 2.9) s.TripleBonds++;
            else if (b.Order >= 1.9) s.DoubleBonds++;
            else if (b.Order > 1.1) s.AromaticBonds++;
        }
        for (var i = 0; i < s.Atoms.Count; i++)
        {
            var a = s.Atoms[i];
            energy += hydrogenCounts[i] * BondEnergy(a.Element, "H", 1);
        }
        s.BondEnergy = energy;
    }

    private static int Valence(string element) => element switch
    {
        "C" => 4,
        "Si" => 4,
        "N" => 3,
        "P" => 3,
        "B" => 3,
        "O" => 2,
        "S" => 2,
        "F" or "Cl" or "Br" or "I" => 1,
        _ => 0,
    };

    private static double AtomicMass(string element) => element switch
    {
        "H" => 1.008,
        "C" => 12.011,
        "N" => 14.007,
        "O" => 15.999,
        "S" => 32.06,
        "P" => 30.974,
        "F" => 18.998,
        "Cl" => 35.45,
        "Br" => 79.904,
        "I" => 126.90,
        "B" => 10.81,
        "Si" => 28.085,
        "Na" => 22.990,
        "K" => 39.098,
        "Ca" => 40.078,
        "Mg" => 24.305,
        "Fe" => 55.845,
        "Zn" => 65.38,
        "Cu" => 63.546,
        _ => 12.0,
    };

    private static string HillFormula(Dictionary<string, int> elements)
    {
        var sb = new StringBuilder();
        void Append(string el)
        {
            if (!elements.TryGetValue(el, out var n) || n <= 0) return;
            sb.Append(el);
            if (n > 1) sb.Append(n.ToString(CultureInfo.InvariantCulture));
        }
        if (elements.ContainsKey("C"))
        {
            Append("C");
            Append("H");
            foreach (var el in elements.Keys.Where(k => k != "C" && k != "H").OrderBy(k => k, StringComparer.Ordinal))
            {
                Append(el);
            }
        }
        else
        {
            foreach (var el in elements.Keys.OrderBy(k => k, StringComparer.Ordinal)) Append(el);
        }
        return sb.Length == 0 ? "—" : sb.ToString();
    }

    // ==================== 键能 / 熵 / 生成焓 ====================

    private static double BondEnergy(string e1, string e2, double order)
    {
        var key = string.CompareOrdinal(e1, e2) <= 0 ? $"{e1}|{e2}" : $"{e2}|{e1}";
        if (!BondEnergies.TryGetValue(key, out var set)) return 300.0;
        return order switch
        {
            >= 2.9 => set.Triple,
            >= 1.9 => set.Double,
            > 1.1 => (set.Single + set.Double) / 2,
            _ => set.Single,
        };
    }

    private static readonly Dictionary<string, (double Single, double Double, double Triple)> BondEnergies =
        new(StringComparer.Ordinal)
        {
            ["C|C"] = (346, 614, 839),
            ["C|H"] = (413, 413, 413),
            ["C|O"] = (358, 745, 1072),
            ["C|N"] = (305, 615, 891),
            ["C|S"] = (259, 477, 573),
            ["C|F"] = (485, 485, 485),
            ["C|Cl"] = (328, 328, 328),
            ["C|Br"] = (276, 276, 276),
            ["C|I"] = (240, 240, 240),
            ["N|H"] = (391, 391, 391),
            ["N|N"] = (160, 418, 946),
            ["N|O"] = (201, 607, 607),
            ["O|H"] = (463, 463, 463),
            ["O|O"] = (146, 498, 498),
            ["S|H"] = (339, 339, 339),
            ["S|O"] = (265, 523, 523),
            ["S|S"] = (266, 425, 425),
            ["H|H"] = (436, 436, 436),
            ["F|F"] = (158, 158, 158),
            ["Cl|Cl"] = (242, 242, 242),
            ["Br|Br"] = (193, 193, 193),
            ["I|I"] = (151, 151, 151),
            ["B|O"] = (536, 536, 536),
            ["B|H"] = (389, 389, 389),
            ["C|B"] = (356, 356, 356),
            ["C|P"] = (264, 264, 264),
            ["P|O"] = (335, 544, 544),
            ["P|H"] = (322, 322, 322),
            ["P|Cl"] = (326, 326, 326),
            ["Si|O"] = (452, 452, 452),
            ["Si|H"] = (318, 318, 318),
            ["Si|C"] = (318, 318, 318),
        };

    /// <summary>标准生成焓（kJ/mol）与标准摩尔熵（J/(mol·K)），气相为主。</summary>
    private static readonly Dictionary<string, (double Hf, double S)> KnownSpecies =
        new(StringComparer.Ordinal)
        {
            ["H2"] = (0, 130.7),
            ["O2"] = (0, 205.1),
            ["N2"] = (0, 191.6),
            ["O3"] = (142.7, 238.9),
            ["CO"] = (-110.5, 197.7),
            ["CO2"] = (-393.5, 213.8),
            ["H2O"] = (-241.8, 188.8),
            ["H2O2"] = (-136.3, 232.7),
            ["CH4"] = (-74.8, 186.3),
            ["C2H2"] = (226.7, 200.9),
            ["C2H4"] = (52.5, 219.6),
            ["C2H6"] = (-84.7, 229.6),
            ["C3H8"] = (-103.8, 270.0),
            ["C4H10"] = (-125.7, 310.0),
            ["C6H6"] = (82.9, 269.3),
            ["CH4O"] = (-201.0, 239.8), // 甲醇
            ["C2H6O"] = (-235.1, 282.7), // 乙醇
            ["C2H4O2"] = (-432.2, 282.8), // 乙酸
            ["NH3"] = (-45.9, 192.8),
            ["NO"] = (90.3, 210.8),
            ["NO2"] = (33.2, 240.1),
            ["N2O"] = (82.1, 219.9),
            ["SO2"] = (-296.8, 248.2),
            ["SO3"] = (-395.7, 256.8),
            ["HCl"] = (-92.3, 186.9),
            ["HBr"] = (-36.3, 198.7),
            ["Cl2"] = (0, 223.1),
            ["Br2"] = (30.9, 245.5),
            ["I2"] = (62.4, 260.7),
            ["F2"] = (0, 202.8),
        };

    /// <summary>熵近似：以重原子/氢/双键/三键贡献估算（仅用于未知物质）。</summary>
    private static double EstimateEntropy(Species s)
    {
        var heavy = Math.Max(0, s.HeavyCount - 1);
        return 150 + 24.0 * heavy + 9.0 * s.HydrogenCount + 12.0 * s.DoubleBonds
               + 8.0 * s.TripleBonds + 6.0 * s.AromaticBonds;
    }

    // ==================== 格式化 ====================

    private static string Format(double v)
    {
        if (double.IsNaN(v) || double.IsInfinity(v)) return "—";
        if (Math.Abs(v) >= 1e6 || (Math.Abs(v) > 1e-4 && Math.Abs(v) < 1e-2))
        {
            return v.ToString("0.###e+0", CultureInfo.InvariantCulture);
        }
        return v.ToString("0.###", CultureInfo.InvariantCulture);
    }

    private static string FormatK(double v)
    {
        if (double.IsPositiveInfinity(v)) return "∞";
        if (double.IsNaN(v)) return "—";
        if (v == 0) return "0";
        if (v >= 1e6 || v < 1e-4) return v.ToString("0.###e+0", CultureInfo.InvariantCulture);
        return v.ToString("0.####", CultureInfo.InvariantCulture);
    }

    private static string FormatTime(double seconds)
    {
        if (double.IsNaN(seconds) || double.IsInfinity(seconds)) return "—";
        if (seconds >= 86400 * 365) return $"{seconds / (86400 * 365):0.##e+0} 年";
        if (seconds >= 86400) return $"{seconds / 86400:0.##} 天";
        if (seconds >= 3600) return $"{seconds / 3600:0.##} 小时";
        if (seconds >= 1) return $"{seconds:0.###} 秒";
        return $"{seconds * 1000:0.###} 毫秒";
    }

    private static string RateGrade(double k)
    {
        if (double.IsInfinity(k) || k >= 1e3) return "极快（毫秒级完成）";
        if (k >= 1) return "快（秒级）";
        if (k >= 1e-3) return "中等（分钟级）";
        if (k >= 1e-6) return "慢（小时~天级）";
        return "极慢（常温下几乎观察不到）";
    }
}
