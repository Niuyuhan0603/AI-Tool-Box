using Serilog;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace AI_Tool_Box.Services;

/// <summary>
/// Ollama 运行时管理：定位本地 ollama.exe、一键 <c>ollama serve</c>、<c>ollama pull</c> 拉取模型、删除模型。
/// 仅调用外部可执行文件，不内置 Ollama。
/// </summary>
public sealed class OllamaRuntimeService : IOllamaRuntimeService
{
    private const string DefaultBaseUrl = "http://localhost:11434";
    private static readonly TimeSpan ReadyTimeout = TimeSpan.FromSeconds(20);

    private readonly ILogger _logger;
    private readonly HttpClient _http;
    private Process? _serveProcess;
    private string? _exePath;

    /// <summary>初始化服务并读取上次保存的自定义 ollama.exe 路径。</summary>
    public OllamaRuntimeService(ILogger logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        _exePath = LoadSavedPath();
    }

    /// <summary>用户设置文件路径。</summary>
    private static string PathFile => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AI-Tool-Box", "ollama_path.txt");

    /// <inheritdoc/>
    public string? ExecutablePath => _exePath is not null && File.Exists(_exePath) ? _exePath : Detect();

    /// <inheritdoc/>
    public bool IsServeProcessRunning => _serveProcess is { HasExited: false };

    /// <inheritdoc/>
    public void SetExecutablePath(string path)
    {
        _exePath = string.IsNullOrWhiteSpace(path) ? null : path.Trim();
        try
        {
            var dir = System.IO.Path.GetDirectoryName(PathFile)!;
            Directory.CreateDirectory(dir);
            File.WriteAllText(PathFile, _exePath ?? string.Empty, Encoding.UTF8);
            _logger.Information("Ollama 可执行文件路径已保存：{Path}", _exePath);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "保存 Ollama 路径失败（忽略）");
        }
    }

    /// <inheritdoc/>
    public string? Detect()
    {
        // 1) 用户指定
        if (!string.IsNullOrWhiteSpace(_exePath) && File.Exists(_exePath)) return _exePath;

        // 2) PATH 环境变量
        var pathVar = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var dir in pathVar.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = System.IO.Path.Combine(dir.Trim(), "ollama.exe");
                if (File.Exists(candidate)) return candidate;
            }
            catch { /* 非法路径忽略 */ }
        }

        // 3) 常见安装位置
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var progFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string?[] common =
        {
            System.IO.Path.Combine(local, "Programs", "Ollama", "ollama.exe"),
            System.IO.Path.Combine(progFiles, "Ollama", "ollama.exe"),
            // 4) 随应用放置（可选）
            System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "ollama", "ollama.exe"),
        };
        foreach (var c in common)
        {
            if (c is not null && File.Exists(c)) return c;
        }
        return null;
    }

    /// <inheritdoc/>
    public async Task<bool> IsServerRunningAsync(CancellationToken ct = default)
    {
        try
        {
            using var resp = await _http.GetAsync(DefaultBaseUrl + "/api/tags", ct);
            return resp.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    /// <inheritdoc/>
    public async Task<bool> StartServeAsync(CancellationToken ct = default)
    {
        if (await IsServerRunningAsync(ct)) return true;

        var exe = ExecutablePath;
        if (exe is null)
        {
            _logger.Warning("未检测到 ollama.exe，无法启动服务");
            return false;
        }

        try
        {
            if (_serveProcess is null || _serveProcess.HasExited)
            {
                var psi = new ProcessStartInfo(exe, "serve")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                _serveProcess = new Process { StartInfo = psi, EnableRaisingEvents = true };
                _serveProcess.OutputDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) _logger.Debug("ollama: {Line}", e.Data); };
                _serveProcess.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) _logger.Debug("ollama(err): {Line}", e.Data); };
                _serveProcess.Start();
                _serveProcess.BeginOutputReadLine();
                _serveProcess.BeginErrorReadLine();
                _logger.Information("已启动 ollama serve（PID={Pid}）", _serveProcess.Id);
            }

            // 等待 API 就绪
            var deadline = DateTime.UtcNow + ReadyTimeout;
            while (DateTime.UtcNow < deadline)
            {
                ct.ThrowIfCancellationRequested();
                if (await IsServerRunningAsync(ct)) return true;
                await Task.Delay(500, ct);
            }
            _logger.Warning("ollama serve 启动超时（{Sec}s）", ReadyTimeout.TotalSeconds);
            return false;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.Error(ex, "启动 ollama serve 失败");
            return false;
        }
    }

    /// <inheritdoc/>
    public void StopServe()
    {
        try
        {
            if (_serveProcess is { HasExited: false })
            {
                _serveProcess.Kill(entireProcessTree: true);
                _serveProcess.WaitForExit(3000);
                _logger.Information("已停止由本应用启动的 ollama serve");
            }
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "停止 ollama serve 失败");
        }
        finally
        {
            _serveProcess?.Dispose();
            _serveProcess = null;
        }
    }

    /// <inheritdoc/>
    public async Task<bool> PullModelAsync(string model, IProgress<string>? output = null, CancellationToken ct = default)
    {
        var exe = ExecutablePath;
        if (exe is null)
        {
            output?.Report("未检测到 ollama.exe，请先安装 Ollama 或指定路径。");
            return false;
        }
        if (string.IsNullOrWhiteSpace(model))
        {
            output?.Report("模型名称不能为空。");
            return false;
        }

        try
        {
            var psi = new ProcessStartInfo(exe, $"pull {model.Trim()}")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            using var p = new Process { StartInfo = psi };
            p.OutputDataReceived += (_, e) => { if (!string.IsNullOrEmpty(e.Data)) output?.Report(e.Data); };
            p.ErrorDataReceived += (_, e) => { if (!string.IsNullOrEmpty(e.Data)) output?.Report(e.Data); };
            p.Start();
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();

            using var reg = ct.Register(() => { try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { } });
            await p.WaitForExitAsync(ct);
            var ok = p.ExitCode == 0;
            _logger.Information("ollama pull {Model} 结束，ExitCode={Code}", model, p.ExitCode);
            return ok;
        }
        catch (OperationCanceledException)
        {
            output?.Report("已取消拉取。");
            return false;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "拉取模型失败：{Model}", model);
            output?.Report($"拉取失败：{ex.Message}");
            return false;
        }
    }

    /// <inheritdoc/>
    public async Task<bool> DeleteModelAsync(string model, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(model)) return false;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Delete, DefaultBaseUrl + "/api/delete")
            {
                Content = new StringContent(JsonSerializer.Serialize(new { model }), Encoding.UTF8, "application/json")
            };
            using var resp = await _http.SendAsync(req, ct);
            if (resp.IsSuccessStatusCode)
            {
                _logger.Information("已删除模型（HTTP）：{Model}", model);
                return true;
            }
            _logger.Warning("HTTP 删除模型返回 {Code}：{Model}", (int)resp.StatusCode, model);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "HTTP 删除模型失败，回退 CLI：{Model}", model);
        }

        // CLI 回退
        var exe = ExecutablePath;
        if (exe is null) return false;
        try
        {
            var psi = new ProcessStartInfo(exe, $"rm {model.Trim()}")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var p = Process.Start(psi);
            if (p is null) return false;
            using var reg = ct.Register(() => { try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { } });
            await p.WaitForExitAsync(ct);
            var ok = p.ExitCode == 0;
            _logger.Information("ollama rm {Model} 结束，ExitCode={Code}", model, p.ExitCode);
            return ok;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "删除模型失败：{Model}", model);
            return false;
        }
    }

    private static string? LoadSavedPath()
    {
        try
        {
            return File.Exists(PathFile) ? File.ReadAllText(PathFile, Encoding.UTF8).Trim() : null;
        }
        catch
        {
            return null;
        }
    }
}
