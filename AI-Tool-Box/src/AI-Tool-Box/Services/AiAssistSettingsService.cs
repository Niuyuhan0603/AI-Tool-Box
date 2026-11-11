using System.Text.Json;
using AI_Tool_Box.Models;
using Serilog;

namespace AI_Tool_Box.Services;

/// <summary>右侧栏 AI 辅助设置服务契约（档案列表持久化；API-Key 由 IApiKeyService 以 assist:{Id} 名存 DPAPI）。</summary>
public interface IAiAssistSettingsService
{
    /// <summary>当前设置（含提供商档案）。</summary>
    AiAssistSettings Settings { get; }

    /// <summary>写入磁盘。</summary>
    void Save();
}

/// <summary>右侧栏 AI 辅助设置服务：持久化到 %LOCALAPPDATA%\AI-Tool-Box\ai_assist_settings.json。</summary>
public sealed class AiAssistSettingsService : IAiAssistSettingsService
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AI-Tool-Box", "ai_assist_settings.json");

    private readonly ILogger _logger;

    /// <summary>当前设置（构造时从磁盘加载，失败则用默认档案）。</summary>
    public AiAssistSettings Settings { get; private set; }

    /// <summary>初始化并加载设置。</summary>
    public AiAssistSettingsService(ILogger logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        Settings = Load();
        // 后台纠正本地免鉴权档案里的“伪模型名”（旧版本预填的 qwen3:8b 之类），避免各页面发出不存在的模型
        _ = HealLocalModelsAsync();
    }

    /// <summary>
    /// 从本机 Ollama /api/tags 读取实际已安装模型；档案里记录的模型不存在时改用第一个真实模型。
    /// 本机服务不可用（未启动 Ollama）则保持原值不动。
    /// </summary>
    private async Task HealLocalModelsAsync()
    {
        var changed = false;
        foreach (var profile in Settings.Profiles.Where(p => p.NoKeyRequired))
        {
            var models = await OllamaService.ListModelsAtAsync(profile.BaseUrl);
            if (models.Count == 0) continue;
            if (models.Contains(profile.DefaultModel ?? string.Empty)) continue;

            profile.DefaultModel = models[0];
            changed = true;
            _logger.Information("本地模型自动校正：{Profile} → {Model}（本机已安装 {Count} 个）",
                profile.DisplayName, models[0], models.Count);
        }
        if (changed) Save();
    }

    private AiAssistSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                var s = JsonSerializer.Deserialize<AiAssistSettings>(json);
                if (s is not null && s.Profiles.Count > 0)
                {
                    if (string.IsNullOrEmpty(s.SelectedProfileId) || s.Profiles.All(p => p.Id != s.SelectedProfileId))
                    {
                        s.SelectedProfileId = s.Profiles[0].Id;
                    }
                    _logger.Information("右侧栏AI辅助设置已加载：{Count} 个提供商档案", s.Profiles.Count);
                    return s;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "右侧栏AI辅助设置加载失败，使用默认配置");
        }

        var def = new AiAssistSettings { SelectedProfileId = "" };
        def.SelectedProfileId = def.Profiles[0].Id;
        return def;
    }

    /// <summary>写入磁盘（原子替换）。</summary>
    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(Settings, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, FilePath, overwrite: true);
            _logger.Information("右侧栏AI辅助设置已保存：{File}", FilePath);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "右侧栏AI辅助设置保存失败");
            throw;
        }
    }
}
