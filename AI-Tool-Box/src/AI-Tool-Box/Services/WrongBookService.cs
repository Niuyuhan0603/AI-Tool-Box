using Serilog;
using System.IO;
using System.Text.Json;

namespace AI_Tool_Box.Services;

/// <summary>错题本条目。</summary>
public sealed class WrongQuestion
{
    /// <summary>唯一 ID。</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>科目（数学/物理/化学…）。</summary>
    public string Subject { get; set; } = "未分类";

    /// <summary>标签（逗号分隔）。</summary>
    public string Tags { get; set; } = string.Empty;

    /// <summary>题干。</summary>
    public string Question { get; set; } = string.Empty;

    /// <summary>正确答案。</summary>
    public string Answer { get; set; } = string.Empty;

    /// <summary>解析 / 错因。</summary>
    public string Analysis { get; set; } = string.Empty;

    /// <summary>创建时间。</summary>
    public DateTime CreatedAt { get; set; } = DateTime.Now;

    /// <summary>复习次数。</summary>
    public int ReviewCount { get; set; }

    /// <summary>最近复习时间。</summary>
    public DateTime? LastReviewedAt { get; set; }

    /// <summary>是否已掌握。</summary>
    public bool Mastered { get; set; }

    /// <summary>可视化内容类型：none=纯文本；pdf=PDF 裁剪片段；html=DOCX/网页选中内容（HTML）；image=图片（可裁剪）。</summary>
    public string Kind { get; set; } = "none";

    /// <summary>源文件路径（pdf/image 为原文件；html 为来源 docx 路径，仅作记录）。</summary>
    public string SourcePath { get; set; } = string.Empty;

    /// <summary>源文件页码（pdf 类型，1 起）。</summary>
    public int SourcePage { get; set; }

    /// <summary>PDF 裁剪框 [x1,y1,x2,y2]（PDF 坐标点，左下原点）；null/空 表示整页。</summary>
    public double[]? CropBox { get; set; }

    /// <summary>html 类型的选中内容 HTML（DOCX 选中片段）。</summary>
    public string HtmlContent { get; set; } = string.Empty;

    /// <summary>题目缩略图路径（裁剪区域的 PNG，用于在「题目」栏以图片展示被选中的题）。</summary>
    public string ThumbnailPath { get; set; } = string.Empty;

    /// <summary>答案图片路径（可选，用于在「答案」栏以图片展示）。</summary>
    public string AnswerImagePath { get; set; } = string.Empty;

    /// <summary>是否含可视化题目内容（非纯文本）。</summary>
    public bool HasVisual => !string.Equals(Kind, "none", StringComparison.OrdinalIgnoreCase)
                             && !string.IsNullOrEmpty(Kind);

    /// <summary>列表显示文本。</summary>
    public string Display => $"[{Subject}] {(Question.Length > 60 ? Question[..60] + "…" : Question)}"
                             + (HasVisual ? "  🖼" : string.Empty)
                             + (Mastered ? "  ✅" : string.Empty);
}

/// <summary>错题本本地存储（%LOCALAPPDATA%\AI-Tool-Box\wrongbook.json）。</summary>
public interface IWrongBookService
{
    /// <summary>当前全部条目（按创建时间倒序）。</summary>
    IReadOnlyList<WrongQuestion> Items { get; }

    /// <summary>新增或更新条目。</summary>
    void Upsert(WrongQuestion item);

    /// <summary>删除条目。</summary>
    void Delete(string id);

    /// <summary>写入磁盘。</summary>
    void Save();
}

/// <summary>错题本实现。</summary>
public sealed class WrongBookService : IWrongBookService
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AI-Tool-Box", "wrongbook.json");

    private readonly ILogger _logger;
    private readonly List<WrongQuestion> _items;

    /// <summary>构造并加载本地文件。</summary>
    public WrongBookService(ILogger logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _items = Load();
    }

    /// <inheritdoc/>
    public IReadOnlyList<WrongQuestion> Items => _items;

    /// <inheritdoc/>
    public void Upsert(WrongQuestion item)
    {
        var index = _items.FindIndex(x => x.Id == item.Id);
        if (index >= 0) _items[index] = item;
        else _items.Insert(0, item);
        Save();
    }

    /// <inheritdoc/>
    public void Delete(string id)
    {
        _items.RemoveAll(x => x.Id == id);
        Save();
    }

    /// <inheritdoc/>
    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(_items, new JsonSerializerOptions { WriteIndented = true }));
            _logger.Information("错题本已保存：{Count} 条", _items.Count);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "保存错题本失败");
        }
    }

    private List<WrongQuestion> Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new List<WrongQuestion>();
            var json = File.ReadAllText(FilePath);
            return JsonSerializer.Deserialize<List<WrongQuestion>>(json) ?? new List<WrongQuestion>();
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "加载错题本失败，使用空列表");
            return new List<WrongQuestion>();
        }
    }
}
