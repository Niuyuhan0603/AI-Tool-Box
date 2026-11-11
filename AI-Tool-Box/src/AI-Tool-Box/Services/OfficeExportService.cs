using Serilog;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;

namespace AI_Tool_Box.Services;

/// <summary>HTML 文档导出：DOCX（文本）/ PPTX（标题+要点）/ SVG（foreignObject 保留排版）。</summary>
public interface IOfficeExportService
{
    /// <summary>HTML → DOCX（按段落/标题/列表转为文本行，复用最小 DOCX 写入器）。</summary>
    Task<string> ExportDocxFromHtmlAsync(string html, string outPath, CancellationToken ct = default);

    /// <summary>HTML → PPTX（h1/h2 作为幻灯片标题，段落/列表作为要点）。</summary>
    string ExportPptxFromHtml(string html, string outPath);

    /// <summary>HTML → SVG（&lt;foreignObject&gt; 包裹，可在浏览器/Inkscape 中还原排版）。</summary>
    string BuildSvgFromHtml(string html);
}

/// <summary>HTML 导出实现。</summary>
public sealed class OfficeExportService : IOfficeExportService
{
    private const string RelSlide = "http://schemas.openxmlformats.org/officeDocument/2006/relationships/slide";
    private const string RelSlideMaster = "http://schemas.openxmlformats.org/officeDocument/2006/relationships/slideMaster";
    private const string RelSlideLayout = "http://schemas.openxmlformats.org/officeDocument/2006/relationships/slideLayout";
    private const string RelTheme = "http://schemas.openxmlformats.org/officeDocument/2006/relationships/theme";
    private const string RelOfficeDoc = "http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument";

    private const string CtPresentation = "application/vnd.openxmlformats-officedocument.presentationml.presentation.main+xml";
    private const string CtSlide = "application/vnd.openxmlformats-officedocument.presentationml.slide+xml";
    private const string CtSlideMaster = "application/vnd.openxmlformats-officedocument.presentationml.slideMaster+xml";
    private const string CtSlideLayout = "application/vnd.openxmlformats-officedocument.presentationml.slideLayout+xml";
    private const string CtTheme = "application/vnd.openxmlformats-officedocument.theme+xml";

    private readonly ILogger _logger;
    private readonly ITransformService _transform;

    /// <summary>构造：注入最小 DOCX 写入能力（ITransformService）。</summary>
    public OfficeExportService(ILogger logger, ITransformService transform)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _transform = transform ?? throw new ArgumentNullException(nameof(transform));
    }

    /// <inheritdoc/>
    public async Task<string> ExportDocxFromHtmlAsync(string html, string outPath, CancellationToken ct = default)
    {
        var lines = HtmlToLines(html);
        await _transform.WriteDocxFromLinesAsync(outPath, lines, title: Path.GetFileNameWithoutExtension(outPath));
        _logger.Information("HTML → DOCX：{Path}（{Lines} 行）", outPath, lines.Count);
        return outPath;
    }

    /// <inheritdoc/>
    public string ExportPptxFromHtml(string html, string outPath)
    {
        var slides = HtmlToSlides(html);
        var dir = Path.GetDirectoryName(outPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        using var pkg = System.IO.Packaging.Package.Open(outPath, FileMode.Create, FileAccess.ReadWrite);

        // 主题
        CreatePart(pkg, "/ppt/theme/theme1.xml", CtTheme, ThemeXml());

        // 母版 + 版式
        var master = CreatePart(pkg, "/ppt/slideMasters/slideMaster1.xml", CtSlideMaster, MasterXml());
        var layout = CreatePart(pkg, "/ppt/slideLayouts/slideLayout1.xml", CtSlideLayout, LayoutXml());
        layout.CreateRelationship(new Uri("../slideMasters/slideMaster1.xml", UriKind.Relative), System.IO.Packaging.TargetMode.Internal, RelSlideMaster);
        master.CreateRelationship(new Uri("../slideLayouts/slideLayout1.xml", UriKind.Relative), System.IO.Packaging.TargetMode.Internal, RelSlideLayout);
        master.CreateRelationship(new Uri("../theme/theme1.xml", UriKind.Relative), System.IO.Packaging.TargetMode.Internal, RelTheme);

        // 幻灯片
        var slideIds = new StringBuilder();
        var presentation = CreatePart(pkg, "/ppt/presentation.xml", CtPresentation, string.Empty);
        // 母版关系（rId 顺序固定：先母版，再幻灯片，最后主题）
        var ridMaster = presentation.CreateRelationship(new Uri("slideMasters/slideMaster1.xml", UriKind.Relative), System.IO.Packaging.TargetMode.Internal, RelSlideMaster).Id;

        for (var i = 0; i < slides.Count; i++)
        {
            var part = CreatePart(pkg, $"/ppt/slides/slide{i + 1}.xml", CtSlide, SlideXml(slides[i].Title, slides[i].Bullets));
            part.CreateRelationship(new Uri("../slideLayouts/slideLayout1.xml", UriKind.Relative), System.IO.Packaging.TargetMode.Internal, RelSlideLayout);
            var rid = presentation.CreateRelationship(new Uri($"slides/slide{i + 1}.xml", UriKind.Relative), System.IO.Packaging.TargetMode.Internal, RelSlide).Id;
            slideIds.Append($"""<p:sldId id="{256 + i}" r:id="{rid}"/>""");
        }
        presentation.CreateRelationship(new Uri("theme/theme1.xml", UriKind.Relative), System.IO.Packaging.TargetMode.Internal, RelTheme);

        SetPartXml(presentation, PresentationXml(ridMaster, slideIds.ToString()));
        pkg.CreateRelationship(new Uri("ppt/presentation.xml", UriKind.Relative), System.IO.Packaging.TargetMode.Internal, RelOfficeDoc);

        _logger.Information("HTML → PPTX：{Path}（{Count} 页）", outPath, slides.Count);
        return outPath;
    }

    /// <inheritdoc/>
    public string BuildSvgFromHtml(string html)
    {
        const int w = 1000, h = 1400;
        var body = html ?? string.Empty;
        return $"""
<?xml version="1.0" encoding="UTF-8"?>
<svg xmlns="http://www.w3.org/2000/svg" width="{w}" height="{h}" viewBox="0 0 {w} {h}">
  <rect width="100%" height="100%" fill="#ffffff"/>
  <foreignObject x="40" y="40" width="{w - 80}" height="{h - 80}">
    <div xmlns="http://www.w3.org/1999/xhtml" style="font-family:'Segoe UI','Microsoft YaHei',sans-serif;font-size:15px;line-height:1.75;color:#111418">
      {body}
    </div>
  </foreignObject>
</svg>
""";
    }

    // ==================== HTML 解析 ====================

    /// <summary>把 HTML 转为纯文本行（段落/标题/列表/表格行各占一行）。</summary>
    private static List<string> HtmlToLines(string html)
    {
        var text = html ?? string.Empty;
        text = Regex.Replace(text, @"<br\s*/?>", "\n", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"</(p|div|h[1-6]|li|tr|blockquote)>", "\n", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"<li[^>]*>", "- ", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"<[^>]+>", string.Empty);
        text = WebUtility.HtmlDecode(text);

        var lines = new List<string>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Replace('\u00A0', ' ').TrimEnd();
            if (line.Trim().Length == 0 && (lines.Count == 0 || lines[^1].Length == 0)) continue;
            lines.Add(line);
        }
        return lines;
    }

    /// <summary>按 h1/h2 切分幻灯片；无标题时整篇作为一页。</summary>
    private List<(string Title, List<string> Bullets)> HtmlToSlides(string html)
    {
        var slides = new List<(string Title, List<string> Bullets)>();
        var title = "演示文稿";
        var bullets = new List<string>();

        var pattern = @"<h[12][^>]*>(?<h>.*?)</h[12]>|<(?:p|li|blockquote)[^>]*>(?<p>.*?)</(?:p|li|blockquote)>";
        foreach (Match m in Regex.Matches(html ?? string.Empty, pattern, RegexOptions.IgnoreCase | RegexOptions.Singleline))
        {
            if (m.Groups["h"].Success)
            {
                if (bullets.Count > 0 || slides.Count > 0) slides.Add((title, bullets));
                title = Clean(m.Groups["h"].Value);
                bullets = new List<string>();
            }
            else
            {
                var line = Clean(m.Groups["p"].Value);
                if (line.Length == 0) continue;
                bullets.Add(line);
            }
        }
        if (bullets.Count > 0 || slides.Count == 0) slides.Add((title, bullets));

        // 单页要点过多时按 8 条拆分，避免溢出
        var result = new List<(string Title, List<string> Bullets)>();
        foreach (var (t, b) in slides)
        {
            if (b.Count <= 10) { result.Add((t, b)); continue; }
            for (var i = 0; i < b.Count; i += 8)
            {
                var slice = b.Skip(i).Take(8).ToList();
                result.Add((i == 0 ? t : t + "（续）", slice));
            }
        }
        foreach (var (t, b) in result)
        {
            _logger.Debug("PPTX 页：{Title} / {Count} 条", t, b.Count);
        }
        return result;
    }

    private static string Clean(string fragment)
    {
        var t = Regex.Replace(fragment, @"<br\s*/?>", " ", RegexOptions.IgnoreCase);
        t = Regex.Replace(t, @"<[^>]+>", string.Empty);
        return WebUtility.HtmlDecode(t).Replace('\u00A0', ' ').Trim();
    }

    private static string Esc(string? s) => SecurityElementEscape(s ?? string.Empty);

    private static string SecurityElementEscape(string s) => s
        .Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");

    // ==================== OOXML 组装 ====================

    private static System.IO.Packaging.PackagePart CreatePart(System.IO.Packaging.Package pkg, string uri, string contentType, string xml)
    {
        var part = pkg.CreatePart(new Uri(uri, UriKind.Relative), contentType);
        if (!string.IsNullOrEmpty(xml)) SetPartXml(part, xml);
        return part;
    }

    private static void SetPartXml(System.IO.Packaging.PackagePart part, string xml)
    {
        using var stream = part.GetStream(FileMode.Create, FileAccess.Write);
        var bytes = new UTF8Encoding(false).GetBytes(xml);
        stream.Write(bytes, 0, bytes.Length);
    }

    private static string PresentationXml(string masterRid, string slideIds) => $"""
<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<p:presentation xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships" xmlns:p="http://schemas.openxmlformats.org/presentationml/2006/main">
  <p:sldMasterIdLst><p:sldMasterId id="2147483648" r:id="{masterRid}"/></p:sldMasterIdLst>
  <p:sldIdLst>{slideIds}</p:sldIdLst>
  <p:sldSz cx="12192000" cy="6858000"/>
  <p:notesSz cx="6858000" cy="9144000"/>
</p:presentation>
""";

    private static string MasterXml() => """
<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<p:sldMaster xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships" xmlns:p="http://schemas.openxmlformats.org/presentationml/2006/main">
  <p:cSld>
    <p:spTree>
      <p:nvGrpSpPr><p:cNvPr id="1" name=""/><p:cNvGrpSpPr/><p:nvPr/></p:nvGrpSpPr>
      <p:grpSpPr><a:xfrm><a:off x="0" y="0"/><a:ext cx="0" cy="0"/><a:chOff x="0" y="0"/><a:chExt cx="0" cy="0"/></a:xfrm></p:grpSpPr>
    </p:spTree>
  </p:cSld>
  <p:clrMap bg1="lt1" tx1="dk1" bg2="lt2" tx2="dk2" accent1="accent1" accent2="accent2" accent3="accent3" accent4="accent4" accent5="accent5" accent6="accent6" hlink="hlink" folHlink="folHlink"/>
  <p:sldLayoutIdLst><p:sldLayoutId id="2147483649" r:id="rId1"/></p:sldLayoutIdLst>
</p:sldMaster>
""";

    private static string LayoutXml() => """
<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<p:sldLayout xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships" xmlns:p="http://schemas.openxmlformats.org/presentationml/2006/main" type="blank" preserve="1">
  <p:cSld name="空白">
    <p:spTree>
      <p:nvGrpSpPr><p:cNvPr id="1" name=""/><p:cNvGrpSpPr/><p:nvPr/></p:nvGrpSpPr>
      <p:grpSpPr><a:xfrm><a:off x="0" y="0"/><a:ext cx="0" cy="0"/><a:chOff x="0" y="0"/><a:chExt cx="0" cy="0"/></a:xfrm></p:grpSpPr>
    </p:spTree>
  </p:cSld>
  <p:clrMapOvr><a:masterClrMapping/></p:clrMapOvr>
</p:sldLayout>
""";

    private static string SlideXml(string title, IReadOnlyList<string> bullets)
    {
        var body = new StringBuilder();
        foreach (var b in bullets.Take(12))
        {
            body.Append($"""
<a:p><a:pPr marL="285750" indent="-285750"><a:buFont typeface="Arial"/><a:buChar char="•"/></a:pPr>
<a:r><a:rPr lang="zh-CN" sz="1800" dirty="0"/><a:t>{Esc(b)}</a:t></a:r></a:p>
""");
        }
        if (bullets.Count == 0)
        {
            body.Append("""<a:p><a:r><a:rPr lang="zh-CN" sz="1800" dirty="0"/><a:t></a:t></a:r></a:p>""");
        }

        return $"""
<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<p:sld xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships" xmlns:p="http://schemas.openxmlformats.org/presentationml/2006/main">
  <p:cSld>
    <p:spTree>
      <p:nvGrpSpPr><p:cNvPr id="1" name=""/><p:cNvGrpSpPr/><p:nvPr/></p:nvGrpSpPr>
      <p:grpSpPr><a:xfrm><a:off x="0" y="0"/><a:ext cx="0" cy="0"/><a:chOff x="0" y="0"/><a:chExt cx="0" cy="0"/></a:xfrm></p:grpSpPr>
      <p:sp>
        <p:nvSpPr><p:cNvPr id="2" name="Title"/><p:cNvSpPr><a:spLocks noGrp="1"/></p:cNvSpPr><p:nvPr/></p:nvSpPr>
        <p:spPr>
          <a:xfrm><a:off x="609600" y="457200"/><a:ext cx="10972800" cy="1188720"/></a:xfrm>
          <a:prstGeom prst="rect"><a:avLst/></a:prstGeom>
        </p:spPr>
        <p:txBody><a:bodyPr/><a:lstStyle/>
          <a:p><a:r><a:rPr lang="zh-CN" sz="3200" b="1" dirty="0"/><a:t>{Esc(title)}</a:t></a:r></a:p>
        </p:txBody>
      </p:sp>
      <p:sp>
        <p:nvSpPr><p:cNvPr id="3" name="Content"/><p:cNvSpPr><a:spLocks noGrp="1"/></p:cNvSpPr><p:nvPr/></p:nvSpPr>
        <p:spPr>
          <a:xfrm><a:off x="609600" y="1783080"/><a:ext cx="10972800" cy="4343400"/></a:xfrm>
          <a:prstGeom prst="rect"><a:avLst/></a:prstGeom>
        </p:spPr>
        <p:txBody><a:bodyPr><a:normAutofit/></a:bodyPr><a:lstStyle/>
          {body}
        </p:txBody>
      </p:sp>
    </p:spTree>
  </p:cSld>
  <p:clrMapOvr><a:masterClrMapping/></p:clrMapOvr>
</p:sld>
""";
    }

    private static string ThemeXml() => """
<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<a:theme xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main" name="Office">
  <a:themeElements>
    <a:clrScheme name="Office">
      <a:dk1><a:sysClr val="windowText" lastClr="000000"/></a:dk1>
      <a:lt1><a:sysClr val="window" lastClr="FFFFFF"/></a:lt1>
      <a:dk2><a:srgbClr val="44546A"/></a:dk2>
      <a:lt2><a:srgbClr val="E7E6E6"/></a:lt2>
      <a:accent1><a:srgbClr val="4472C4"/></a:accent1>
      <a:accent2><a:srgbClr val="ED7D31"/></a:accent2>
      <a:accent3><a:srgbClr val="A5A5A5"/></a:accent3>
      <a:accent4><a:srgbClr val="FFC000"/></a:accent4>
      <a:accent5><a:srgbClr val="5B9BD5"/></a:accent5>
      <a:accent6><a:srgbClr val="70AD47"/></a:accent6>
      <a:hlink><a:srgbClr val="0563C1"/></a:hlink>
      <a:folHlink><a:srgbClr val="954F72"/></a:folHlink>
    </a:clrScheme>
    <a:fontScheme name="Office">
      <a:majorFont><a:latin typeface="Calibri Light"/><a:ea typeface=""/><a:cs typeface=""/></a:majorFont>
      <a:minorFont><a:latin typeface="Calibri"/><a:ea typeface=""/><a:cs typeface=""/></a:minorFont>
    </a:fontScheme>
    <a:fmtScheme name="Office">
      <a:fillStyleLst>
        <a:solidFill><a:schemeClr val="phClr"/></a:solidFill>
        <a:solidFill><a:schemeClr val="phClr"/></a:solidFill>
        <a:solidFill><a:schemeClr val="phClr"/></a:solidFill>
      </a:fillStyleLst>
      <a:lnStyleLst>
        <a:ln w="6350" cap="flat" cmpd="sng" algn="ctr"><a:solidFill><a:schemeClr val="phClr"/></a:solidFill><a:prstDash val="solid"/><a:miter lim="800000"/></a:ln>
        <a:ln w="12700" cap="flat" cmpd="sng" algn="ctr"><a:solidFill><a:schemeClr val="phClr"/></a:solidFill><a:prstDash val="solid"/><a:miter lim="800000"/></a:ln>
        <a:ln w="19050" cap="flat" cmpd="sng" algn="ctr"><a:solidFill><a:schemeClr val="phClr"/></a:solidFill><a:prstDash val="solid"/><a:miter lim="800000"/></a:ln>
      </a:lnStyleLst>
      <a:effectStyleLst>
        <a:effectStyle><a:effectLst/></a:effectStyle>
        <a:effectStyle><a:effectLst/></a:effectStyle>
        <a:effectStyle><a:effectLst/></a:effectStyle>
      </a:effectStyleLst>
      <a:bgFillStyleLst>
        <a:solidFill><a:schemeClr val="phClr"/></a:solidFill>
        <a:solidFill><a:schemeClr val="phClr"/></a:solidFill>
        <a:solidFill><a:schemeClr val="phClr"/></a:solidFill>
      </a:bgFillStyleLst>
    </a:fmtScheme>
  </a:themeElements>
</a:theme>
""";
}
