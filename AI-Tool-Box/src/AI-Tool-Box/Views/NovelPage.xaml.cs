using AI_Tool_Box.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Serilog;
using System.Text;
using System.Text.Json;
using Windows.Data.Pdf;
using Windows.Storage;
using Windows.Storage.Streams;

namespace AI_Tool_Box.Views;

/// <summary>
/// AI 小说创作：以工作区文件夹中的已有章节 + 用户填写的“前情提要”为上下文，
/// 调用已配置的 OpenAI 兼容 API 流式续写正文，并可保存为工作区内的新章节。
/// </summary>
public sealed partial class NovelPage : Page
{
    private sealed class NovelSettings
    {
        public string WorkspaceFolder { get; set; } = string.Empty;
        public string Premise { get; set; } = string.Empty;
        public string FileName { get; set; } = "第1章.md";
        public string Model { get; set; } = string.Empty;
        public int ContextBudget { get; set; } = 12000;
    }

    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AI-Tool-Box", "novel_settings.json");

    /// <summary>PDF 章节 OCR 结果缓存目录（按路径+修改时间作键，避免每次续写重复识别）。</summary>
    private static readonly string PdfCacheDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AI-Tool-Box", "novel_cache");

    private const int MaxPdfPages = 40;

    private readonly ILogger _logger;
    private readonly IApiAiService _api;
    private readonly IApiKeyService _keys;
    private readonly IAiAssistSettingsService _assist;
    private readonly ILiteParseOcrService _ocr;

    private readonly StringBuilder _generated = new();
    private readonly object _generatedLock = new();
    private readonly List<string> _chapterPaths = new();
    private readonly Dictionary<string, (long Ticks, string Text)> _chapterTextCache = new(StringComparer.OrdinalIgnoreCase);

    private CancellationTokenSource? _cts;
    private string _workspace = string.Empty;

    public NovelPage()
    {
        InitializeComponent();
        _logger = App.Services.GetService(typeof(ILogger)) as ILogger ?? Serilog.Log.Logger;
        _api = App.Services.GetRequiredService<IApiAiService>();
        _keys = App.Services.GetRequiredService<IApiKeyService>();
        _assist = App.Services.GetRequiredService<IAiAssistSettingsService>();
        _ocr = App.Services.GetRequiredService<ILiteParseOcrService>();

        LoadSettings();
        ApplySavedApiConfig();

        if (!string.IsNullOrWhiteSpace(_workspace) && Directory.Exists(_workspace))
        {
            WorkspaceBox.Text = _workspace;
            _ = RefreshChaptersAsync();
        }
        Unloaded += OnPageUnloaded;
    }

    // ===== 设置持久化 =====

    private void LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath, Encoding.UTF8);
                var s = JsonSerializer.Deserialize<NovelSettings>(json);
                if (s is not null)
                {
                    _workspace = s.WorkspaceFolder ?? string.Empty;
                    PremiseBox.Text = s.Premise ?? string.Empty;
                    FileNameBox.Text = string.IsNullOrWhiteSpace(s.FileName) ? "第1章.md" : s.FileName;
                    ModelBox.Text = s.Model ?? string.Empty;
                    BudgetBox.Text = s.ContextBudget > 0 ? s.ContextBudget.ToString() : "12000";
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "读取小说创作设置失败，使用默认值");
        }
    }

    private void SaveSettings()
    {
        try
        {
            var dir = Path.GetDirectoryName(SettingsPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
            var s = new NovelSettings
            {
                WorkspaceFolder = _workspace,
                Premise = PremiseBox.Text ?? string.Empty,
                FileName = FileNameBox.Text ?? "第1章.md",
                Model = ModelBox.Text ?? string.Empty,
                ContextBudget = ParseBudget()
            };
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(s, new JsonSerializerOptions { WriteIndented = true }),
                Encoding.UTF8);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "保存小说创作设置失败");
        }
    }

    private void OnPageUnloaded(object sender, RoutedEventArgs e)
    {
        try
        {
            _cts?.Cancel();
            SaveSettings();
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "离开小说创作页时清理失败");
        }
    }

    // ===== API 配置 =====

    /// <summary>读取设置页保存的 API 基地址 / Key / 模型（与 AI 问答页共用）。</summary>
    private void ApplySavedApiConfig()
    {
        var baseUrl = _keys.Load("api_baseurl");
        if (!string.IsNullOrWhiteSpace(baseUrl)) _api.BaseUrl = baseUrl;
        _api.ApiKey = _keys.Load("api");

        var model = ModelBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(model)) model = _keys.Load("api_model");
        if (string.IsNullOrWhiteSpace(model))
        {
            var profiles = _assist.Settings.Profiles;
            model = profiles.FirstOrDefault(p => p.Id == _assist.Settings.SelectedProfileId)?.DefaultModel
                    ?? profiles.FirstOrDefault()?.DefaultModel
                    ?? string.Empty;
        }
        ModelBox.Text = model ?? string.Empty;

        var hasKey = !string.IsNullOrWhiteSpace(_api.ApiKey);
        ApiInfoText.Text = hasKey
            ? $"当前 API：{_api.BaseUrl}　模型：{(string.IsNullOrWhiteSpace(ModelBox.Text) ? "未设置" : ModelBox.Text)}"
              + "（可在「设置 → AI API 配置」中修改，改完回到本页会重新读取）"
            : "尚未配置 API：请到「设置 → AI API 配置」选择厂商并填写 API-Key，再回到本页创作。";
    }

    // ===== 工作区 =====

    private async void OnPickWorkspaceClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new Windows.Storage.Pickers.FolderPicker();
            picker.FileTypeFilter.Add("*");
            picker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary;
            WinRT.Interop.InitializeWithWindow.Initialize(picker,
                WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow));
            var folder = await picker.PickSingleFolderAsync();
            if (folder is null) return;

            _workspace = folder.Path;
            WorkspaceBox.Text = _workspace;
            await RefreshChaptersAsync();
            SaveSettings();
            StatusText.Text = $"工作区已设置：{_workspace}";
            _logger.Information("小说创作工作区已设置：{Dir}", _workspace);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "选择小说工作区失败");
            StatusText.Text = $"选择工作区失败：{ex.Message}";
        }
    }

    private void OnOpenWorkspaceClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(_workspace) || !Directory.Exists(_workspace))
            {
                StatusText.Text = "请先选择工作区文件夹。";
                return;
            }
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{_workspace}\"",
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "打开小说工作区失败");
        }
    }

    private async void OnRefreshChaptersClick(object sender, RoutedEventArgs e) => await RefreshChaptersAsync();

    /// <summary>刷新章节列表并预先载入/识别各章节文本（PDF 走 OCR，结果会缓存）。</summary>
    private async Task RefreshChaptersAsync()
    {
        ChapterList.Items.Clear();
        _chapterPaths.Clear();
        if (string.IsNullOrWhiteSpace(_workspace) || !Directory.Exists(_workspace))
        {
            ContextInfoText.Text = "尚未选择工作区，AI 只能依据“前情提要”续写。";
            return;
        }

        foreach (var file in EnumerateChapterFiles(_workspace))
        {
            _chapterPaths.Add(file);
            ChapterList.Items.Add(Path.GetFileName(file));
        }

        if (_chapterPaths.Count == 0)
        {
            ContextInfoText.Text = "工作区中还没有 .md / .txt / .pdf 章节文件。";
            return;
        }

        BusyRing.IsActive = true;
        var total = 0;
        var failed = 0;
        try
        {
            foreach (var file in _chapterPaths)
            {
                var text = await GetChapterTextAsync(file);
                if (text.Length == 0) failed++;
                total += text.Length;
            }
        }
        finally
        {
            BusyRing.IsActive = false;
        }

        ContextInfoText.Text = $"已加载 {_chapterPaths.Count} 个章节文件，上下文约 {total} 字符（预算 {ParseBudget()}）"
                               + (failed > 0 ? $"；{failed} 个文件未能提取文字" : string.Empty) + "。";
        StatusText.Text = "章节已就绪。";
    }

    private static IEnumerable<string> EnumerateChapterFiles(string dir)
    {
        try
        {
            return Directory.GetFiles(dir, "*.*")
                .Where(f => f.EndsWith(".md", StringComparison.OrdinalIgnoreCase)
                            || f.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)
                            || f.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
                .OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception)
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>读取章节文本：md/txt 直接读；PDF 渲染为图片后 OCR（按路径+修改时间缓存）。</summary>
    private async Task<string> GetChapterTextAsync(string path)
    {
        var ext = Path.GetExtension(path);
        if (ext.Equals(".md", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".txt", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                return await File.ReadAllTextAsync(path, Encoding.UTF8);
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "读取章节文件失败：{File}", path);
                return string.Empty;
            }
        }
        if (!ext.Equals(".pdf", StringComparison.OrdinalIgnoreCase)) return string.Empty;

        long ticks = 0;
        try { ticks = File.GetLastWriteTimeUtc(path).Ticks; } catch { /* 忽略 */ }
        if (_chapterTextCache.TryGetValue(path, out var cached) && cached.Ticks == ticks)
        {
            return cached.Text;
        }

        var text = await ExtractPdfTextAsync(path, ticks);
        _chapterTextCache[path] = (ticks, text);
        return text;
    }

    /// <summary>PDF 章节：逐页渲染（1500px 宽）后 OCR，最多解析 MaxPdfPages 页，并写入本地缓存。</summary>
    private async Task<string> ExtractPdfTextAsync(string path, long ticks)
    {
        var cacheFile = Path.Combine(PdfCacheDir, PdfCacheKey(path, ticks) + ".txt");
        try
        {
            if (File.Exists(cacheFile)) return await File.ReadAllTextAsync(cacheFile, Encoding.UTF8);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "读取 PDF 章节缓存失败：{File}", cacheFile);
        }

        var lang = await ResolveOcrLanguageAsync();
        if (lang is null)
        {
            StatusText.Text = "LiteParse2.0 缺少 OCR 语言数据，PDF 章节无法提取文字（请检查「OCR 文字识别」页的引擎目录）。";
            return string.Empty;
        }

        var sb = new StringBuilder();
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(path);
            var pdf = await PdfDocument.LoadFromFileAsync(file);
            var pages = (int)Math.Min(pdf.PageCount, (uint)MaxPdfPages);
            for (var i = 0; i < pages; i++)
            {
                StatusText.Text = $"正在识别 PDF《{Path.GetFileName(path)}》第 {i + 1}/{pages} 页…";
                using var page = pdf.GetPage((uint)i);
                using var stream = new InMemoryRandomAccessStream();
                await page.RenderToStreamAsync(stream, new PdfPageRenderOptions { DestinationWidth = 1500 });
                var tmp = Path.Combine(Path.GetTempPath(), $"aitoolbox-novel-{Guid.NewGuid():N}.png");
                try
                {
                    await using (var fs = File.Create(tmp))
                    {
                        stream.Seek(0);
                        using var source = stream.AsStreamForRead();
                        await source.CopyToAsync(fs);
                    }
                    var text = await _ocr.RecognizeAsync(tmp, lang.Code);
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        sb.AppendLine(text.Trim());
                        sb.AppendLine();
                    }
                }
                finally
                {
                    try { File.Delete(tmp); } catch { /* 临时文件清理失败不影响解析 */ }
                }
            }
            if (pdf.PageCount > MaxPdfPages)
            {
                sb.AppendLine($"（该 PDF 共 {pdf.PageCount} 页，仅解析前 {MaxPdfPages} 页）");
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "解析 PDF 章节失败：{File}", path);
            return string.Empty;
        }

        var result = sb.ToString();
        try
        {
            Directory.CreateDirectory(PdfCacheDir);
            await File.WriteAllTextAsync(cacheFile, result, Encoding.UTF8);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "写入 PDF 章节缓存失败：{File}", cacheFile);
        }
        return result;
    }

    private static string PdfCacheKey(string path, long ticks)
    {
        var hash = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(path.ToLowerInvariant()));
        return Convert.ToHexString(hash)[..16] + "_" + ticks;
    }

    private async Task<OcrLanguageInfo?> ResolveOcrLanguageAsync()
    {
        var languages = await _ocr.GetLanguagesAsync();
        var installed = languages.Where(l => l.IsInstalled).ToList();
        return installed.FirstOrDefault(l => l.Code.StartsWith("chi_sim", StringComparison.OrdinalIgnoreCase))
               ?? installed.FirstOrDefault(l => l.Code.StartsWith("eng", StringComparison.OrdinalIgnoreCase))
               ?? installed.FirstOrDefault();
    }

    /// <summary>确保工作区内所有章节（含 PDF）的文字都已载入缓存。</summary>
    private async Task EnsureChapterTextsAsync()
    {
        if (string.IsNullOrWhiteSpace(_workspace) || !Directory.Exists(_workspace)) return;
        foreach (var file in EnumerateChapterFiles(_workspace))
        {
            await GetChapterTextAsync(file);
        }
    }

    private async void OnChapterSelected(object sender, SelectionChangedEventArgs e)
    {
        if (ChapterList.SelectedIndex < 0 || ChapterList.SelectedIndex >= _chapterPaths.Count) return;
        try
        {
            var path = _chapterPaths[ChapterList.SelectedIndex];
            BusyRing.IsActive = true;
            var text = await GetChapterTextAsync(path);
            lock (_generatedLock)
            {
                _generated.Clear();
                _generated.Append(text);
            }
            NovelOutput.Inlines.Clear();
            NovelOutput.Inlines.Add(new Run { Text = text });
            FileNameBox.Text = Path.GetFileName(path);
            OutputScroll.ChangeView(null, 0, null, true);
            StatusText.Text = text.Length == 0
                ? $"已载入章节：{Path.GetFileName(path)}（未能提取到文字）"
                : $"已载入章节：{Path.GetFileName(path)}（{text.Length} 字符）";
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "读取章节失败");
            StatusText.Text = $"读取章节失败：{ex.Message}";
        }
        finally
        {
            BusyRing.IsActive = false;
        }
    }

    // ===== 生成 =====

    private async void OnGenerateClick(object sender, RoutedEventArgs e)
    {
        if (_cts is not null) return;

        if (string.IsNullOrWhiteSpace(_api.ApiKey))
        {
            StatusText.Text = "未配置 API-Key：请到「设置 → AI API 配置」填写后再试（本页会自动读取）。";
            return;
        }
        var model = ModelBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(model))
        {
            StatusText.Text = "请填写模型（如 deepseek-chat / gpt-4o-mini）。";
            return;
        }

        // PDF 章节首次使用需要 OCR，先确保所有章节文字已就绪（有缓存时几乎无耗时）
        SetBusy(true);
        var cts = new CancellationTokenSource();
        _cts = cts;
        StatusText.Text = "正在准备前情上下文（PDF 章节首次需要识别）…";
        try
        {
            await EnsureChapterTextsAsync();
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "准备前情上下文失败");
        }

        var prompt = BuildPrompt();
        NoveloClearIfNewChapter();
        StatusText.Text = "正在流式生成…（可随时点击“停止”）";

        try
        {
            await _api.GenerateStreamAsync(model, prompt, chunk =>
            {
                lock (_generatedLock) _generated.Append(chunk);
                DispatcherQueue.TryEnqueue(() =>
                {
                    NovelOutput.Inlines.Add(new Run { Text = chunk });
                    OutputScroll.UpdateLayout();
                    OutputScroll.ChangeView(null, OutputScroll.ScrollableHeight, null, true);
                });
            }, cts.Token);

            StatusText.Text = "生成完成。可点击“保存到工作区”写入章节文件。";
            _logger.Information("小说续写完成：{Model} {Chars} 字符", model, _generated.Length);
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "已停止生成（已产生的内容仍可保存）。";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"生成失败：{ex.Message}";
            _logger.Error(ex, "小说续写失败");
        }
        finally
        {
            SetBusy(false);
            _cts?.Dispose();
            _cts = null;
            SaveSettings();
        }
    }

    /// <summary>新开一章：清空正文区与缓冲（已有内容若未保存会被覆盖，状态栏会提示）。</summary>
    private void NoveloClearIfNewChapter()
    {
        var current = FileNameBox.Text?.Trim() ?? string.Empty;
        string? existing = null;
        if (!string.IsNullOrWhiteSpace(_workspace) && Directory.Exists(_workspace) && !string.IsNullOrWhiteSpace(current)
            && (current.EndsWith(".md", StringComparison.OrdinalIgnoreCase) || current.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)))
        {
            var candidate = Path.Combine(_workspace, current);
            if (File.Exists(candidate)) existing = File.ReadAllText(candidate, Encoding.UTF8);
        }

        if (existing is not null)
        {
            // 已有同名章节：在其基础上继续续写
            lock (_generatedLock)
            {
                _generated.Clear();
                _generated.Append(existing);
            }
            NovelOutput.Inlines.Clear();
            NovelOutput.Inlines.Add(new Run { Text = existing });
        }
        else if (NovelOutput.Inlines.Count > 0)
        {
            // 当前正文与目标文件不同：作为新章节处理，清空显示
            lock (_generatedLock) _generated.Clear();
            NovelOutput.Inlines.Clear();
        }
    }

    private void OnStopClick(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        StatusText.Text = "正在停止…";
    }

    private void SetBusy(bool busy)
    {
        GenerateButton.IsEnabled = !busy;
        StopButton.IsEnabled = busy;
        BusyRing.IsActive = busy;
    }

    // ===== 上下文与提示词 =====

    private int ParseBudget()
    {
        return int.TryParse(BudgetBox.Text?.Trim(), out var v)
            ? Math.Clamp(v, 1000, 100000)
            : 12000;
    }

    /// <summary>按文件名顺序拼接待续写正文，超预算时保留最近部分。</summary>
    private string BuildWorkspaceContext(int budget)
    {
        if (string.IsNullOrWhiteSpace(_workspace) || !Directory.Exists(_workspace)) return string.Empty;
        var all = new StringBuilder();
        foreach (var file in EnumerateChapterFiles(_workspace))
        {
            try
            {
                string text;
                if (Path.GetExtension(file).Equals(".pdf", StringComparison.OrdinalIgnoreCase))
                {
                    // PDF 章节使用已识别（或缓存）的文本，未就绪则跳过
                    if (!_chapterTextCache.TryGetValue(file, out var cached)) continue;
                    text = cached.Text;
                }
                else
                {
                    text = File.ReadAllText(file, Encoding.UTF8);
                }
                if (string.IsNullOrWhiteSpace(text)) continue;
                all.AppendLine($"### {Path.GetFileName(file)}");
                all.AppendLine(text.Trim());
                all.AppendLine();
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "读取章节文件失败：{File}", file);
            }
        }

        var joined = all.ToString();
        if (joined.Length == 0) return string.Empty;
        return joined.Length <= budget
            ? joined
            : "（前文过长，仅保留最近部分）\n" + joined[^budget..];
    }

    private string BuildPrompt()
    {
        var premise = PremiseBox.Text?.Trim() ?? string.Empty;
        var instruction = InstructionBox.Text?.Trim() ?? string.Empty;
        var context = BuildWorkspaceContext(ParseBudget());

        var sb = new StringBuilder();
        sb.AppendLine("你是一名中文小说续写助手。请严格遵循下面的设定与前情，保持人物性格、世界观、语气与伏笔一致，"
                      + "不要跑题，不要复述前文，不要输出解释、标题或 Markdown 标记。");
        if (premise.Length > 0)
        {
            sb.AppendLine();
            sb.AppendLine("【设定 / 前情提要】");
            sb.AppendLine(premise);
        }
        if (context.Length > 0)
        {
            sb.AppendLine();
            sb.AppendLine("【工作区已有正文（按章节顺序，末段为最新）】");
            sb.AppendLine(context);
        }
        sb.AppendLine();
        sb.AppendLine("【本次续写要求】");
        sb.AppendLine(instruction.Length > 0 ? instruction : "自然地续写下一段情节，推进故事，不要草率收尾。");
        sb.AppendLine();
        sb.AppendLine("请直接输出续写正文（中文，可分段）。");
        return sb.ToString();
    }

    // ===== 保存 =====

    private async void OnSaveChapterClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(_workspace) || !Directory.Exists(_workspace))
            {
                StatusText.Text = "请先选择工作区文件夹，再保存章节。";
                return;
            }
            string text;
            lock (_generatedLock) text = _generated.ToString();
            if (string.IsNullOrWhiteSpace(text))
            {
                StatusText.Text = "正文为空，没有可保存的内容。";
                return;
            }

            var name = FileNameBox.Text?.Trim();
            if (string.IsNullOrWhiteSpace(name)) name = "第1章.md";
            foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            if (!name.Contains('.')) name += ".md";
            // PDF 章节是只读来源，续写内容另存为 markdown，避免覆盖原文件
            if (name.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
            {
                name = Path.ChangeExtension(name, ".md");
            }

            var path = Path.Combine(_workspace, name);
            File.WriteAllText(path, text, Encoding.UTF8);
            FileNameBox.Text = name;
            _chapterTextCache.Remove(path);
            await RefreshChaptersAsync();
            StatusText.Text = $"已保存到工作区：{path}（{text.Length} 字符）";
            _logger.Information("小说章节已保存：{Path} {Chars} 字符", path, text.Length);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "保存小说章节失败");
            StatusText.Text = $"保存失败：{ex.Message}";
        }
    }
}
