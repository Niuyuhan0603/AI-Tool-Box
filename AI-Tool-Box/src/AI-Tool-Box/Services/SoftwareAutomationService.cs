using Serilog;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace AI_Tool_Box.Services;

/// <summary>外部工程/3D 软件自动化执行结果。</summary>
public sealed record AutomationResult(int ExitCode, string Output);

/// <summary>工程/3D 软件自动化：把脚本写入临时文件并以命令行方式驱动外部软件（Blender/FreeCAD/AutoCAD/Python 等）。</summary>
public interface ISoftwareAutomationService
{
    /// <summary>运行脚本；arguments 中的 {script} 会替换为临时脚本路径。</summary>
    Task<AutomationResult> RunAsync(string exePath, string arguments, string scriptContent, string scriptExtension,
        Action<string> log, CancellationToken ct = default);
}

/// <summary>工程软件自动化实现。</summary>
public sealed class SoftwareAutomationService : ISoftwareAutomationService
{
    private readonly ILogger _logger;

    /// <summary>构造。</summary>
    public SoftwareAutomationService(ILogger logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc/>
    public async Task<AutomationResult> RunAsync(string exePath, string arguments, string scriptContent, string scriptExtension,
        Action<string> log, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
            throw new FileNotFoundException("未找到可执行文件，请指定正确路径：" + exePath);

        var ext = string.IsNullOrWhiteSpace(scriptExtension) ? ".py" : scriptExtension;
        var scriptPath = Path.Combine(Path.GetTempPath(), "AI-Tool-Box", "automation",
            $"script-{DateTime.Now:yyyyMMdd-HHmmss}{ext}");
        Directory.CreateDirectory(Path.GetDirectoryName(scriptPath)!);
        await File.WriteAllTextAsync(scriptPath, scriptContent ?? string.Empty, new UTF8Encoding(false), ct);
        log($"脚本已写入：{scriptPath}");

        var args = (arguments ?? string.Empty).Replace("{script}", scriptPath);
        log($"执行：\"{exePath}\" {args}");

        var output = new StringBuilder();
        var psi = new ProcessStartInfo
        {
            FileName = exePath,
            Arguments = args,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Path.GetDirectoryName(exePath) ?? Environment.CurrentDirectory,
        };

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) =>
        {
            if (string.IsNullOrEmpty(e.Data)) return;
            output.AppendLine(e.Data);
            log(e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (string.IsNullOrEmpty(e.Data)) return;
            output.AppendLine(e.Data);
            log("[err] " + e.Data);
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var reg = ct.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
        });

        await process.WaitForExitAsync(ct);
        _logger.Information("自动化执行结束：{Exe} ExitCode={Code}", exePath, process.ExitCode);
        log($"进程结束，退出码 {process.ExitCode}");
        return new AutomationResult(process.ExitCode, output.ToString());
    }
}
