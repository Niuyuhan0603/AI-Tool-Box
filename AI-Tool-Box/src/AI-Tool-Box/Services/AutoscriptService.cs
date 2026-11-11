using CommunityToolkit.Mvvm.Messaging;
using AI_Tool_Box.ViewModels;
using MoonSharp.Interpreter;
using MoonSharp.Interpreter.Interop;
using Serilog;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace AI_Tool_Box.Services;

/// <summary>Autoscript 执行结果。</summary>
public sealed record AutoscriptResult(bool Success, string? ReturnValue, string? Error);

/// <summary>Autoscript：Lua 脚本运行时（MoonSharp 硬沙箱）+ 有限工具箱 API + 脚本加密打包。</summary>
public interface IAutoscriptService
{
    /// <summary>执行 Lua 脚本（硬沙箱：无 io/os 直接访问，仅暴露 toolbox:* API）。</summary>
    Task<AutoscriptResult> RunAsync(string code, Action<string> log, CancellationToken ct = default);

    /// <summary>加密脚本（ATS1 格式：AES-256-CBC + 随机 IV）。</summary>
    string Protect(string code);

    /// <summary>解密脚本；若非 ATS1 格式则原样返回。</summary>
    string Unprotect(string content);
}

/// <summary>Autoscript 实现。</summary>
public sealed class AutoscriptService : IAutoscriptService
{
    private readonly ILogger _logger;
    private readonly IAiQuickService _ai;
    private readonly IMessenger _messenger;

    /// <summary>脚本加密固定密钥（仅做混淆，避免明文分发；非机密保护）。</summary>
    private static readonly byte[] Key =
    {
        0x41, 0x49, 0x2D, 0x54, 0x6F, 0x6F, 0x6C, 0x2D, 0x42, 0x6F, 0x78, 0x2D, 0x41, 0x75, 0x74, 0x6F,
        0x73, 0x63, 0x72, 0x69, 0x70, 0x74, 0x2D, 0x4B, 0x65, 0x79, 0x2D, 0x32, 0x30, 0x32, 0x36, 0x21,
    };

    /// <summary>构造：注入日志、AI 服务与消息总线（用于 toolbox.navigate）。</summary>
    public AutoscriptService(ILogger logger, IAiQuickService ai, IMessenger messenger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _ai = ai ?? throw new ArgumentNullException(nameof(ai));
        _messenger = messenger ?? throw new ArgumentNullException(nameof(messenger));
    }

    /// <inheritdoc/>
    public Task<AutoscriptResult> RunAsync(string code, Action<string> log, CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            try
            {
                UserData.RegistrationPolicy = InteropRegistrationPolicy.Automatic;
                var script = new Script(CoreModules.Preset_HardSandbox);
                var toolbox = BuildToolbox(script, log, ct);
                script.Globals["toolbox"] = toolbox;
                var result = script.DoString(code ?? string.Empty);
                var text = result?.Type == DataType.Nil || result is null ? null : result.ToPrintString();
                _logger.Information("Autoscript 执行完成，返回：{Result}", text);
                return new AutoscriptResult(true, text, null);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Autoscript 执行失败");
                return new AutoscriptResult(false, null, ex.Message);
            }
        }, ct);
    }

    /// <summary>构建 toolbox 表（全部为无 self 的普通函数）。</summary>
    private Table BuildToolbox(Script script, Action<string> log, CancellationToken ct)
    {
        var t = new Table(script);
        t["log"] = (Action<string>)(msg => log(msg ?? string.Empty));
        t["sleep"] = (Action<int>)(ms => Thread.Sleep(Math.Clamp(ms, 0, 30_000)));
        t["cancel_requested"] = (Func<bool>)(() => ct.IsCancellationRequested);
        t["navigate"] = (Action<string>)(tag => _messenger.Send(new NavigateToTagMessage(tag ?? string.Empty)));
        t["ai"] = (Func<string, string>)(prompt =>
        {
            log($"AI 请求：{(prompt ?? string.Empty).Length} 字");
            var answer = _ai.AskAsync("你是脚本助手，请直接给出可执行结果的简洁回答。", prompt ?? string.Empty)
                .GetAwaiter().GetResult();
            return answer;
        });
        t["file_read"] = (Func<string, string>)(path => File.ReadAllText(path));
        t["file_write"] = (Action<string, string>)((path, text) => File.WriteAllText(path, text ?? string.Empty, new UTF8Encoding(false)));
        t["file_exists"] = (Func<string, bool>)(path => File.Exists(path));
        t["http_get"] = (Func<string, string>)(url =>
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            return http.GetStringAsync(url).GetAwaiter().GetResult();
        });
        t["app_dir"] = (Func<string>)(() => AppContext.BaseDirectory);
        t["now"] = (Func<string>)(() => DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        return t;
    }

    /// <inheritdoc/>
    public string Protect(string code)
    {
        if (code is null) throw new ArgumentNullException(nameof(code));
        using var aes = Aes.Create();
        aes.Key = Key;
        aes.GenerateIV();
        var plain = Encoding.UTF8.GetBytes(code);
        using var encryptor = aes.CreateEncryptor();
        var cipher = encryptor.TransformFinalBlock(plain, 0, plain.Length);
        var payload = new byte[aes.IV.Length + cipher.Length];
        Buffer.BlockCopy(aes.IV, 0, payload, 0, aes.IV.Length);
        Buffer.BlockCopy(cipher, 0, payload, aes.IV.Length, cipher.Length);
        return "ATS1:" + Convert.ToBase64String(payload);
    }

    /// <inheritdoc/>
    public string Unprotect(string content)
    {
        if (string.IsNullOrEmpty(content) || !content.StartsWith("ATS1:", StringComparison.Ordinal)) return content;
        var payload = Convert.FromBase64String(content[5..]);
        using var aes = Aes.Create();
        aes.Key = Key;
        var iv = new byte[16];
        Buffer.BlockCopy(payload, 0, iv, 0, 16);
        aes.IV = iv;
        using var decryptor = aes.CreateDecryptor();
        var plain = decryptor.TransformFinalBlock(payload, 16, payload.Length - 16);
        return Encoding.UTF8.GetString(plain);
    }
}
