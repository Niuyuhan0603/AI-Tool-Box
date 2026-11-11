using System.Globalization;

namespace AI_Tool_Box.Services;

/// <summary>电路元件类型。</summary>
public enum CircuitKind
{
    /// <summary>电池（电动势 + 内阻）。</summary>
    Battery,
    /// <summary>电阻。</summary>
    Resistor,
    /// <summary>电容。</summary>
    Capacitor,
    /// <summary>电感。</summary>
    Inductor,
    /// <summary>开关。</summary>
    Switch,
    /// <summary>电流表（理想电流表内阻很小，用电阻模型求解并显示电流）。</summary>
    Ammeter,
    /// <summary>电压表（理想电压表内阻很大，用电阻模型求解并显示电压）。</summary>
    Voltmeter,
}

/// <summary>电路元件：两端器件（端子 0 在左、端子 1 在右）。</summary>
public sealed class CircuitElement
{
    /// <summary>唯一 ID（端子编号 = Id*2 + 0/1）。</summary>
    public int Id { get; set; }

    /// <summary>元件类型。</summary>
    public CircuitKind Kind { get; set; }

    /// <summary>画布坐标（米，中心）。</summary>
    public double X { get; set; }

    /// <summary>画布坐标（米，中心）。</summary>
    public double Y { get; set; }

    /// <summary>显示标签（自动生成，如 R1、E1）。</summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>电池电动势（V）。</summary>
    public double Emf { get; set; } = 6;

    /// <summary>电池内阻（Ω），0 表示理想电压源。</summary>
    public double InternalResistance { get; set; } = 0.5;

    /// <summary>电阻阻值（Ω）。</summary>
    public double Resistance { get; set; } = 10;

    /// <summary>电容（F）。</summary>
    public double Capacitance { get; set; } = 0.001;

    /// <summary>电感（H）。</summary>
    public double Inductance { get; set; } = 0.1;

    /// <summary>开关是否闭合。</summary>
    public bool Closed { get; set; } = true;

    /// <summary>电容电压（暂态状态量）。</summary>
    public double CapacitorVoltage { get; set; }

    /// <summary>电感电流（暂态状态量）。</summary>
    public double InductorCurrent { get; set; }
}

/// <summary>导线：连接两个端子（端子编号 = 元件 Id*2 + 0/1）。</summary>
public sealed class CircuitWire
{
    /// <summary>端子 A。</summary>
    public int TerminalA { get; set; }

    /// <summary>端子 B。</summary>
    public int TerminalB { get; set; }
}

/// <summary>电路求解结果。</summary>
public sealed class CircuitSolveResult
{
    /// <summary>是否求解成功。</summary>
    public bool Ok { get; init; }

    /// <summary>说明（失败原因或节点数等）。</summary>
    public string Message { get; init; } = string.Empty;

    /// <summary>各端子所在节点的电压（相对参考地）。</summary>
    public IReadOnlyDictionary<int, double> TerminalVoltages { get; init; } = new Dictionary<int, double>();

    /// <summary>各元件电流（A，按端子 0 → 端子 1 的规定方向）。</summary>
    public IReadOnlyDictionary<int, double> ElementCurrents { get; init; } = new Dictionary<int, double>();

    /// <summary>各元件电压（V，端子 0 → 端子 1）。</summary>
    public IReadOnlyDictionary<int, double> ElementVoltages { get; init; } = new Dictionary<int, double>();
}

/// <summary>
/// 直流 / 暂态电路求解器（改进节点法 MNA）。
/// 电阻直接 stamp 电导；电池用戴维南等效（E 串联 r）→ 诺顿（E/r 并联 1/r）；
/// 电容/电感用后向欧拉伴随模型；闭合开关与直流稳态电感视为短接（节点合并）。
/// </summary>
public static class CircuitSolver
{
    /// <summary>求解电路。</summary>
    /// <param name="elements">元件集合。</param>
    /// <param name="wires">导线集合。</param>
    /// <param name="dt">暂态步长（秒）；传 0 表示直流稳态。</param>
    public static CircuitSolveResult Solve(IReadOnlyList<CircuitElement> elements, IReadOnlyList<CircuitWire> wires, double dt = 0)
    {
        if (elements.Count == 0) return Fail("电路为空：先添加电池、电阻等元件。");

        var transient = dt > 0;
        var parent = new Dictionary<int, int>();
        int Find(int x)
        {
            if (!parent.TryGetValue(x, out var p)) { parent[x] = x; return x; }
            if (p == x) return x;
            var root = Find(p);
            parent[x] = root;
            return root;
        }
        void Union(int a, int b)
        {
            var ra = Find(a);
            var rb = Find(b);
            if (ra != rb) parent[rb] = ra;
        }

        foreach (var e in elements)
        {
            Find(e.Id * 2);
            Find(e.Id * 2 + 1);
        }
        foreach (var w in wires) Union(w.TerminalA, w.TerminalB);
        foreach (var e in elements)
        {
            if (e.Kind == CircuitKind.Switch && e.Closed) Union(e.Id * 2, e.Id * 2 + 1);
            if (!transient && e.Kind == CircuitKind.Inductor) Union(e.Id * 2, e.Id * 2 + 1);
        }

        // 节点编号：根 → 索引；参考地 = 第一个电池的负极端子所在节点（无电池则取第一个端子）
        var roots = new List<int>();
        var rootIndex = new Dictionary<int, int>();
        foreach (var e in elements)
        {
            foreach (var key in new[] { e.Id * 2, e.Id * 2 + 1 })
            {
                var r = Find(key);
                if (!rootIndex.ContainsKey(r)) { rootIndex[r] = roots.Count; roots.Add(r); }
            }
        }
        var groundTerminal = elements.FirstOrDefault(e => e.Kind == CircuitKind.Battery)?.Id * 2;
        var groundRoot = Find(groundTerminal ?? elements[0].Id * 2);
        var groundIndex = rootIndex[groundRoot];

        // 电压源支路（仅理想电池 r = 0）：额外未知量
        var idealSources = elements
            .Where(e => e.Kind == CircuitKind.Battery && e.InternalResistance <= 1e-9)
            .ToList();
        var nodeCount = roots.Count - 1;
        var size = nodeCount + idealSources.Count;
        if (size <= 0) return Fail("电路只有参考地节点，无法求解。");

        var a = new double[size, size];
        var z = new double[size];

        int Idx(int terminal)
        {
            var idx = rootIndex[Find(terminal)];
            return idx == groundIndex ? -1 : (idx < groundIndex ? idx : idx - 1);
        }

        void StampG(int t1, int t2, double g)
        {
            var i = Idx(t1);
            var j = Idx(t2);
            if (i >= 0) a[i, i] += g;
            if (j >= 0) a[j, j] += g;
            if (i >= 0 && j >= 0) { a[i, j] -= g; a[j, i] -= g; }
        }

        // 电流源：正方向 t1 → t2
        void StampI(int t1, int t2, double current)
        {
            var i = Idx(t1);
            var j = Idx(t2);
            if (i >= 0) z[i] -= current;
            if (j >= 0) z[j] += current;
        }

        var sourceSlot = 0;
        foreach (var e in elements)
        {
            var t0 = e.Id * 2;
            var t1 = e.Id * 2 + 1;
            switch (e.Kind)
            {
                case CircuitKind.Resistor:
                case CircuitKind.Ammeter:
                case CircuitKind.Voltmeter:
                {
                    var r = Math.Max(1e-9, e.Resistance);
                    StampG(t0, t1, 1.0 / r);
                    break;
                }
                case CircuitKind.Capacitor when transient:
                {
                    var g = Math.Max(1e-12, e.Capacitance) / dt;
                    StampG(t0, t1, g);
                    StampI(t0, t1, -g * e.CapacitorVoltage);
                    break;
                }
                case CircuitKind.Inductor when transient:
                {
                    var g = dt / Math.Max(1e-9, e.Inductance);
                    StampG(t0, t1, g);
                    StampI(t0, t1, e.InductorCurrent);
                    break;
                }
                case CircuitKind.Battery:
                {
                    if (e.InternalResistance > 1e-9)
                    {
                        var g = 1.0 / e.InternalResistance;
                        StampG(t0, t1, g);
                        StampI(t0, t1, e.Emf * g);
                    }
                    else
                    {
                        var k = nodeCount + sourceSlot++;
                        var i0 = Idx(t0);
                        var i1 = Idx(t1);
                        if (i0 >= 0) { a[i0, k] += 1; a[k, i0] -= 1; }
                        if (i1 >= 0) { a[i1, k] -= 1; a[k, i1] += 1; }
                        z[k] = e.Emf; // V(1) - V(0) = E
                    }
                    break;
                }
                default:
                    break; // 开关/直流电容/直流电感已在节点合并或开路中处理
            }
        }

        // 微小对地漏电，避免悬空节点导致矩阵奇异
        for (var i = 0; i < nodeCount; i++) a[i, i] += 1e-9;

        if (!Gauss(a, z, size)) return Fail("无法求解：请检查是否有悬空节点或短路电压源。");

        var terminalVoltages = new Dictionary<int, double>();
        foreach (var e in elements)
        {
            foreach (var terminal in new[] { e.Id * 2, e.Id * 2 + 1 })
            {
                var idx = rootIndex[Find(terminal)];
                double v;
                if (idx == groundIndex) v = 0;
                else
                {
                    var slot = idx < groundIndex ? idx : idx - 1;
                    v = z[slot];
                }
                terminalVoltages[terminal] = v;
            }
        }

        var currents = new Dictionary<int, double>();
        var voltages = new Dictionary<int, double>();
        foreach (var e in elements)
        {
            var v0 = terminalVoltages[e.Id * 2];
            var v1 = terminalVoltages[e.Id * 2 + 1];
            var v = v0 - v1;
            voltages[e.Id] = v;
            double current;
            switch (e.Kind)
            {
                case CircuitKind.Resistor:
                case CircuitKind.Ammeter:
                case CircuitKind.Voltmeter:
                    current = v / Math.Max(1e-9, e.Resistance);
                    break;
                case CircuitKind.Capacitor:
                    current = transient
                        ? Math.Max(1e-12, e.Capacitance) / dt * (v - e.CapacitorVoltage)
                        : 0;
                    if (transient) e.CapacitorVoltage = v;
                    break;
                case CircuitKind.Inductor:
                    if (transient)
                    {
                        current = e.InductorCurrent + dt / Math.Max(1e-9, e.Inductance) * v;
                        e.InductorCurrent = current;
                    }
                    else
                    {
                        current = 0;
                    }
                    break;
                case CircuitKind.Battery:
                {
                    var e0 = e.Emf;
                    current = e.InternalResistance > 1e-9
                        ? (e0 - (v1 - v0)) / e.InternalResistance
                        : 0;
                    break;
                }
                case CircuitKind.Switch:
                    current = e.Closed ? 0 : 0;
                    break;
                default:
                    current = 0;
                    break;
            }
            currents[e.Id] = current;
        }

        return new CircuitSolveResult
        {
            Ok = true,
            Message = transient
                ? $"暂态求解完成（dt = {dt * 1000:0.###} ms，节点 {roots.Count} 个）。"
                : $"直流求解完成（节点 {roots.Count} 个，理想电压源 {idealSources.Count} 个）。",
            TerminalVoltages = terminalVoltages,
            ElementCurrents = currents,
            ElementVoltages = voltages,
        };
    }

    private static CircuitSolveResult Fail(string message) => new() { Ok = false, Message = message };

    /// <summary>高斯消元（部分主元）。</summary>
    private static bool Gauss(double[,] a, double[] b, int n)
    {
        for (var col = 0; col < n; col++)
        {
            var pivot = col;
            var best = Math.Abs(a[col, col]);
            for (var row = col + 1; row < n; row++)
            {
                var value = Math.Abs(a[row, col]);
                if (value > best) { best = value; pivot = row; }
            }
            if (best < 1e-12) return false;
            if (pivot != col)
            {
                for (var k = col; k < n; k++)
                {
                    (a[col, k], a[pivot, k]) = (a[pivot, k], a[col, k]);
                }
                (b[col], b[pivot]) = (b[pivot], b[col]);
            }
            var diag = a[col, col];
            for (var row = col + 1; row < n; row++)
            {
                var factor = a[row, col] / diag;
                if (factor == 0) continue;
                for (var k = col; k < n; k++) a[row, k] -= factor * a[col, k];
                b[row] -= factor * b[col];
            }
        }
        for (var row = n - 1; row >= 0; row--)
        {
            var sum = b[row];
            for (var col = row + 1; col < n; col++) sum -= a[row, col] * b[col];
            b[row] = sum / a[row, row];
        }
        return true;
    }

    /// <summary>元件短名称（界面显示）。</summary>
    public static string KindName(CircuitKind kind) => kind switch
    {
        CircuitKind.Battery => "电池",
        CircuitKind.Resistor => "电阻",
        CircuitKind.Capacitor => "电容",
        CircuitKind.Inductor => "电感",
        CircuitKind.Switch => "开关",
        CircuitKind.Ammeter => "电流表",
        CircuitKind.Voltmeter => "电压表",
        _ => kind.ToString(),
    };

    /// <summary>元件参数摘要（界面显示）。</summary>
    public static string Describe(CircuitElement e) => e.Kind switch
    {
        CircuitKind.Battery => $"{e.Emf.ToString("0.###", CultureInfo.InvariantCulture)} V / r={e.InternalResistance.ToString("0.###", CultureInfo.InvariantCulture)} Ω",
        CircuitKind.Resistor => $"{e.Resistance.ToString("0.###", CultureInfo.InvariantCulture)} Ω",
        CircuitKind.Capacitor => $"{e.Capacitance.ToString("0.###e+0", CultureInfo.InvariantCulture)} F",
        CircuitKind.Inductor => $"{e.Inductance.ToString("0.###", CultureInfo.InvariantCulture)} H",
        CircuitKind.Switch => e.Closed ? "闭合" : "断开",
        CircuitKind.Ammeter => $"量程内阻 {e.Resistance.ToString("0.###", CultureInfo.InvariantCulture)} Ω",
        CircuitKind.Voltmeter => $"内阻 {e.Resistance.ToString("0.###e+0", CultureInfo.InvariantCulture)} Ω",
        _ => string.Empty,
    };
}
