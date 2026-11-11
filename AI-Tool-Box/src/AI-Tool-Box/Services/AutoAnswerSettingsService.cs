using System.Text.Json;
using AI_Tool_Box.Models;
using Serilog;

namespace AI_Tool_Box.Services;

/// <summary>自动答题设置服务契约。</summary>
public interface IAutoAnswerSettingsService
{
    /// <summary>当前设置（含站点档案）。</summary>
    AutoAnswerSettings Settings { get; }

    /// <summary>当前选中的站点档案（不存在时自动创建并选中）。</summary>
    AutoAnswerSiteProfile SelectedSite { get; }

    /// <summary>写入磁盘。</summary>
    void Save();
}

/// <summary>自动答题设置服务：持久化到 %LOCALAPPDATA%\AI-Tool-Box\auto_answer_settings.json。</summary>
public sealed class AutoAnswerSettingsService : IAutoAnswerSettingsService
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AI-Tool-Box", "auto_answer_settings.json");

    private readonly ILogger _logger;

    /// <summary>当前设置（构造时从磁盘加载，失败或为空则使用默认站点档案）。</summary>
    public AutoAnswerSettings Settings { get; private set; }

    /// <summary>初始化并加载设置。</summary>
    public AutoAnswerSettingsService(ILogger logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        Settings = Load();
    }

    /// <inheritdoc/>
    public AutoAnswerSiteProfile SelectedSite
    {
        get
        {
            var s = Settings;
            if (s.Sites.Count == 0)
            {
                s.Sites.Add(new AutoAnswerSiteProfile());
            }
            var site = s.Sites.FirstOrDefault(x => x.Id == s.SelectedSiteId) ?? s.Sites[0];
            s.SelectedSiteId = site.Id;
            return site;
        }
    }

    private AutoAnswerSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                var s = JsonSerializer.Deserialize<AutoAnswerSettings>(json);
                if (s is not null && s.Sites.Count > 0)
                {
                    if (string.IsNullOrEmpty(s.SelectedSiteId) || s.Sites.All(x => x.Id != s.SelectedSiteId))
                    {
                        s.SelectedSiteId = s.Sites[0].Id;
                    }
                    _logger.Information("自动答题设置已加载：{Count} 个站点档案", s.Sites.Count);
                    return s;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "自动答题设置加载失败，使用默认配置");
        }

        var def = new AutoAnswerSettings();
        def.Sites.Add(new AutoAnswerSiteProfile());
        def.SelectedSiteId = def.Sites[0].Id;
        return def;
    }

    /// <inheritdoc/>
    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(Settings, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, FilePath, overwrite: true);
            _logger.Information("自动答题设置已保存：{File}", FilePath);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "自动答题设置保存失败");
        }
    }
}
