using Serilog;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace AI_Tool_Box.Services;

/// <summary>演示文稿导出结果。</summary>
/// <param name="HtmlPath">导出的播放器 HTML 路径。</param>
/// <param name="AssetFolder">媒体素材文件夹路径（无本地媒体时为 null）。</param>
/// <param name="SlideCount">幻灯片页数。</param>
/// <param name="MediaCount">已复制到素材文件夹的媒体数量。</param>
public sealed record PresentationExportResult(string HtmlPath, string? AssetFolder, int SlideCount, int MediaCount);

/// <summary>演示文稿（HTML 幻灯片）能力：切分幻灯片、生成自包含全屏播放器、收集并搬迁本地媒体为相对引用。</summary>
public interface IPresentationService
{
    /// <summary>按约定把正文 HTML 切分为多张幻灯片（<c>&lt;section class="slide"&gt;</c> → <c>&lt;hr&gt;</c> → <c>&lt;h1&gt;</c> 分组 → 整篇一页）。</summary>
    IReadOnlyList<string> SplitSlides(string contentHtml);

    /// <summary>构建自包含的全屏播放器 HTML（内联 CSS + 原生 JS，无外部依赖，可离线双击打开）。</summary>
    string BuildPlayerHtml(IReadOnlyList<string> slides, string title);

    /// <summary>导出演示文稿：写播放器 HTML；若含本地媒体，则在 HTML 同级创建同名 <c>.assets</c> 文件夹、复制媒体并改写为相对路径。</summary>
    Task<PresentationExportResult> ExportAsync(string contentHtml, string outHtmlPath, string title, CancellationToken ct = default);
}

/// <summary>演示文稿服务实现（纯本地，无第三方依赖）。</summary>
public sealed class PresentationService : IPresentationService
{
    private readonly ILogger _logger;

    /// <summary>构造函数：注入日志器。</summary>
    public PresentationService(ILogger logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    // 显式分节：<section class="... slide ...">
    private static readonly Regex SectionRegex = new(
        @"<section\b(?=[^>]*\bclass\s*=\s*[""'][^""']*\bslide\b)[^>]*>(?<c>.*?)</section>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    // 分节线：任意 <hr>
    private static readonly Regex HrRegex = new(@"<hr\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex HrSplitRegex = new(@"\s*<hr\b[^>]*>\s*", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // 标题分组：每个 <h1> 起一页
    private static readonly Regex H1Regex = new(@"<h1\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex H1SplitRegex = new(@"(?=<h1\b)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // 本地媒体引用：img/video/audio/source 的 src
    private static readonly Regex MediaSrcRegex = new(
        @"<(?:img|video|audio|source)\b[^>]*?\bsrc\s*=\s*(?<q>[""'])(?<u>[^""']+)\k<q>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly HashSet<string> MediaExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".svg", ".ico",
        ".mp4", ".webm", ".ogg", ".ogv", ".mov", ".m4v", ".mkv",
        ".mp3", ".wav", ".m4a", ".aac", ".flac", ".opus"
    };

    /// <inheritdoc/>
    public IReadOnlyList<string> SplitSlides(string contentHtml)
    {
        var html = contentHtml ?? string.Empty;
        if (html.Trim().Length == 0)
        {
            return new[] { string.Empty };
        }

        // 1) 显式 <section class="slide"> 分节
        var sections = SectionRegex.Matches(html);
        if (sections.Count > 0)
        {
            var list = sections
                .Select(m => m.Groups["c"].Value.Trim())
                .Where(s => s.Length > 0)
                .ToList();
            if (list.Count > 0)
            {
                return list;
            }
        }

        // 2) 分节线 <hr class="slide-break"> / <hr>
        if (HrRegex.IsMatch(html))
        {
            var parts = HrSplitRegex.Split(html)
                .Select(p => p.Trim())
                .Where(p => p.Length > 0)
                .ToList();
            if (parts.Count > 0)
            {
                return parts;
            }
        }

        // 3) 按 <h1> 分组（每个 h1 起一页）
        if (H1Regex.IsMatch(html))
        {
            var parts = H1SplitRegex.Split(html)
                .Select(p => p.Trim())
                .Where(p => p.Length > 0)
                .ToList();
            if (parts.Count > 0)
            {
                return parts;
            }
        }

        // 4) 整篇作为一页
        return new[] { html.Trim() };
    }

    /// <inheritdoc/>
    public string BuildPlayerHtml(IReadOnlyList<string> slides, string title)
    {
        var items = slides is { Count: > 0 } ? slides : new List<string> { string.Empty };
        var body = new StringBuilder();
        foreach (var s in items)
        {
            body.Append("<section class=\"slide\">\n").Append(s ?? string.Empty).Append("\n</section>\n");
        }

        var safeTitle = WebUtility.HtmlEncode(string.IsNullOrWhiteSpace(title) ? "演示文稿" : title);
        return PlayerTemplate
            .Replace("__TITLE__", safeTitle)
            .Replace("__SLIDES__", body.ToString())
            .Replace("__SCRIPT__", PlayerScript);
    }

    /// <inheritdoc/>
    public async Task<PresentationExportResult> ExportAsync(string contentHtml, string outHtmlPath, string title, CancellationToken ct = default)
    {
        var slides = SplitSlides(contentHtml);

        // 1) 收集本地媒体（仅已存在的文件）
        var media = FindLocalMedia(contentHtml).Where(m => File.Exists(m.AbsolutePath)).ToList();
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? assetFolder = null;
        var copied = 0;

        // 2) 需要时创建同名 .assets 文件夹并复制媒体
        if (media.Count > 0)
        {
            var dir = Path.GetDirectoryName(outHtmlPath);
            if (string.IsNullOrEmpty(dir))
            {
                dir = Directory.GetCurrentDirectory();
            }
            var baseName = Path.GetFileNameWithoutExtension(outHtmlPath);
            assetFolder = Path.Combine(dir, baseName + ".assets");
            Directory.CreateDirectory(assetFolder);

            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var m in media)
            {
                var name = UniqueName(m.FileName, used);
                var dest = Path.Combine(assetFolder, name);
                try
                {
                    File.Copy(m.AbsolutePath, dest, overwrite: true);
                    copied++;
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "复制演示文稿媒体失败：{Src}", m.AbsolutePath);
                    continue;
                }
                map[m.SourceRef] = (baseName + ".assets/" + name).Replace('\\', '/');
            }

            if (copied == 0)
            {
                TryDeleteEmptyFolder(assetFolder);
                assetFolder = null;
            }
        }

        // 3) 改写幻灯片中的媒体引用为相对路径
        var rewritten = slides.Select(s => RewriteMediaRefs(s, map)).ToList();

        // 4) 写自包含播放器
        var player = BuildPlayerHtml(rewritten, title);
        await File.WriteAllTextAsync(outHtmlPath, player, new UTF8Encoding(false), ct);

        _logger.Information("HTML → 演示文稿：{Path}（{Slides} 页，媒体 {Media} 个）", outHtmlPath, rewritten.Count, copied);
        return new PresentationExportResult(outHtmlPath, assetFolder, rewritten.Count, copied);
    }

    /// <summary>扫描内容中的本地媒体引用（file:// 或绝对路径）。</summary>
    private static List<PresentationMedia> FindLocalMedia(string contentHtml)
    {
        var result = new List<PresentationMedia>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in MediaSrcRegex.Matches(contentHtml ?? string.Empty))
        {
            var src = m.Groups["u"].Value.Trim();
            if (src.Length == 0) continue;
            var path = ToLocalPath(src);
            if (path is null) continue;
            var ext = Path.GetExtension(path);
            if (!MediaExtensions.Contains(ext)) continue;
            if (!seen.Add(src)) continue;
            result.Add(new PresentationMedia(src, path, Path.GetFileName(path)));
        }
        return result;
    }

    /// <summary>把 src 转为本地绝对路径；非本地引用返回 null。</summary>
    private static string? ToLocalPath(string src)
    {
        try
        {
            if (src.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
            {
                return new Uri(src).LocalPath;
            }
            if (src.StartsWith("data:", StringComparison.OrdinalIgnoreCase) ||
                src.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                src.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
            // Windows 绝对路径（含 UNC）
            if (Path.IsPathRooted(src) && File.Exists(src))
            {
                return src;
            }
        }
        catch
        {
            // 非法 URI / 路径：忽略
        }
        return null;
    }

    private static string RewriteMediaRefs(string html, IReadOnlyDictionary<string, string> map)
    {
        if (map.Count == 0 || string.IsNullOrEmpty(html))
        {
            return html;
        }
        var result = html;
        foreach (var kv in map)
        {
            result = result.Replace(kv.Key, kv.Value, StringComparison.OrdinalIgnoreCase);
        }
        return result;
    }

    private static string UniqueName(string fileName, HashSet<string> used)
    {
        if (used.Add(fileName))
        {
            return fileName;
        }
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var ext = Path.GetExtension(fileName);
        for (var i = 1; ; i++)
        {
            var candidate = $"{stem}-{i}{ext}";
            if (used.Add(candidate))
            {
                return candidate;
            }
        }
    }

    private static void TryDeleteEmptyFolder(string folder)
    {
        try
        {
            if (Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any())
            {
                Directory.Delete(folder);
            }
        }
        catch
        {
            // 忽略清理失败
        }
    }

    /// <summary>本地媒体引用（源引用字符串 → 绝对路径）。</summary>
    private sealed record PresentationMedia(string SourceRef, string AbsolutePath, string FileName);

    // ==================== 自包含播放器模板 ====================

    private const string PlayerTemplate = """
<!DOCTYPE html>
<html lang="zh-CN">
<head>
<meta charset="utf-8"/>
<meta name="viewport" content="width=device-width, initial-scale=1"/>
<title>__TITLE__</title>
<!-- ==================================================================
     HTML 演示文稿播放器（自包含，可直接双击用浏览器打开，无需联网）
     · 每张幻灯片为一个 <section class="slide">，导出时按约定自动切分
     · 点击页面右侧 → 前进；左侧 18% 区域 ← 后退（类似 PowerPoint）
     · 键盘：← → Space Enter PageUp/PageDown ↑ ↓ Backspace 翻页
              Home / End 首页/末页；F 切换全屏；Esc 退出全屏或关闭窗口
     · 支持嵌入 <img>/<video>/<audio>/<iframe>（媒体置于同名 .assets 文件夹）
     · 扩展点：可自行追加更丰富的 CSS，或调用 window.Deck 的 next/prev/show
     ================================================================== -->
<style>
  :root{ --deck-bg:#0A0F14; --deck-fg:#EAF6FC; --deck-accent:#00E5FF; }
  *{ box-sizing:border-box; }
  html,body{ margin:0; height:100%; background:var(--deck-bg); color:var(--deck-fg);
    font-family:"Segoe UI","Microsoft YaHei",system-ui,sans-serif; overflow:hidden; }
  #deck{ position:fixed; inset:0; }
  .slide{ position:absolute; inset:0; display:none; flex-direction:column; align-items:center;
    justify-content:center; gap:1em; padding:6vh 8vw; overflow:auto; text-align:center; }
  .slide.active{ display:flex; animation:deckIn .5s cubic-bezier(.22,.61,.36,1) both; }
  @keyframes deckIn{ from{ opacity:0; transform:translateY(28px) scale(.985); } to{ opacity:1; transform:none; } }
  .slide h1{ font-size:clamp(28px,5vw,64px); margin:.2em 0; }
  .slide h2{ font-size:clamp(22px,3.6vw,42px); margin:.2em 0; color:var(--deck-accent); }
  .slide p,.slide li,.slide blockquote{ font-size:clamp(16px,2vw,24px); line-height:1.7; }
  .slide ul,.slide ol{ text-align:left; display:inline-block; margin:0; }
  .slide img,.slide video,.slide iframe{ max-width:100%; max-height:62vh; border:0;
    border-radius:10px; background:#000; }
  .slide audio{ width:min(720px,80%); }
  .slide table{ border-collapse:collapse; font-size:clamp(14px,1.6vw,20px); }
  .slide td,.slide th{ border:1px solid #38505F; padding:6px 10px; }
  .slide a{ color:var(--deck-accent); }
  #deck-progress{ position:fixed; left:0; bottom:0; height:3px; width:0; background:var(--deck-accent);
    transition:width .35s ease; z-index:5; }
  #deck-hud{ position:fixed; right:16px; bottom:12px; font-size:13px; opacity:.55;
    user-select:none; z-index:5; }
  @media print{
    .slide{ position:static; display:flex; page-break-after:always; height:auto; }
    #deck-progress,#deck-hud{ display:none; }
  }
</style>
</head>
<body>
<div id="deck">
__SLIDES__</div>
<div id="deck-progress"></div>
<div id="deck-hud"><span id="deck-page">1</span> / <span id="deck-total">1</span></div>
<script>
__SCRIPT__
</script>
</body>
</html>
""";

    private const string PlayerScript = """
(function () {
  var slides = Array.prototype.slice.call(document.querySelectorAll('#deck .slide'));
  if (!slides.length) {
    var s = document.createElement('section');
    s.className = 'slide active';
    s.textContent = '（无内容）';
    document.getElementById('deck').appendChild(s);
    slides = [s];
  }
  var pageEl = document.getElementById('deck-page');
  var totalEl = document.getElementById('deck-total');
  var barEl = document.getElementById('deck-progress');
  var idx = 0;
  totalEl.textContent = String(slides.length);

  function render() {
    slides.forEach(function (s, i) { s.classList.toggle('active', i === idx); });
    pageEl.textContent = String(idx + 1);
    barEl.style.width = ((idx + 1) / slides.length * 100) + '%';
  }
  function show(n) { idx = Math.max(0, Math.min(slides.length - 1, n)); render(); }
  function next() { if (idx < slides.length - 1) { show(idx + 1); } }
  function prev() { if (idx > 0) { show(idx - 1); } }
  function toggleFullscreen() {
    if (document.fullscreenElement) { document.exitFullscreen(); }
    else if (document.documentElement.requestFullscreen) { document.documentElement.requestFullscreen(); }
  }
  function exitPresentation() {
    if (document.fullscreenElement) { document.exitFullscreen(); return; }
    try { window.close(); } catch (e) { /* 浏览器标签无法脚本关闭时忽略 */ }
  }
  function interactive(t) {
    return !!(t && t.closest && t.closest('video,audio,a,button,input,textarea,select,iframe,.no-advance'));
  }

  // 点击翻页：右侧前进、左侧 18% 后退；交互元素不触发
  document.addEventListener('click', function (e) {
    if (interactive(e.target)) { return; }
    if (e.clientX < window.innerWidth * 0.18) { prev(); } else { next(); }
  });

  // 键盘翻页 / 全屏 / 退出
  document.addEventListener('keydown', function (e) {
    switch (e.key) {
      case 'ArrowRight': case 'ArrowDown': case 'PageDown': case ' ': case 'Enter':
        e.preventDefault(); next(); break;
      case 'ArrowLeft': case 'ArrowUp': case 'PageUp': case 'Backspace':
        e.preventDefault(); prev(); break;
      case 'Home': e.preventDefault(); show(0); break;
      case 'End': e.preventDefault(); show(slides.length - 1); break;
      case 'f': case 'F': toggleFullscreen(); break;
      case 'Escape': exitPresentation(); break;
    }
  });

  // 触摸左右滑动翻页
  var sx = null;
  document.addEventListener('touchstart', function (e) { sx = e.touches[0].clientX; }, { passive: true });
  document.addEventListener('touchend', function (e) {
    if (sx === null) { return; }
    var dx = e.changedTouches[0].clientX - sx; sx = null;
    if (Math.abs(dx) > 50) { if (dx < 0) { next(); } else { prev(); } }
  }, { passive: true });

  show(0);

  // 对外扩展 API：window.Deck.next() / prev() / show(2) / index / count
  window.Deck = {
    show: show, next: next, prev: prev,
    get index() { return idx; },
    get count() { return slides.length; }
  };
})();
""";
}
