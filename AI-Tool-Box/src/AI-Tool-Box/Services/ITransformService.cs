namespace AI_Tool_Box.Services;

/// <summary>文件转换（Word ↔ PDF；视频格式 → MP4；直链/m3u8 下载合并）。</summary>
public interface ITransformService
{
    // Word ↔ PDF 简易实现（纯文本提取 + 最小 PDF writer / DOCX writer；零第三方库）
    Task<string> DocxExtractTextAsync(string docxPath, CancellationToken ct = default);
    Task<string> DocxToHtmlAsync(string docxPath, CancellationToken ct = default);
    Task WriteDocxFromLinesAsync(string outDocx, IEnumerable<string> lines, string title = "", CancellationToken ct = default);
    Task<string> PdfExtractTextAsync(string pdfPath, CancellationToken ct = default);
    Task WriteSimplePdfFromLinesAsync(string outPdf, IEnumerable<string> lines, string title = "", CancellationToken ct = default);

    // FFmpeg（视频 → MP4；路径解析见 FfmpegLocator）
    string? LocateFfmpeg();
    Task<string> ConvertToMp4Async(string input, string output, IProgress<double>? progress = null, CancellationToken ct = default);

    /// <summary>通用音视频格式互转：按输出扩展名自动选择 ffmpeg 参数。</summary>
    Task<string> ConvertMediaAsync(string input, string output, IProgress<double>? progress = null, CancellationToken ct = default);

    // NCM（网易云加密音频）→ MP3/FLAC（个人格式转换；输出目录缺省为源文件所在目录）
    Task<NcmConvertResult> ConvertNcmAsync(string ncmPath, string? outputDir = null, CancellationToken ct = default);

    // 直链视频分块下载（HTTP Range）、m3u8 用 ISnifferService.DownloadAsync 复用
    Task<string> DownloadHttpRangeAsync(string url, string savePath, int concurrency = 64, long chunkSize = 4 * 1024 * 1024,
        IProgress<double>? progress = null, CancellationToken ct = default);
}
