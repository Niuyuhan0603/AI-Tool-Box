using Serilog;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace AI_Tool_Box.Services;

/// <summary>音视频 AI 总结服务契约：ffmpeg 抽取音频 → 转写 → AI 分段总结。</summary>
public interface IMediaSummaryService
{
    /// <summary>用 ffmpeg 抽取为 16kHz 单声道 MP3（适合语音转写上传），返回音频路径。</summary>
    Task<string> ExtractAudioAsync(string input, IProgress<double>? progress = null, CancellationToken ct = default);

    /// <summary>把转写文本交给 AI 总结（长文本自动分段再汇总）。</summary>
    Task<string> SummarizeAsync(string transcript, string focus, IProgress<string>? status = null, CancellationToken ct = default);
}

/// <summary>音视频 AI 总结实现。</summary>
public sealed class MediaSummaryService : IMediaSummaryService
{
    private const int ChunkSize = 6000;

    private readonly ILogger _logger;
    private readonly FfmpegLocator _ffmpeg;
    private readonly IAiQuickService _ai;

    /// <summary>构造：注入 ffmpeg 定位器与 AI 服务。</summary>
    public MediaSummaryService(ILogger logger, FfmpegLocator ffmpeg, IAiQuickService ai)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _ffmpeg = ffmpeg ?? throw new ArgumentNullException(nameof(ffmpeg));
        _ai = ai ?? throw new ArgumentNullException(nameof(ai));
    }

    /// <summary>抽取音频的临时目录：%TEMP%\AI-Tool-Box\summary\。</summary>
    public static string TempDir => Path.Combine(Path.GetTempPath(), "AI-Tool-Box", "summary");

    /// <inheritdoc/>
    public async Task<string> ExtractAudioAsync(string input, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        if (!File.Exists(input)) throw new FileNotFoundException("媒体文件不存在：" + input);
        var ffmpeg = _ffmpeg.Resolve()
            ?? throw new FileNotFoundException("未检测到 ffmpeg.exe，请到「文件转换」页设置 FFmpeg 路径，或安装并加入 PATH。");

        Directory.CreateDirectory(TempDir);
        var output = Path.Combine(TempDir, $"{Path.GetFileNameWithoutExtension(input)}-{DateTime.Now:HHmmss}.mp3");

        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var proc = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = ffmpeg,
                Arguments = $"-hide_banner -nostdin -y -i \"{input}\" -vn -ac 1 -ar 16000 -c:a libmp3lame -b:a 64k \"{output}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                StandardErrorEncoding = Encoding.UTF8,
            },
            EnableRaisingEvents = true,
        };

        double durationSec = 0;
        proc.ErrorDataReceived += (_, e) =>
        {
            if (string.IsNullOrEmpty(e.Data)) return;
            var line = e.Data;
            try
            {
                var dm = Regex.Match(line, @"Duration:\s*(\d+):(\d+):([\d.]+)");
                if (dm.Success)
                {
                    durationSec = int.Parse(dm.Groups[1].Value) * 3600 + int.Parse(dm.Groups[2].Value) * 60 +
                                  double.Parse(dm.Groups[3].Value, CultureInfo.InvariantCulture);
                }
                var tm = Regex.Match(line, @"time=(\d+):(\d+):([\d.]+)");
                if (tm.Success && durationSec > 0)
                {
                    var cur = int.Parse(tm.Groups[1].Value) * 3600 + int.Parse(tm.Groups[2].Value) * 60 +
                              double.Parse(tm.Groups[3].Value, CultureInfo.InvariantCulture);
                    progress?.Report(Math.Clamp(cur / durationSec, 0, 0.995));
                }
            }
            catch { /* 忽略解析异常 */ }
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
                tcs.TrySetException(new InvalidOperationException($"ffmpeg 退出码 {proc.ExitCode}，无法抽取音频。"));
            }
        };

        using var reg = ct.Register(() =>
        {
            try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { }
            tcs.TrySetCanceled(ct);
        });

        _logger.Information("抽取音频：{Input} -> {Output}", input, output);
        proc.Start();
        proc.BeginErrorReadLine();
        proc.BeginOutputReadLine();
        return await tcs.Task;
    }

    /// <inheritdoc/>
    public async Task<string> SummarizeAsync(string transcript, string focus, IProgress<string>? status = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(transcript)) throw new ArgumentException("转写文本为空。", nameof(transcript));

        var style = string.IsNullOrWhiteSpace(focus) ? "通用" : focus.Trim();
        var sb = new StringBuilder();

        if (transcript.Length <= ChunkSize)
        {
            status?.Report("AI 总结中…");
            return await _ai.AskAsync(
                $"你是一名专业的内容整理助手。请用中文对以下音视频转写稿做{style}总结，输出 Markdown：先给 3–6 条要点，再给一份结构化大纲（含时间线不要求）。不要编造原文没有的信息。",
                transcript, ct: ct);
        }

        var chunks = new List<string>();
        for (var i = 0; i < transcript.Length; i += ChunkSize)
            chunks.Add(transcript.Substring(i, Math.Min(ChunkSize, transcript.Length - i)));

        for (var i = 0; i < chunks.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            status?.Report($"分段总结 {i + 1}/{chunks.Count}…");
            var part = await _ai.AskAsync(
                $"你是内容整理助手。这是长转写稿的第 {i + 1}/{chunks.Count} 段，请用中文提炼该段的核心信息（200 字以内，Markdown 列表）。",
                chunks[i], ct: ct);
            sb.AppendLine($"## 第 {i + 1} 段").AppendLine(part).AppendLine();
        }

        status?.Report("汇总全篇…");
        var merged = await _ai.AskAsync(
            $"你是内容整理助手。下面是长音视频转写稿的分段摘要，请合并为一份{style}总结（Markdown）：总览 → 关键要点 → 结论/待办。去除重复、保留数字与专有名词。",
            sb.ToString(), ct: ct);
        return merged;
    }
}
