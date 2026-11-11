using Serilog;
using System.Diagnostics;
using System.IO.Compression;
using System.IO.Packaging;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Package = System.IO.Packaging.Package;

namespace AI_Tool_Box.Services;

/// <summary>文件转换服务（零额外依赖；Docx ↔ 文本 ↔ 最小 PDF；FFmpeg 转码；HTTP Range 分块下载；NCM 还原）。</summary>
public sealed class TransformService : ITransformService
{
    private readonly ILogger _logger;
    private readonly HttpClient _http;
    private readonly FfmpegLocator _ffmpeg;

    /// <summary>构造：注入 FfmpegLocator（DI 未注册时回退为新实例，解析顺序：用户设置 → PATH/常见位置 → Assets 兜底）。</summary>
    public TransformService(ILogger logger, FfmpegLocator? ffmpeg = null)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _ffmpeg = ffmpeg ?? new FfmpegLocator();
        _http = new HttpClient(new HttpClientHandler
        {
            AllowAutoRedirect = true,
            UseCookies = true,
            AutomaticDecompression = System.Net.DecompressionMethods.All
        })
        { Timeout = TimeSpan.FromMinutes(30) };
    }

    // ========== Docx ==========

    public async Task<string> DocxExtractTextAsync(string docxPath, CancellationToken ct = default)
    {
        _logger.Information("Docx 提取文本：{Path}", docxPath);
        await Task.Yield();
        using var pkg = Package.Open(docxPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var part = pkg.GetPart(new Uri("/word/document.xml", UriKind.Relative));
        using var stream = part.GetStream(FileMode.Open, FileAccess.Read);
        var xdoc = XDocument.Load(stream);
        var w = (XNamespace)"http://schemas.openxmlformats.org/wordprocessingml/2006/main";
        var sb = new StringBuilder();
        foreach (var para in xdoc.Descendants(w + "p"))
        {
            var texts = para.Descendants(w + "t").Select(t => t.Value);
            sb.Append(string.Concat(texts));
            sb.AppendLine();
        }
        return sb.ToString();
    }

    /// <summary>DOCX → 简易 HTML：解压 word/document.xml，按段落/行内文本转 &lt;p&gt;/&lt;hN&gt;（保留粗体/斜体）。</summary>
    public async Task<string> DocxToHtmlAsync(string docxPath, CancellationToken ct = default)
    {
        _logger.Information("Docx 转 HTML：{Path}", docxPath);
        await Task.Yield();
        await using var fs = File.OpenRead(docxPath);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Read);
        var entry = zip.GetEntry("word/document.xml")
                    ?? throw new InvalidOperationException("不是有效的 DOCX 文件（缺少 word/document.xml）。");
        await using var es = entry.Open();
        var xdoc = await XDocument.LoadAsync(es, LoadOptions.None, ct);
        XNamespace w = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

        var sb = new StringBuilder();
        foreach (var para in xdoc.Descendants(w + "p"))
        {
            var style = para.Element(w + "pPr")?.Element(w + "pStyle")?.Attribute(w + "val")?.Value ?? string.Empty;
            var heading = Regex.Match(style, @"^[Hh]eading(\d)$");
            var inner = string.Concat(para.Elements(w + "r").Select(r => DocxRunToHtml(r, w)));
            if (inner.Length == 0) inner = "<br/>";
            sb.Append(heading.Success ? $"<h{heading.Groups[1].Value}>{inner}</h{heading.Groups[1].Value}>" : $"<p>{inner}</p>");
        }
        return sb.ToString();
    }

    /// <summary>将单个 w:r 转为 HTML：文本转义，按 w:b/w:i 包裹 strong/em，w:br/tab 转 br/空格。</summary>
    private static string DocxRunToHtml(XElement run, XNamespace w)
    {
        var sb = new StringBuilder();
        foreach (var node in run.Elements())
        {
            if (node.Name == w + "t") sb.Append(WebUtility.HtmlEncode(node.Value));
            else if (node.Name == w + "br" || node.Name == w + "cr") sb.Append("<br/>");
            else if (node.Name == w + "tab") sb.Append("&emsp;");
        }
        var text = sb.ToString();
        if (text.Length == 0) return string.Empty;
        var rPr = run.Element(w + "rPr");
        if (rPr?.Element(w + "b") is not null) text = "<strong>" + text + "</strong>";
        if (rPr?.Element(w + "i") is not null) text = "<em>" + text + "</em>";
        return text;
    }

    public async Task WriteDocxFromLinesAsync(string outDocx, IEnumerable<string> lines, string title = "", CancellationToken ct = default)
    {
        _logger.Information("写入 Docx：{Path}", outDocx);
        await Task.Yield();
        using var pkg = Package.Open(outDocx, FileMode.Create, FileAccess.ReadWrite);

        // ContentTypes.xml
        var ctPartUri = new Uri("/[Content_Types].xml", UriKind.Relative);
        var ctPart = pkg.CreatePart(ctPartUri, "application/xml");
        var ctXdoc = new XDocument(
            new XElement("Types",
                new XAttribute("xmlns", "http://schemas.openxmlformats.org/package/2006/content-types"),
                new XElement("Default", new XAttribute("Extension", "rels"), new XAttribute("ContentType", "application/vnd.openxmlformats-package.relationships+xml")),
                new XElement("Default", new XAttribute("Extension", "xml"), new XAttribute("ContentType", "application/xml")),
                new XElement("Override", new XAttribute("PartName", "/word/document.xml"), new XAttribute("ContentType", "application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml")),
                new XElement("Override", new XAttribute("PartName", "/docProps/core.xml"), new XAttribute("ContentType", "application/vnd.openxmlformats-package.core-properties+xml")),
                new XElement("Override", new XAttribute("PartName", "/docProps/app.xml"), new XAttribute("ContentType", "application/vnd.openxmlformats-officedocument.extended-properties+xml"))));
        using (var s = ctPart.GetStream(FileMode.Create, FileAccess.Write)) ctXdoc.Save(s);

        // Relationships
        var relsPartUri = new Uri("/_rels/.rels", UriKind.Relative);
        var relsPart = pkg.CreatePart(relsPartUri, "application/vnd.openxmlformats-package.relationships+xml");
        var relsXdoc = new XDocument(
            new XElement("Relationships",
                new XAttribute("xmlns", "http://schemas.openxmlformats.org/package/2006/relationships"),
                new XElement("Relationship", new XAttribute("Id", "rId1"), new XAttribute("Type", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument"), new XAttribute("Target", "word/document.xml")),
                new XElement("Relationship", new XAttribute("Id", "rId2"), new XAttribute("Type", "http://schemas.openxmlformats.org/package/2006/relationships/metadata/core-properties"), new XAttribute("Target", "docProps/core.xml")),
                new XElement("Relationship", new XAttribute("Id", "rId3"), new XAttribute("Type", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/extended-properties"), new XAttribute("Target", "docProps/app.xml"))));
        using (var s = relsPart.GetStream(FileMode.Create, FileAccess.Write)) relsXdoc.Save(s);

        // core.xml
        var corePart = pkg.CreatePart(new Uri("/docProps/core.xml", UriKind.Relative), "application/vnd.openxmlformats-package.core-properties+xml");
        var coreXdoc = new XDocument(
            new XElement("cp:coreProperties",
                new XAttribute(XNamespace.Xmlns + "cp", "http://schemas.openxmlformats.org/package/2006/metadata/core-properties"),
                new XAttribute(XNamespace.Xmlns + "dc", "http://purl.org/dc/elements/1.1/"),
                new XAttribute(XNamespace.Xmlns + "dcterms", "http://purl.org/dc/terms/"),
                new XElement("dc:title", title ?? Path.GetFileNameWithoutExtension(outDocx)),
                new XElement("dc:creator", "AI-Tool-Box")));
        using (var s = corePart.GetStream(FileMode.Create, FileAccess.Write)) coreXdoc.Save(s);

        // app.xml
        var appPart = pkg.CreatePart(new Uri("/docProps/app.xml", UriKind.Relative), "application/vnd.openxmlformats-officedocument.extended-properties+xml");
        var appXdoc = new XDocument(new XElement("Properties", new XAttribute("xmlns", "http://schemas.openxmlformats.org/officeDocument/2006/extended-properties"),
            new XElement("Application", "AI-Tool-Box")));
        using (var s = appPart.GetStream(FileMode.Create, FileAccess.Write)) appXdoc.Save(s);

        // document.xml
        var docPart = pkg.CreatePart(new Uri("/word/document.xml", UriKind.Relative), "application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml");
        var w = (XNamespace)"http://schemas.openxmlformats.org/wordprocessingml/2006/main";
        var body = new XElement(w + "body");
        foreach (var line in lines ?? Enumerable.Empty<string>())
        {
            var para = new XElement(w + "p",
                new XElement(w + "r",
                    new XElement(w + "t", new XAttribute(XNamespace.Xml + "space", "preserve"), line ?? string.Empty)));
            body.Add(para);
        }
        var docXdoc = new XDocument(
            new XElement(w + "document",
                new XAttribute("xmlns", w.NamespaceName),
                body));
        using (var s = docPart.GetStream(FileMode.Create, FileAccess.Write)) docXdoc.Save(s);

        // word/_rels/document.xml.rels（空）
        var docRelsPart = pkg.CreatePart(new Uri("/word/_rels/document.xml.rels", UriKind.Relative), "application/vnd.openxmlformats-package.relationships+xml");
        var docRelsXdoc = new XDocument(new XElement("Relationships", new XAttribute("xmlns", "http://schemas.openxmlformats.org/package/2006/relationships")));
        using (var s = docRelsPart.GetStream(FileMode.Create, FileAccess.Write)) docRelsXdoc.Save(s);

        pkg.Flush();
        _logger.Information("Docx 已写出：{Path}", outDocx);
    }

    // ========== PDF (最小实现：不依赖第三方库，只输出可打开的文本 A4 PDF) ==========

    private static readonly Regex PdfTextClean = new(@"[\x00-\x08\x0B-\x1F\x7F]", RegexOptions.Compiled);

    public Task<string> PdfExtractTextAsync(string pdfPath, CancellationToken ct = default)
    {
        _logger.Information("PDF 简易文本提取：{Path}", pdfPath);
        // 极简：提取所有 BT ... ET 内的 (xxx) Tj / [(x)(y)] TJ 字面字符串（无压缩、无编码映射）
        var raw = File.ReadAllBytes(pdfPath);
        var sb = new StringBuilder();
        var s = Encoding.Latin1.GetString(raw);
        // 抓 BT...ET 块
        var btMatches = Regex.Matches(s, @"BT\s*(.+?)\s*ET", RegexOptions.Singleline);
        foreach (Match m in btMatches)
        {
            var block = m.Groups[1].Value;
            // Tj: (text) Tj
            foreach (Match tj in Regex.Matches(block, @"\(((?:\\.|[^\\()])*)\)\s*Tj"))
            {
                sb.Append(UnescapePdfLiteral(tj.Groups[1].Value));
            }
            // TJ: [(one) 50 (two)] TJ
            foreach (Match tj in Regex.Matches(block, @"\[([^\]]*)\]\s*TJ"))
            {
                var inner = tj.Groups[1].Value;
                foreach (Match lit in Regex.Matches(inner, @"\(((?:\\.|[^\\()])*)\)"))
                {
                    sb.Append(UnescapePdfLiteral(lit.Groups[1].Value));
                }
            }
            sb.AppendLine();
        }
        var txt = PdfTextClean.Replace(sb.ToString(), " ");
        return Task.FromResult(txt);
    }

    private static string UnescapePdfLiteral(string s)
    {
        var sb = new StringBuilder(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (c == '\\' && i + 1 < s.Length)
            {
                i++;
                var n = s[i];
                sb.Append(n switch
                {
                    'n' => '\n',
                    'r' => '\r',
                    't' => '\t',
                    'b' => '\b',
                    'f' => '\f',
                    '(' => '(',
                    ')' => ')',
                    '\\' => '\\',
                    _ => n
                });
            }
            else sb.Append(c);
        }
        return sb.ToString();
    }

    public async Task WriteSimplePdfFromLinesAsync(string outPdf, IEnumerable<string> lines, string title = "", CancellationToken ct = default)
    {
        _logger.Information("写出文本型 PDF：{Path}", outPdf);
        await Task.Yield();

        // A4: 595 x 842 pt; 边距 50; 每行高度 14; 每字符约 6 pt (Helv 10 非 CID)
        // 非 ASCII 字符在 Helv 标准字体下会丢失；将中文等用 "?" 代替（保留换行），保证 PDF 至少可打开。
        const int PageW = 595, PageH = 842;
        const int MarginL = 50, MarginR = 50, MarginT = 60, MarginB = 60;
        const int LineH = 14;
        const int CharPtX = 6;
        int maxPerLine = (PageW - MarginL - MarginR) / CharPtX;
        int maxLinesPerPage = (PageH - MarginT - MarginB) / LineH;

        var flatLines = new List<string>();
        if (!string.IsNullOrWhiteSpace(title))
        {
            flatLines.Add($"==== {title.Trim()} ====");
            flatLines.Add(string.Empty);
        }
        foreach (var ln in lines ?? Enumerable.Empty<string>())
        {
            var safe = ln ?? string.Empty;
            // 折叠非 ASCII 为 "?" 以避免标准 Helv 字体无法显示
            var sb = new StringBuilder(safe.Length);
            foreach (var ch in safe)
            {
                if (ch < 0x20 || ch == '\r' || ch == '\n') continue;
                if (ch < 0x7F || ch == '　') sb.Append(ch < 0x7F ? ch : ' ');
                else sb.Append('?');
            }
            var line = sb.ToString();
            if (line.Length <= maxPerLine) { flatLines.Add(line); continue; }
            for (int i = 0; i < line.Length; i += maxPerLine)
            {
                var sub = i + maxPerLine <= line.Length ? line.Substring(i, maxPerLine) : line[i..];
                flatLines.Add(sub);
            }
        }
        if (flatLines.Count == 0) flatLines.Add(string.Empty);

        var totalPages = (int)Math.Ceiling(flatLines.Count / (double)maxLinesPerPage);

        using var fs = new FileStream(outPdf, FileMode.Create, FileAccess.ReadWrite);
        using var wr = new StreamWriter(fs, new UTF8Encoding(false), leaveOpen: true) { NewLine = "\n" };
        wr.Write("%PDF-1.4\n%\xe2\xe3\xcf\xd3\n");
        wr.Flush();

        var objects = new List<long>(); // 对象索引 -> 文件偏移
        long offset() => fs.Position;

        void WriteObjHeader(int id)
        {
            wr.Flush();
            objects.Add(offset());
            wr.Write($"{id} 0 obj\n");
        }

        // 对象 1: Catalog
        WriteObjHeader(1);
        wr.Write($"<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");

        // 对象 2: Pages
        WriteObjHeader(2);
        var kids = new StringBuilder();
        for (int p = 1; p <= totalPages; p++) kids.Append($" {2 + p} 0 R");
        wr.Write($"<< /Type /Pages /Kids [{kids} ] /Count {totalPages} >>\nendobj\n");

        // 对象 3..N: 每页 Page + 内容流
        for (int p = 0; p < totalPages; p++)
        {
            var pageId = 3 + p;
            var contentId = 3 + totalPages + p;
            // 页面
            WriteObjHeader(pageId);
            wr.Write($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {PageW} {PageH}] /Resources << /Font << /F1 {3 + 2 * totalPages} 0 R >> >> /Contents {contentId} 0 R >>\nendobj\n");
        }

        // 内容流
        for (int p = 0; p < totalPages; p++)
        {
            var contentId = 3 + totalPages + p;
            // 先把 stream 内容写临时 buffer，以确定长度
            using var ms = new MemoryStream();
            using var sw = new StreamWriter(ms, new UTF8Encoding(false), leaveOpen: true) { NewLine = "\n" };
            sw.Write("BT\n");
            sw.Write($"/F1 10 Tf\n");
            int startLine = p * maxLinesPerPage;
            for (int i = 0; i < maxLinesPerPage && startLine + i < flatLines.Count; i++)
            {
                var y = PageH - MarginT - i * LineH;
                var txt = flatLines[startLine + i];
                var pdfLit = ToPdfLiteral(txt);
                sw.Write($"1 0 0 1 {MarginL} {y} Tm\n");
                sw.Write($"{pdfLit} Tj\n");
            }
            sw.Write("ET\n");
            sw.Flush();

            var buf = ms.ToArray();
            WriteObjHeader(contentId);
            wr.Write($"<< /Length {buf.Length} >>\nstream\n");
            wr.Flush();
            fs.Write(buf, 0, buf.Length);
            wr.Write("\nendstream\nendobj\n");
        }

        // Font: Helvetica
        WriteObjHeader(3 + 2 * totalPages);
        wr.Write("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>\nendobj\n");

        // xref
        wr.Flush();
        var xrefPos = offset();
        var objCount = objects.Count + 1;
        wr.Write($"xref\n0 {objCount}\n");
        wr.Write("0000000000 65535 f \n");
        foreach (var off in objects)
        {
            wr.Write($"{off:0000000000} 00000 n \n");
        }
        wr.Write($"trailer\n<< /Size {objCount} /Root 1 0 R >>\nstartxref\n{xrefPos}\n%%EOF\n");
        wr.Flush();
    }

    private static string ToPdfLiteral(string s)
    {
        // 转义 () 和 \
        var sb = new StringBuilder(s.Length + 2);
        sb.Append('(');
        foreach (var ch in s)
        {
            if (ch == '(') sb.Append("\\(");
            else if (ch == ')') sb.Append("\\)");
            else if (ch == '\\') sb.Append("\\\\");
            else if (ch >= 0x20 && ch < 0x7F) sb.Append(ch);
        }
        sb.Append(')');
        return sb.ToString();
    }

    // ========== FFmpeg ==========

    /// <summary>当前实际生效的 ffmpeg.exe 路径（委托 FfmpegLocator：用户设置 → PATH/常见位置 → Assets 兜底）。</summary>
    public string? LocateFfmpeg() => _ffmpeg.Resolve();

    public async Task<string> ConvertToMp4Async(string input, string output, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var ffmpeg = _ffmpeg.Resolve()
            ?? throw new FileNotFoundException("未检测到 ffmpeg.exe。请在本页「FFmpeg 设置」指定外部 ffmpeg.exe 路径，或安装 ffmpeg 并加入系统 PATH。");
        if (!File.Exists(input)) throw new FileNotFoundException("输入文件不存在：" + input);
        _logger.Information("开始转码：{Input} -> {Output}", input, output);

        Directory.CreateDirectory(Path.GetDirectoryName(output) ?? ".");
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var proc = new Process();
        proc.StartInfo = new ProcessStartInfo
        {
            FileName = ffmpeg,
            Arguments = $"-hide_banner -nostdin -y -i \"{input}\" -c:v libx264 -preset veryfast -crf 23 -c:a aac -b:a 192k -movflags +faststart \"{output}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            StandardErrorEncoding = Encoding.UTF8,
        };
        proc.EnableRaisingEvents = true;

        double durationSec = 0;

        proc.ErrorDataReceived += (_, e) =>
        {
            if (string.IsNullOrEmpty(e.Data)) return;
            var line = e.Data;
            try
            {
                // Duration: 00:00:10.12
                if (line.Contains("Duration:", StringComparison.Ordinal))
                {
                    var m = Regex.Match(line, @"Duration:\s*(\d+):(\d+):([\d.]+)");
                    if (m.Success && int.TryParse(m.Groups[1].Value, out var h) && int.TryParse(m.Groups[2].Value, out var min) && double.TryParse(m.Groups[3].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var sec))
                    {
                        durationSec = h * 3600 + min * 60 + sec;
                        _logger.Information("视频总时长：{Dur}s", durationSec);
                    }
                }
                // out_time_ms=... 或 time=00:00:01.00
                Match tm;
                if ((tm = Regex.Match(line, @"time=(\d+):(\d+):([\d.]+)")).Success
                    && int.TryParse(tm.Groups[1].Value, out var th) && int.TryParse(tm.Groups[2].Value, out var tmin)
                    && double.TryParse(tm.Groups[3].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var tsec))
                {
                    var cur = th * 3600 + tmin * 60 + tsec;
                    if (durationSec > 0) progress?.Report(Math.Clamp(cur / durationSec, 0, 0.995));
                }
            }
            catch { }
        };

        proc.Exited += (_, _) =>
        {
            if (proc.ExitCode == 0 && File.Exists(output))
            {
                progress?.Report(1.0);
                tcs.TrySetResult(output);
            }
            else tcs.TrySetException(new InvalidOperationException($"ffmpeg 退出码 {proc.ExitCode}。请确认输入是受支持的视频格式。"));
        };

        using var reg = ct.Register(() =>
        {
            try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { }
            tcs.TrySetCanceled(ct);
        });

        proc.Start();
        proc.BeginErrorReadLine();
        proc.BeginOutputReadLine();
        return await tcs.Task;
    }

    // ========== 通用音视频格式转换 ==========

    /// <summary>通用音视频/图片格式转换：按输出扩展名自动选择 ffmpeg 编码参数。</summary>
    public async Task<string> ConvertMediaAsync(string input, string output, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var ffmpeg = _ffmpeg.Resolve()
            ?? throw new FileNotFoundException("未检测到 ffmpeg.exe。请在本页「FFmpeg 设置」指定外部 ffmpeg.exe 路径，或安装 ffmpeg 并加入系统 PATH。");
        if (!File.Exists(input)) throw new FileNotFoundException("输入文件不存在：" + input);

        var ext = Path.GetExtension(output).ToLowerInvariant();
        var codecArgs = ext switch
        {
            // 纯音频输出（去掉视频流）
            ".mp3" => "-vn -c:a libmp3lame -b:a 320k",
            ".flac" => "-vn -c:a flac",
            ".wav" => "-vn -c:a pcm_s16le",
            ".aac" => "-vn -c:a aac -b:a 256k",
            ".m4a" => "-vn -c:a aac -b:a 256k",
            ".ogg" => "-vn -c:a libvorbis -q:a 6",
            ".opus" => "-vn -c:a libopus -b:a 128k",
            ".wma" => "-vn -c:a wmav2 -b:a 192k",
            // 视频输出
            ".avi" => "-c:v mpeg4 -q:v 5 -c:a libmp3lame -b:a 192k",
            ".mkv" => "-c:v libx264 -preset veryfast -crf 23 -c:a aac -b:a 192k",
            ".webm" => "-c:v libvpx-vp9 -crf 32 -b:v 0 -c:a libopus -b:a 128k",
            ".mov" => "-c:v libx264 -preset veryfast -crf 23 -c:a aac -b:a 192k",
            ".gif" => "-vf fps=12,scale=480:-1 -an",
            _ => "-c:v libx264 -preset veryfast -crf 23 -c:a aac -b:a 192k -movflags +faststart",
        };
        _logger.Information("音视频转换：{Input} -> {Output}（目标 {Ext}）", input, output, ext);

        Directory.CreateDirectory(Path.GetDirectoryName(output) ?? ".");
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var proc = new Process();
        proc.StartInfo = new ProcessStartInfo
        {
            FileName = ffmpeg,
            Arguments = $"-hide_banner -nostdin -y -i \"{input}\" {codecArgs} \"{output}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            StandardErrorEncoding = Encoding.UTF8,
        };
        proc.EnableRaisingEvents = true;

        double durationSec = 0;
        proc.ErrorDataReceived += (_, e) =>
        {
            if (string.IsNullOrEmpty(e.Data)) return;
            var line = e.Data;
            try
            {
                if (line.Contains("Duration:", StringComparison.Ordinal))
                {
                    var m = Regex.Match(line, @"Duration:\s*(\d+):(\d+):([\d.]+)");
                    if (m.Success && int.TryParse(m.Groups[1].Value, out var h)
                        && int.TryParse(m.Groups[2].Value, out var min)
                        && double.TryParse(m.Groups[3].Value, System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out var sec))
                    {
                        durationSec = h * 3600 + min * 60 + sec;
                    }
                }
                var tm = Regex.Match(line, @"time=(\d+):(\d+):([\d.]+)");
                if (tm.Success && int.TryParse(tm.Groups[1].Value, out var th)
                    && int.TryParse(tm.Groups[2].Value, out var tmin)
                    && double.TryParse(tm.Groups[3].Value, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var tsec))
                {
                    var cur = th * 3600 + tmin * 60 + tsec;
                    if (durationSec > 0) progress?.Report(Math.Clamp(cur / durationSec, 0, 0.995));
                }
            }
            catch { }
        };

        proc.Exited += (_, _) =>
        {
            if (proc.ExitCode == 0 && File.Exists(output))
            {
                progress?.Report(1.0);
                tcs.TrySetResult(output);
            }
            else
            {
                tcs.TrySetException(new InvalidOperationException($"ffmpeg 退出码 {proc.ExitCode}。请确认输入格式受支持、目标格式所需编码器可用。"));
            }
        };

        using var reg = ct.Register(() =>
        {
            try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { }
            tcs.TrySetCanceled(ct);
        });

        proc.Start();
        proc.BeginErrorReadLine();
        proc.BeginOutputReadLine();
        return await tcs.Task;
    }

    // ========== NCM → MP3/FLAC ==========

    /// <summary>转换 .ncm 为 .mp3 / .flac（个人格式转换；输出目录缺省为源文件所在目录）。</summary>
    public async Task<NcmConvertResult> ConvertNcmAsync(string ncmPath, string? outputDir = null, CancellationToken ct = default)
    {
        _logger.Information("NCM 转换：{Path} -> {Dir}", ncmPath, outputDir ?? "(源目录)");
        return await NcmConverter.ConvertAsync(ncmPath, outputDir, ct);
    }

    // ========== HTTP Range 分块下载 ==========

    public async Task<string> DownloadHttpRangeAsync(string url, string savePath, int concurrency = 64, long chunkSize = 4 * 1024 * 1024, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(url)) throw new ArgumentException("URL 不能为空", nameof(url));
        if (concurrency < 1) concurrency = 1;
        if (chunkSize < 64 * 1024) chunkSize = 64 * 1024;
        _logger.Information("Range 下载：{Url} -> {Save} 并发={C} 块={ChunkSize}", url, savePath, concurrency, chunkSize);
        Directory.CreateDirectory(Path.GetDirectoryName(savePath) ?? ".");

        // HEAD / GET 0-0 拿 Content-Length 与 Accept-Ranges
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 0);
        using var resp0 = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        long total = 0;
        bool supportsRange = resp0.Headers.Contains("Content-Range");
        if (supportsRange)
        {
            var cr = resp0.Content.Headers.ContentRange;
            total = cr?.Length ?? 0;
        }
        if (total <= 0)
        {
            // 退化：单线程直下
            _logger.Information("服务器不支持 Range，回退为整文件下载…");
            progress?.Report(0);
            using var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            resp.EnsureSuccessStatusCode();
            total = resp.Content.Headers.ContentLength ?? 0;
            await using var fs = File.Create(savePath);
            var buf = new byte[128 * 1024];
            long received = 0;
            var s = await resp.Content.ReadAsStreamAsync(ct);
            int n;
            while ((n = await s.ReadAsync(buf, 0, buf.Length, ct)) > 0)
            {
                await fs.WriteAsync(buf, 0, n, ct);
                received += n;
                if (total > 0) progress?.Report(Math.Clamp((double)received / total, 0, 1));
            }
            if (total <= 0) progress?.Report(1.0);
            return savePath;
        }

        // 分块
        var chunks = new List<(long start, long end)>();
        for (long pos = 0; pos < total; pos += chunkSize + 1)
        {
            var end = Math.Min(pos + chunkSize, total - 1);
            chunks.Add((pos, end));
        }
        progress?.Report(0);

        var tmpDir = Path.Combine(Path.GetTempPath(), $"aitoolbox_dl_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmpDir);
        try
        {
            using var sem = new SemaphoreSlim(concurrency, concurrency);
            long totalReceived = 0;
            var tasks = chunks.Select(async c =>
            {
                await sem.WaitAsync(ct);
                try
                {
                    var idx = chunks.IndexOf(c);
                    var chunkPath = Path.Combine(tmpDir, $"chunk_{idx:000000}.part");
                    using var req2 = new HttpRequestMessage(HttpMethod.Get, url);
                    req2.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(c.start, c.end);
                    using var resp = await _http.SendAsync(req2, HttpCompletionOption.ResponseContentRead, ct);
                    resp.EnsureSuccessStatusCode();
                    var data = await resp.Content.ReadAsByteArrayAsync(ct);
                    await File.WriteAllBytesAsync(chunkPath, data, ct);
                    Interlocked.Add(ref totalReceived, data.Length);
                    progress?.Report(Math.Clamp((double)totalReceived / total, 0, 0.999));
                    return (idx, chunkPath, size: data.Length);
                }
                finally
                {
                    sem.Release();
                }
            });
            var results = await Task.WhenAll(tasks);
            // 合并
            await using var fs = File.Create(savePath);
            foreach (var (idx, chunkPath, _) in results.OrderBy(r => r.idx))
            {
                await using var chunk = File.OpenRead(chunkPath);
                await chunk.CopyToAsync(fs, 81920, ct);
            }
            progress?.Report(1.0);
            _logger.Information("Range 下载并合并完成：{Save}，{Len} B", savePath, total);
            return savePath;
        }
        finally
        {
            try { Directory.Delete(tmpDir, true); } catch { }
        }
    }
}
