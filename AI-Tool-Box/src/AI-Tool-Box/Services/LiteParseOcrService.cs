using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using Serilog;
using Windows.Graphics.Imaging;
using Windows.Storage;

namespace AI_Tool_Box.Services;

/// <summary>OCR 语言目录项（LiteParse2.0 / lit.exe 引擎）。</summary>
public sealed record OcrLanguageInfo(
    /// <summary>Tesseract 兼容语言代码（traineddata 文件名，如 chi_sim）。</summary>
    string Code,
    /// <summary>用户友好显示名。</summary>
    string DisplayName,
    /// <summary>分类："推荐 · 联合国工作语言" / "推荐 · 常用外语"。</summary>
    string Category,
    /// <summary>该语言 traineddata 是否存在于引擎目录。</summary>
    bool IsInstalled);

/// <summary>本地 OCR 服务接口：基于 LiteParse2.0 引擎（lit.exe），语言数据随引擎目录离线分发。</summary>
public interface ILiteParseOcrService
{
    /// <summary>LiteParse2.0 引擎目录（含 lit.exe 与 *.traineddata）。</summary>
    string EngineDirectory { get; }

    /// <summary>扫描引擎目录并返回推荐语言目录（含本地存在标记）。</summary>
    Task<IReadOnlyList<OcrLanguageInfo>> GetLanguagesAsync();

    /// <summary>对图片或 PDF 执行识别 / 文本提取，返回文本。</summary>
    Task<string> RecognizeAsync(string sourcePath, string languageCode, CancellationToken ct = default);

    /// <summary>带格式识别：按字符框坐标还原行内空格与行首缩进（适合清晰的文字截图 / 代码截图）。
    /// indentWidth&gt;0 表示代码识别模式，缩进按该语言的最小缩进单元对齐；0 表示普通文本模式（按原样还原空格）。</summary>
    Task<string> RecognizeFormattedAsync(string sourcePath, string languageCode, int indentWidth, bool useTabs, CancellationToken ct = default);
}

/// <summary>本地 OCR 服务实现：调用随应用分发的 LiteParse2.0（lit.exe）命令行解析图片 / PDF，
/// 语言数据（traineddata）与引擎同目录，完全离线。</summary>
public sealed class LiteParseOcrService : ILiteParseOcrService
{
    /// <summary>推荐语言静态目录：联合国 6 种工作语言 + 常用外语，共 11 种（UI 仅显示此清单）。</summary>
    private static readonly (string Code, string DisplayName, string Category)[] Catalog = new[]
    {
        // 联合国 6 种工作语言（中文含简繁）
        ("chi_sim", "中文简体 (chi_sim)", "推荐 · 联合国工作语言"),
        ("chi_tra", "中文繁體 (chi_tra)", "推荐 · 联合国工作语言"),
        ("eng",     "English (eng)",      "推荐 · 联合国工作语言"),
        ("fra",     "Français (fra)",     "推荐 · 联合国工作语言"),
        ("rus",     "Русский (rus)",      "推荐 · 联合国工作语言"),
        ("spa",     "Español (spa)",      "推荐 · 联合国工作语言"),
        ("ara",     "العربية (ara)",      "推荐 · 联合国工作语言"),
        // 常用外语（日、韩、德语、葡萄牙语）
        ("deu", "Deutsch (deu)",   "推荐 · 常用外语"),
        ("jpn", "日本語 (jpn)",     "推荐 · 常用外语"),
        ("kor", "한국어 (kor)",    "推荐 · 常用外语"),
        ("por", "Português (por)", "推荐 · 常用外语"),
    };

    private readonly ILogger _logger;
    private readonly IAppSettingsService _settings;

    /// <summary>初始化 LiteParseOcrService。</summary>
    /// <param name="logger">Serilog 日志。</param>
    /// <param name="settings">应用设置（读取 OCR 并发数，映射 lit.exe --num-workers）。</param>
    /// <param name="engineDirOverride">引擎目录覆盖（测试/便携场景；null 时自动定位）。</param>
    public LiteParseOcrService(ILogger logger, IAppSettingsService settings, string? engineDirOverride = null)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _engineDirOverride = string.IsNullOrWhiteSpace(engineDirOverride) ? null : engineDirOverride;
    }

    private readonly string? _engineDirOverride;

    /// <summary>引擎目录定位：① 应用自带 Assets\LiteParse（发布/构建时从仓库 LiteParse2.0 复制）
    /// → ② 应用根目录 LiteParse2.0（便携放置）→ ③ 应用上级目录 LiteParse2.0（开发运行兜底）。</summary>
    public string EngineDirectory
    {
        get
        {
            if (_engineDirOverride is not null) return _engineDirOverride;
            var candidates = new[]
            {
                Path.Combine(AppContext.BaseDirectory, "Assets", "LiteParse"),
                Path.Combine(AppContext.BaseDirectory, "LiteParse2.0"),
                Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "LiteParse2.0")),
            };
            foreach (var dir in candidates)
            {
                try
                {
                    if (File.Exists(Path.Combine(dir, "lit.exe"))) return dir;
                }
                catch
                {
                    // 路径非法/无权限 → 尝试下一个候选
                }
            }
            return candidates[0];
        }
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<OcrLanguageInfo>> GetLanguagesAsync()
    {
        var installedCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var f in Directory.EnumerateFiles(EngineDirectory, "*.traineddata"))
            {
                installedCodes.Add(Path.GetFileNameWithoutExtension(f));
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "扫描 LiteParse2.0 语言数据目录失败：{Dir}", EngineDirectory);
        }

        // 仅返回 Catalog 中的推荐语言（UI 固定显示 11 种常用语言，其余本地文件不列出）
        IReadOnlyList<OcrLanguageInfo> list = Catalog
            .Select(c => new OcrLanguageInfo(c.Code, c.DisplayName, c.Category, installedCodes.Contains(c.Code)))
            .ToList();

        return Task.FromResult(list);
    }

    /// <inheritdoc/>
    public async Task<string> RecognizeAsync(string sourcePath, string languageCode, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var raw = await RunLitAsync(sourcePath, languageCode, "text", ct).ConfigureAwait(false);

        // PDF 保留 lit 输出的 “--- Page N ---” 页标记（多页结果便于定位）；图片则移除页标记噪声
        var keepPageMarkers = Path.GetExtension(sourcePath).Equals(".pdf", StringComparison.OrdinalIgnoreCase);
        return NormalizeText(raw, keepPageMarkers);
    }

    /// <inheritdoc/>
    public async Task<string> RecognizeFormattedAsync(string sourcePath, string languageCode, int indentWidth, bool useTabs, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var raw = await RunLitAsync(sourcePath, languageCode, "json", ct).ConfigureAwait(false);

        var pages = ParseLayoutJson(raw);
        if (pages.Count == 0)
        {
            // JSON 不可解析：退回纯文本识别，避免功能不可用
            _logger.Warning("LiteParse2.0 JSON 结果为空，回退纯文本识别");
            return await RecognizeAsync(sourcePath, languageCode, ct).ConfigureAwait(false);
        }

        _logger.Information(
            "带格式识别：页 {Pages}，文本块 {Items}，缩进单元 {Unit}",
            pages.Count, pages.Sum(p => p.Items.Count), indentWidth);

        return FormatPages(pages, indentWidth, useTabs);
    }

    /// <summary>启动 lit.exe 并返回 stdout（text 或 json），失败时抛出带 stderr 摘要的异常。</summary>
    private async Task<string> RunLitAsync(string sourcePath, string languageCode, string format, CancellationToken ct)
    {
        var engineDir = EngineDirectory;
        var litPath = Path.Combine(engineDir, "lit.exe");
        if (!File.Exists(litPath))
        {
            throw new FileNotFoundException("LiteParse2.0 引擎未找到：lit.exe", litPath);
        }
        var traineddata = Path.Combine(engineDir, $"{languageCode}.traineddata");
        if (!File.Exists(traineddata))
        {
            throw new FileNotFoundException($"语言数据未准备：{languageCode}.traineddata", traineddata);
        }

        string? upscaledPath = null;
        try
        {
            // 小字号截图直接送 Tesseract 容易漏字：先做 2× 最近邻放大（坐标等比放大，不影响排版还原）
            if (IsImageFile(sourcePath))
            {
                upscaledPath = await TryCreateUpscaledImageAsync(sourcePath, ct).ConfigureAwait(false);
            }
            var inputPath = upscaledPath ?? sourcePath;

            var workers = Math.Max(1, _settings.OcrThreadCount);
            var psi = new ProcessStartInfo
            {
                FileName = litPath,
                WorkingDirectory = engineDir,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            // 双保险：同时设置环境变量，兼容不读取 --tessdata-path 的内部实现
            psi.Environment["TESSDATA_PREFIX"] = engineDir;
            psi.ArgumentList.Add("parse");
            psi.ArgumentList.Add(inputPath);
            psi.ArgumentList.Add("--format");
            psi.ArgumentList.Add(format);
            psi.ArgumentList.Add("--ocr-language");
            psi.ArgumentList.Add(languageCode);
            psi.ArgumentList.Add("--tessdata-path");
            psi.ArgumentList.Add(engineDir);
            psi.ArgumentList.Add("--num-workers");
            psi.ArgumentList.Add(workers.ToString());
            psi.ArgumentList.Add("-q");

            _logger.Information(
                "LiteParse2.0 识别开始：{Source} @ {Lang}（{Format}）{Scaled}，并发 {Workers}",
                sourcePath, languageCode, format, upscaledPath is null ? string.Empty : "（2× 放大预处理）", workers);

            using var process = new Process { StartInfo = psi };
            try
            {
                process.Start();
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"无法启动 LiteParse2.0（lit.exe）：{ex.Message}", ex);
            }

            using var reg = ct.Register(() =>
            {
                try
                {
                    if (!process.HasExited) process.Kill(entireProcessTree: true);
                }
                catch
                {
                    // 进程已退出/已释放：忽略
                }
            });

            // 并发读取 stdout/stderr，避免管道缓冲区塞满导致子进程阻塞
            var outTask = process.StandardOutput.ReadToEndAsync(ct);
            var errTask = process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct).ConfigureAwait(false);

            var stdout = await outTask.ConfigureAwait(false);
            var err = (await errTask.ConfigureAwait(false)).Trim();
            if (process.ExitCode != 0)
            {
                var snippet = err.Length > 300 ? err[..300] : err;
                throw new InvalidOperationException($"LiteParse2.0 识别失败（退出码 {process.ExitCode}）：{snippet}");
            }

            _logger.Information("LiteParse2.0 识别完成：{Lang}（{Format}），输出 {Len} 字符", languageCode, format, stdout.Length);
            return stdout;
        }
        finally
        {
            if (upscaledPath is not null)
            {
                try { File.Delete(upscaledPath); }
                catch
                {
                    // 临时文件删除失败：忽略（下次启动可清理）
                }
            }
        }
    }

    private static readonly string[] ImageExtensions = { ".png", ".jpg", ".jpeg", ".bmp", ".tif", ".tiff", ".webp" };

    private static bool IsImageFile(string path)
        => ImageExtensions.Contains(Path.GetExtension(path).ToLowerInvariant());

    /// <summary>生成 2× 最近邻放大的临时 PNG（提升小字号截图识别率）；失败或尺寸过大时返回 null 用原图。</summary>
    private async Task<string?> TryCreateUpscaledImageAsync(string sourcePath, CancellationToken ct)
    {
        const int maxSide = 4000; // 放大后超过该边长 lit 会判为空页，直接跳过放大
        string? tempPath = null;
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(sourcePath);
            using var input = await file.OpenReadAsync();
            var decoder = await BitmapDecoder.CreateAsync(input);

            var width = decoder.PixelWidth;
            var height = decoder.PixelHeight;
            if (width == 0 || height == 0 || width * 2 > maxSide || height * 2 > maxSide) return null;

            var transform = new BitmapTransform
            {
                ScaledWidth = width * 2,
                ScaledHeight = height * 2,
                InterpolationMode = BitmapInterpolationMode.NearestNeighbor,
            };
            using var bitmap = await decoder.GetSoftwareBitmapAsync(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Premultiplied,
                transform,
                ExifOrientationMode.RespectExifOrientation,
                ColorManagementMode.DoNotColorManage);

            var tempDir = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar);
            var folder = await StorageFolder.GetFolderFromPathAsync(tempDir);
            var tempFile = await folder.CreateFileAsync($"atb_ocr_{Guid.NewGuid():N}.png", CreationCollisionOption.ReplaceExisting);
            using (var output = await tempFile.OpenAsync(FileAccessMode.ReadWrite))
            {
                var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, output);
                encoder.SetSoftwareBitmap(bitmap);
                await encoder.FlushAsync();
            }

            ct.ThrowIfCancellationRequested();
            tempPath = tempFile.Path;
            _logger.Information("已生成 2× 放大临时图片用于 OCR：{Path}", tempPath);
            return tempPath;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            if (tempPath is not null)
            {
                try { File.Delete(tempPath); } catch { }
            }
            _logger.Warning(ex, "生成放大预处理图片失败，改用原图识别：{Path}", sourcePath);
            return null;
        }
    }

    // ==================== 带格式识别：按字符框坐标还原排版 ====================

    private sealed record LayoutItem(string Text, double X, double Y, double Width, double Height);
    private sealed record LayoutPage(int PageNumber, IReadOnlyList<LayoutItem> Items);

    /// <summary>解析 lit JSON 的 pages[].text_items[]（含每个文本块的 x/y/width/height）。</summary>
    private static IReadOnlyList<LayoutPage> ParseLayoutJson(string json)
    {
        var pages = new List<LayoutPage>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("pages", out var pagesEl)) return pages;

            var index = 0;
            foreach (var p in pagesEl.EnumerateArray())
            {
                index++;
                var pageNo = p.TryGetProperty("page", out var pn) && pn.TryGetInt32(out var n) ? n : index;
                var items = new List<LayoutItem>();
                if (p.TryGetProperty("text_items", out var arr))
                {
                    foreach (var it in arr.EnumerateArray())
                    {
                        var text = it.TryGetProperty("text", out var t) ? t.GetString() : null;
                        if (string.IsNullOrWhiteSpace(text)) continue;
                        items.Add(new LayoutItem(
                            text!.Trim(),
                            ReadDouble(it, "x"), ReadDouble(it, "y"),
                            ReadDouble(it, "width"), ReadDouble(it, "height")));
                    }
                }
                pages.Add(new LayoutPage(pageNo, items));
            }
        }
        catch (JsonException)
        {
            // 结构异常 → 返回空列表，由调用方回退纯文本识别
        }
        return pages;
    }

    private static double ReadDouble(JsonElement element, string name)
        => element.TryGetProperty(name, out var p) && p.TryGetDouble(out var v) ? v : 0d;

    private static string FormatPages(IReadOnlyList<LayoutPage> pages, int indentWidth, bool useTabs)
    {
        var sb = new StringBuilder();
        foreach (var page in pages)
        {
            if (pages.Count > 1)
            {
                if (sb.Length > 0) sb.Append('\n');
                sb.Append("--- Page ").Append(page.PageNumber).AppendLine(" ---");
            }
            sb.AppendLine(RebuildPage(page.Items, indentWidth, useTabs));
        }
        return sb.ToString().Trim();
    }

    /// <summary>还原一页：行首缩进按“缩进单元”对齐，行内按字符间距反推空格，行距差补空行。</summary>
    private static string RebuildPage(IReadOnlyList<LayoutItem> items, int indentWidth, bool useTabs)
    {
        if (items.Count == 0) return string.Empty;

        var rows = GroupRows(items, out var rowYs);
        var pitch = EstimateCharPitch(items);
        // 左边界取“各行首个字符”的最小 x，避免行内 OCR 碎片把基准拉偏
        var originX = rows.Min(r => r.Min(i => i.X));
        var lineGap = EstimateLineGap(rowYs);

        var sb = new StringBuilder();
        for (var r = 0; r < rows.Count; r++)
        {
            if (r > 0 && lineGap > 0)
            {
                var blanks = Math.Min(4, Math.Max(0, (int)Math.Round((rowYs[r] - rowYs[r - 1]) / lineGap) - 1));
                for (var b = 0; b < blanks; b++) sb.Append('\n');
            }
            sb.AppendLine(BuildLine(rows[r], originX, pitch, indentWidth, useTabs));
        }
        return sb.ToString().TrimEnd('\r', '\n');
    }

    /// <summary>按 y 坐标聚类成行（容差取中位字高 0.6 倍），并按从上到下排序。</summary>
    private static List<List<LayoutItem>> GroupRows(IReadOnlyList<LayoutItem> items, out List<double> rowYs)
    {
        var heights = items.Select(i => i.Height).Where(h => h > 0).OrderBy(h => h).ToList();
        var medianHeight = heights.Count > 0 ? heights[heights.Count / 2] : 10d;
        var tolerance = Math.Max(2d, medianHeight * 0.6);

        var rows = new List<List<LayoutItem>>();
        rowYs = new List<double>();
        foreach (var item in items.OrderBy(i => i.Y).ThenBy(i => i.X))
        {
            if (rows.Count > 0 && Math.Abs(item.Y - rowYs[^1]) <= tolerance)
            {
                rows[^1].Add(item);
            }
            else
            {
                rows.Add(new List<LayoutItem> { item });
                rowYs.Add(item.Y);
            }
        }
        return rows;
    }

    /// <summary>行距中位数（用于判断空行数量）。</summary>
    private static double EstimateLineGap(IReadOnlyList<double> rowYs)
    {
        if (rowYs.Count < 2) return 0d;
        var gaps = new List<double>();
        for (var i = 1; i < rowYs.Count; i++)
        {
            var gap = rowYs[i] - rowYs[i - 1];
            if (gap > 1) gaps.Add(gap);
        }
        if (gaps.Count == 0) return 0d;
        gaps.Sort();
        return gaps[gaps.Count / 2];
    }

    /// <summary>字符宽度估计：优先用 ASCII 文本块的“宽度 / 字符数”中位数（避免中文方块字把栅格拉大）；
    /// 无样本时退化到全部文本块，再退化为字高 0.7 倍。</summary>
    private static double EstimateCharPitch(IReadOnlyList<LayoutItem> items)
    {
        var asciiRatios = items
            .Where(i => i.Width > 0 && i.Text.Length >= 3 && IsAsciiText(i.Text))
            .Select(i => i.Width / i.Text.Length)
            .Where(r => r is > 2 and < 40)
            .OrderBy(r => r)
            .ToList();
        if (asciiRatios.Count > 0) return asciiRatios[asciiRatios.Count / 2];

        var ratios = items
            .Where(i => i.Width > 0 && i.Text.Length >= 2)
            .Select(i => i.Width / i.Text.Length)
            .Where(r => r is > 2 and < 40)
            .OrderBy(r => r)
            .ToList();
        if (ratios.Count > 0) return ratios[ratios.Count / 2];

        var heights = items.Select(i => i.Height).Where(h => h > 0).OrderBy(h => h).ToList();
        return heights.Count > 0 ? Math.Max(3d, heights[heights.Count / 2] * 0.7) : 7.2d;
    }

    private static bool IsAsciiText(string text)
    {
        foreach (var c in text)
        {
            if (c > 127) return false;
        }
        return true;
    }

    /// <summary>拼一行：行首列位 → 缩进（代码模式按 unit 取整），块间距 → 空格数。</summary>
    private static string BuildLine(IReadOnlyList<LayoutItem> row, double originX, double pitch, int indentWidth, bool useTabs)
    {
        var ordered = row.OrderBy(i => i.X).ToList();
        var cols = Math.Max(0, (int)Math.Round((ordered[0].X - originX) / pitch, MidpointRounding.AwayFromZero));

        var sb = new StringBuilder();
        if (indentWidth > 0)
        {
            var levels = Math.Max(0, (int)Math.Round(cols / (double)indentWidth, MidpointRounding.AwayFromZero));
            sb.Append(useTabs ? new string('\t', levels) : new string(' ', levels * indentWidth));
        }
        else
        {
            sb.Append(' ', cols);
        }

        sb.Append(ordered[0].Text);
        var prevEnd = ordered[0].X + ordered[0].Width;
        for (var i = 1; i < ordered.Count; i++)
        {
            var gap = (ordered[i].X - prevEnd) / pitch;
            // 间距小于半个字符视为同一单词被 OCR 拆开，直接拼接
            var spaces = gap >= 0.45 ? (int)Math.Round(gap, MidpointRounding.AwayFromZero) : 0;
            if (spaces > 0) sb.Append(' ', Math.Clamp(spaces, 0, 16));
            sb.Append(ordered[i].Text);
            prevEnd = ordered[i].X + ordered[i].Width;
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>清理文本输出中的页分隔标记（单张图片识别时 lit 会输出“--- Page 1 ---”，属于噪声）。</summary>
    /// <param name="raw">lit.exe 的原始 stdout 文本。</param>
    /// <param name="keepPageMarkers">true 时保留页标记（PDF 多页结果）。</param>
    private static string NormalizeText(string raw, bool keepPageMarkers)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
        if (keepPageMarkers) return raw.Replace("\r\n", "\n").Trim();
        var lines = raw.Replace("\r\n", "\n").Split('\n');
        var kept = new List<string>(lines.Length);
        foreach (var line in lines)
        {
            var t = line.Trim();
            if (t.StartsWith("--- Page ", StringComparison.OrdinalIgnoreCase) && t.EndsWith("---", StringComparison.Ordinal))
            {
                continue;
            }
            kept.Add(line);
        }
        return string.Join("\n", kept).Trim();
    }
}
