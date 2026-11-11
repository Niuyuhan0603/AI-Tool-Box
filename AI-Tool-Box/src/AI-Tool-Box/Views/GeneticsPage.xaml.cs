using AI_Tool_Box.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Serilog;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace AI_Tool_Box.Views;

/// <summary>生物 / 遗传模拟器：单/双基因杂交、哈代-温伯格平衡、AI 解释与出题。</summary>
public sealed partial class GeneticsPage : Page
{
    private readonly ILogger _logger;
    private readonly IAiQuickService _ai;
    private readonly System.Collections.ObjectModel.ObservableCollection<string> _genotypes = new();
    private readonly System.Collections.ObjectModel.ObservableCollection<string> _phenotypes = new();

    /// <summary>初始化页面。</summary>
    public GeneticsPage()
    {
        InitializeComponent();
        _logger = App.Services.GetService(typeof(ILogger)) as ILogger ?? Serilog.Log.Logger;
        _ai = App.Services.GetRequiredService<IAiQuickService>();
        GenotypeList.ItemsSource = _genotypes;
        PhenotypeList.ItemsSource = _phenotypes;
        Loaded += (_, _) => _logger.Information("GeneticsPage 已加载");
    }

    // ==================== 杂交 ====================

    private void OnCrossClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var father = ParseGenotype(FatherBox.Text);
            var mother = ParseGenotype(MotherBox.Text);
            if (father.Count == 0 || mother.Count != father.Count)
                throw new ArgumentException("父本/母本基因型格式需一致，例如 Aa 或 AaBb。");

            var fatherGametes = BuildGametes(father);
            var motherGametes = BuildGametes(mother);
            var genotypes = new Dictionary<string, int>();
            var phenotypes = new Dictionary<string, int>();

            foreach (var fg in fatherGametes)
            {
                foreach (var mg in motherGametes)
                {
                    var child = Combine(fg, mg);
                    genotypes[child] = genotypes.GetValueOrDefault(child) + 1;
                    var pheno = Phenotype(child, father.Count);
                    phenotypes[pheno] = phenotypes.GetValueOrDefault(pheno) + 1;
                }
            }

            var total = fatherGametes.Count * motherGametes.Count;
            _genotypes.Clear();
            foreach (var kv in genotypes.OrderByDescending(k => k.Value).ThenBy(k => k.Key))
                _genotypes.Add($"{kv.Key}   {kv.Value}/{total}   {Percent(kv.Value, total)}");

            _phenotypes.Clear();
            foreach (var kv in phenotypes.OrderByDescending(k => k.Value).ThenBy(k => k.Key))
                _phenotypes.Add($"{kv.Key}   {kv.Value}/{total}   {Percent(kv.Value, total)}");

            CrossStatus.Text = $"父本 {FatherBox.Text} × 母本 {MotherBox.Text}，共 {total} 种组合。";
            StatusText.Text = "计算完成。";
        }
        catch (Exception ex)
        {
            CrossStatus.Text = $"计算失败：{ex.Message}";
        }
    }

    private void OnDihybridSampleClick(object sender, RoutedEventArgs e)
    {
        FatherBox.Text = "AaBb";
        MotherBox.Text = "AaBb";
        OnCrossClick(sender, e);
    }

    /// <summary>把 "AaBb" 解析为按基因分组的等位基因对：["Aa","Bb"]。</summary>
    private static List<string> ParseGenotype(string? text)
    {
        var raw = (text ?? string.Empty).Replace(" ", string.Empty);
        return Regex.Matches(raw, "[A-Za-z]{1,2}")
            .Select(m => m.Value)
            .Where(v => v.Length is 1 or 2)
            .Select(v => v.Length == 1 ? v + v.ToLowerInvariant() : v)
            .ToList();
    }

    /// <summary>由亲本基因型生成配子（每个基因取一个等位基因，笛卡尔积）。</summary>
    private static List<string> BuildGametes(List<string> genes)
    {
        var result = new List<string> { string.Empty };
        foreach (var gene in genes)
        {
            var alleles = gene.Distinct().ToList();
            var next = new List<string>();
            foreach (var prefix in result)
                foreach (var a in alleles)
                    next.Add(prefix + a);
            result = next;
        }
        return result.Distinct().ToList();
    }

    /// <summary>两个配子结合 → 子代基因型（每对等位基因大写在前）。</summary>
    private static string Combine(string g1, string g2)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < Math.Min(g1.Length, g2.Length); i++)
        {
            var a = g1[i];
            var b = g2[i];
            sb.Append(char.IsUpper(a) || (!char.IsUpper(b) && a <= b) ? string.Concat(a, b) : string.Concat(b, a));
        }
        return sb.ToString();
    }

    /// <summary>表现型：每对基因含大写即为显性。</summary>
    private static string Phenotype(string genotype, int geneCount)
    {
        var parts = new List<string>();
        for (var i = 0; i < geneCount; i++)
        {
            var pair = i * 2 + 2 <= genotype.Length ? genotype.Substring(i * 2, 2) : genotype[^1].ToString();
            var locus = char.ToUpperInvariant(pair[0]);
            var dominant = pair.Any(char.IsUpper);
            parts.Add($"{locus}:{(dominant ? "显性" : "隐性")}");
        }
        return string.Join("，", parts);
    }

    private static string Percent(int count, int total) =>
        total == 0 ? "0%" : (count * 100.0 / total).ToString("0.##", CultureInfo.InvariantCulture) + "%";

    // ==================== 哈代-温伯格 ====================

    private void OnHardyWeinbergClick(object sender, RoutedEventArgs e)
    {
        if (!double.TryParse(PBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var p) || p < 0 || p > 1)
        {
            HwResult.Text = "p 必须是 0–1 之间的数字。";
            return;
        }
        HwResult.Text = BuildHwText(p);
        StatusText.Text = "哈代-温伯格计算完成。";
    }

    private void OnInferFromPhenotypeClick(object sender, RoutedEventArgs e)
    {
        if (!double.TryParse(PBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var dominant) || dominant < 0 || dominant > 1)
        {
            HwResult.Text = "请输入 0–1 之间的显性表现型频率（例如 0.91）。";
            return;
        }
        var q2 = 1 - dominant; // 隐性表现型频率 = q²
        var q = Math.Sqrt(q2);
        var p = 1 - q;
        HwResult.Text = $"由显性表现型频率 {dominant:0.###} 反推：隐性表现型频率 q² = {q2:0.####}，q = {q:0.####}，p = {p:0.####}\n\n"
                        + BuildHwText(p);
        StatusText.Text = "反推完成。";
    }

    private static string BuildHwText(double p)
    {
        var q = 1 - p;
        return $"p = {p:0.####}，q = {q:0.####}\n"
               + $"AA（纯合显性）= p² = {p * p:0.####}（{p * p * 100:0.##}%）\n"
               + $"Aa（杂合）= 2pq = {2 * p * q:0.####}（{2 * p * q * 100:0.##}%）\n"
               + $"aa（纯合隐性）= q² = {q * q:0.####}（{q * q * 100:0.##}%）\n"
               + $"显性表现型合计 = {p * p + 2 * p * q:0.####}，隐性表现型 = {q * q:0.####}";
    }

    // ==================== AI ====================

    private async void OnAiExplainClick(object sender, RoutedEventArgs e)
    {
        var context = $"父本：{FatherBox.Text}，母本：{MotherBox.Text}\n"
                      + "子代基因型：" + string.Join("；", _genotypes) + "\n"
                      + "子代表现型：" + string.Join("；", _phenotypes) + "\n"
                      + "哈代-温伯格：\n" + HwResult.Text;
        await AskAiAsync("你是高中生物老师。请用简明中文解释这组遗传学计算结果：说明基因型/表现型比例是怎么来的，并指出 1–2 个常考易错点。",
            context);
    }

    private async void OnAiQuizClick(object sender, RoutedEventArgs e)
    {
        var context = $"学生的当前练习：父本 {FatherBox.Text} × 母本 {MotherBox.Text}；哈代-温伯格 p 输入为 {PBox.Text}。";
        await AskAiAsync("你是高中生物出题老师。请围绕孟德尔遗传与哈代-温伯格平衡，生成 3 道由易到难的题目（含答案与解析），用中文 Markdown 输出。",
            context);
    }

    private async Task AskAiAsync(string instruction, string content)
    {
        BusyRing.IsActive = true;
        try
        {
            AiOutput.Text = $"AI（{_ai.Describe()}）处理中…";
            AiOutput.Text = await _ai.AskAsync(instruction, content);
            StatusText.Text = "AI 输出完成。";
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "遗传学 AI 调用失败");
            AiOutput.Text = $"AI 失败：{ex.Message}";
        }
        finally
        {
            BusyRing.IsActive = false;
        }
    }
}
