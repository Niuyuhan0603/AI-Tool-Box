using AI_Tool_Box.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using Serilog;
using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AI_Tool_Box.Services;

/// <summary>媒体嗅探器实现（按用户要求直接集成 IDM Integration Module v6.42 核心嗅探代码）：
/// 1) WebView2.AddHostObjectToScript("snifferBridge", SnifferBridgeHost) 作为 chrome.runtime 同步宿主
/// 2) 常驻注入顺序：idm_bridge.js (chrome.runtime stub) → debug.js (IDM 原版 debug 工具)
///    → document.js (IDM 原版 XHR/fetch vnd.t1c.accept 注入) → content.js (IDM 原版
///    MutationObserver + 各视频站 DOM 嗅探 + port.postMessage 上报)
/// 3) 通道：chrome.webview.hostObjects.sync.snifferBridge.(ReportResource|Post) +
///    chrome.webview.postMessage JSON {type:'aitb-idm-res' / 'aitb-idm' / 'aitb-media'}
///    → OnWebMessageReceived 解析后分类入库
/// 4) 保留原有的 WebResourceRequested / WebResourceResponseReceived / ScanCurrentPageAsync
///    / BuildHookScript 兜底机制，防止 IDM 内容脚本因为网站反爬被禁用时嗅探器仍有基本能力
/// 5) DownloadAsync：单文件直下；m3u8 分片下载合并（AES-128/192/256 CBC/PKCS7 解密）
/// </summary>
public sealed class SnifferService : ISnifferService, IDisposable
{
    private static readonly string DefaultSaveDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "Downloads", "AI-Tool-Box", "Sniffer");

    /// <summary>IDM 嗅探脚本相对 Assets/Web/sniffer 的文件列表（严格按注入顺序）。</summary>
    private static readonly string[] IdmScriptOrder = new[]
    {
        "idm_bridge.js",   // 0: 本项目新增，MV2 扩展 API 桩 + document.js handshake + port→C# 翻译
        "debug.js",        // 1: IDM 原版，console.* 防反调试
        "document.js",     // 2: IDM 原版，XHR/fetch 加标 vnd.t1c.<rid> (握手后生效)
        "content.js"       // 3: IDM 原版，DOM 观察 + 多视频站指纹 + [opcode] port 上报
    };

    private readonly ILogger _logger;
    private readonly IWebShellService _webShell;
    private readonly DispatcherQueue _dispatcher;
    private readonly object _lock = new();
    private readonly HashSet<string> _seenUrls = new();
    /// <summary>同名（网页标题）资源的序号计数器，避免重名（用户需求 1）。</summary>
    private readonly Dictionary<string, int> _titleCounters = new(StringComparer.OrdinalIgnoreCase);
    // UseCookies=false：Cookie 由我们从 WebView2 CookieManager 手动注入（同一浏览器会话），
    // 否则站点防盗链/鉴权会因缺少 Cookie 而下载失败（用户需求 2）。
    private readonly HttpClient _http = new(new HttpClientHandler { AllowAutoRedirect = true, UseCookies = false, AutomaticDecompression = System.Net.DecompressionMethods.All })
    { Timeout = TimeSpan.FromMinutes(5) };
    private readonly SnifferBridgeHost _bridgeHost;

    /// <summary>下载请求默认 UA（与 WebView2 内核一致，避免部分站点按 UA 拦截）。</summary>
    private const string DefaultUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36 Edg/124.0.0.0";

    /// <summary>当前活动网页标题（用户需求 1：嗅探到的媒体显示名 = 网页标题）。</summary>
    private volatile string _currentPageTitle = string.Empty;
    private Windows.Foundation.TypedEventHandler<CoreWebView2, object>? _documentTitleChangedHandler;

    // WebView2 的 WinRT 事件使用 TypedEventHandler（第一泛型 = sender, 第二 = args）
    private Windows.Foundation.TypedEventHandler<CoreWebView2, CoreWebView2WebResourceRequestedEventArgs>? _webResourceRequestedHandler;
    private Windows.Foundation.TypedEventHandler<CoreWebView2, CoreWebView2WebResourceResponseReceivedEventArgs>? _responseReceivedHandler;
    private Windows.Foundation.TypedEventHandler<CoreWebView2, CoreWebView2NavigationCompletedEventArgs>? _navigationCompletedHandler;
    private Windows.Foundation.TypedEventHandler<CoreWebView2, CoreWebView2WebMessageReceivedEventArgs>? _webMessageReceivedHandler;
    private string? _cachedHookScript;   // 旧版纯手写 hook（保留，作为 IDM 脚本不可用时的 fallback）
    private string? _cachedIdmPackage;  // IDM 四脚本级联 IIFE 包装（避免每次构建再读磁盘）
    private CoreWebView2? _attachedCwv;
    private bool _started;

    public ObservableCollection<SnifferResourceItem> Resources { get; } = new();
    public ObservableCollection<SnifferDownloadTask> Downloads { get; } = new();

    public event EventHandler<SnifferResourceItem>? ResourceDetected;

    /// <inheritdoc/>
    public event EventHandler? OpenSnifferRequested;

    public SnifferService(ILogger logger, IWebShellService webShell, DispatcherQueue dispatcher)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _webShell = webShell ?? throw new ArgumentNullException(nameof(webShell));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _bridgeHost = new SnifferBridgeHost(HandleBridgeResource, HandleBridgePost, _logger);
    }

    /// <inheritdoc/>
    public void StartSniffing()
    {
        if (_started) return;
        _started = true;
        _ = AttachToActiveCoreAsync();
        _webShell.ActivePageChanged -= OnActivePageChanged;
        _webShell.ActivePageChanged += OnActivePageChanged;
        _logger.Information("嗅探器已启动");
    }

    /// <inheritdoc/>
    public void StopSniffing()
    {
        if (!_started) return;
        _started = false;
        _webShell.ActivePageChanged -= OnActivePageChanged;
        DetachFromCurrentCore();
        _logger.Information("嗅探器已停止");
    }

    private void OnActivePageChanged(object? sender, EventArgs e)
    {
        if (!_started) return;
        _ = AttachToActiveCoreAsync();
        _ = ScanCurrentPageAsync();
    }

    private async Task AttachToActiveCoreAsync()
    {
        try
        {
            var cwv = GetActiveCoreWebView2();
            if (cwv is null || cwv == _attachedCwv) return;

            DetachFromCurrentCore();

            // 关键修复：WebResourceRequested 只有在注册了过滤器后才会触发。
            // 之前用 "*.mp4" 这类非法模式导致事件从不触发 → 什么都嗅探不到。
            // 这里按 IDM 思路注册全量过滤器，在处理器内自行按扩展名/MIME 过滤。
            try
            {
                cwv.AddWebResourceRequestedFilter("*://*/*", CoreWebView2WebResourceContext.All);
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "设置 WebResourceRequested 过滤器失败");
            }

            // ===== 先 AddHostObjectToScript：idm_bridge.js 会通过
            //       chrome.webview.hostObjects.sync.snifferBridge.(ReportResource|Post)
            //       同步回传；WebMessageReceived 再作为异步通道兜底。
            try
            {
                cwv.AddHostObjectToScript("snifferBridge", _bridgeHost);
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "AddHostObjectToScript(snifferBridge) 失败（已降级：仅走 postMessage 通道）");
            }

            _webResourceRequestedHandler = OnWebResourceRequested;
            // 响应阶段：按 Content-Type / Content-Disposition 识别无扩展名的媒体（IDM 的 Response 判定思路）
            _responseReceivedHandler = OnWebResourceResponseReceived;
            // 用户需求 1：网页标题变化时记录，作为后续嗅探到的媒体显示名
            _currentPageTitle = cwv.DocumentTitle ?? string.Empty;
            _documentTitleChangedHandler = (s, _) =>
            {
                try { _currentPageTitle = s.DocumentTitle ?? string.Empty; }
                catch { /* 页面切换瞬间可能取不到标题 */ }
            };
            _navigationCompletedHandler = (s, e) =>
            {
                try { _currentPageTitle = s.DocumentTitle ?? _currentPageTitle; } catch { }
                if (_started) _ = ScanCurrentPageAsync();
            };
            // 网页内消息：'aitb-open-sniffer' 打开嗅探器；
            //   JSON {type:'aitb-media'}       = 旧版 hook fallback
            //   JSON {type:'aitb-idm'}         = IDM port 全量报文 (opcode+args)
            //   JSON {type:'aitb-idm-res'}     = IDM 桥接 cook好 的资源 (已解 URL/tag)
            //   JSON {type:'aitb-idm-ready'}   = 桥接握手确认
            _webMessageReceivedHandler = OnWebMessageReceived;
            cwv.WebResourceRequested += _webResourceRequestedHandler;
            cwv.WebResourceResponseReceived += _responseReceivedHandler;
            cwv.NavigationCompleted += _navigationCompletedHandler;
            cwv.WebMessageReceived += _webMessageReceivedHandler;
            cwv.DocumentTitleChanged += _documentTitleChangedHandler;

            // ===== 常驻注入脚本（用户要求"直接复制 IDM 的嗅探代码"）
            // 注入顺序：先 fallback 简化hook (BuildHookScript) → 再 IDM 四件套
            // 目的：如果某个站点因为 CSP / MV2 桩冲突导致 IDM content.js 抛异常，
            // 上半段旧版手写 hook 已经先把基础资源锁住。
            try
            {
                // 先注入 fallback
                var fallback = EnsureFallbackHookScript();
                await cwv.AddScriptToExecuteOnDocumentCreatedAsync(fallback);
                // 再注入 IDM 套件 (bridge + debug + document + content)
                var idmPkg = EnsureIdmPackageScript();
                await cwv.AddScriptToExecuteOnDocumentCreatedAsync(idmPkg);
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "注入常驻嗅探脚本失败（IDM 套件 + fallback）");
            }

            _attachedCwv = cwv;
            _logger.Information("嗅探器已挂钩当前活动 WebView2（IDM 脚本已注入）");
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "挂钩 WebView2 失败");
        }
    }

    /* ================== 脚本打包：fallback + IDM ================== */

    /// <summary>旧版纯手写 hook，作为 IDM 内容脚本冲突/缺失时的兜底。</summary>
    private string EnsureFallbackHookScript()
    {
        if (_cachedHookScript is not null) return _cachedHookScript;
        _cachedHookScript = BuildHookScript();
        return _cachedHookScript;
    }

    /// <summary>
    /// 从 Assets/Web/sniffer/*.js 读入 IDM 四脚本，拼接成 ";(function(){...})();"
    /// 级联的单字符串。
    /// 若某文件缺失（比如开发机上误删），返回空串（上面 fallback 已先保证基本能力）。
    /// </summary>
    private string EnsureIdmPackageScript()
    {
        if (_cachedIdmPackage is not null) return _cachedIdmPackage;

        var sb = new StringBuilder(80 * 1024);
        string baseDir = Path.Combine(AppContext.BaseDirectory, "Assets", "Web", "sniffer");
        if (!Directory.Exists(baseDir))
        {
            // 开发态构建前，可能 bin 尚未复制 Assets——也尝试直接从项目源码目录读
            baseDir = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Assets", "Web", "sniffer");
            if (!Directory.Exists(baseDir))
            {
                _logger.Warning("找不到 Assets/Web/sniffer 目录（已降级：仅使用 fallback hook）。baseDir1={Base1}",
                    Path.Combine(AppContext.BaseDirectory, "Assets", "Web", "sniffer"));
                _cachedIdmPackage = string.Empty;
                return _cachedIdmPackage;
            }
        }

        int loaded = 0;
        foreach (var name in IdmScriptOrder)
        {
            var file = Path.Combine(baseDir, name);
            if (!File.Exists(file))
            {
                _logger.Warning("IDM 嗅探脚本缺失：{File}", file);
                continue;
            }
            try
            {
                var text = File.ReadAllText(file);
                // content.js / document.js 以 BOM+UTF-8 存储；ReadAllText 会自动剥 BOM。
                sb.Append(";(function(){ /* ---- ").Append(name).Append(" ---- */ ").Append('\n');
                sb.Append(text).Append("\n})();\n");
                loaded++;
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "读取 IDM 嗅探脚本失败：{File}", file);
            }
        }

        if (loaded == 0)
        {
            _logger.Warning("IDM 嗅探脚本全部未加载（已降级：仅使用 fallback hook）");
            _cachedIdmPackage = string.Empty;
        }
        else
        {
            _logger.Information("IDM 嗅探脚本加载完成：{Count}/{Total} 文件，总大小 {Len} chars",
                loaded, IdmScriptOrder.Length, sb.Length);
            _cachedIdmPackage = sb.ToString();
        }
        return _cachedIdmPackage;
    }

    /// <summary>构建常驻注入脚本：媒体元素观察 + fetch/XHR 包装，发现媒体 URL 即 postMessage 上报。
    /// 作为 IDM 原版脚本不可用情况下的 fallback。</summary>
    private static string BuildHookScript() => """
(function () {
    if (window.__aitbSniffHooked) return;
    window.__aitbSniffHooked = true;
    function post(u, tag) {
        try {
            if (!u || typeof u !== 'string') return;
            if (u.indexOf('data:') === 0) return;
            window.chrome.webview.postMessage(JSON.stringify({ type: 'aitb-media', url: u, tag: tag || '' }));
        } catch (e) {}
    }
    function scanEl(el) {
        try {
            if (!el || el.nodeType !== 1) return;
            var t = (el.tagName || '').toUpperCase();
            if (t === 'VIDEO' || t === 'AUDIO' || t === 'SOURCE' || t === 'EMBED' || t === 'OBJECT' || t === 'TRACK') {
                var s = el.currentSrc || el.src || el.getAttribute('data-src');
                if (s) post(s, t);
            }
            if (el.querySelectorAll) {
                el.querySelectorAll('video,audio,source,embed,object,track').forEach(function (m) {
                    var s = m.currentSrc || m.src || m.getAttribute('data-src');
                    if (s) post(s, m.tagName);
                });
            }
        } catch (e) {}
    }
    scanEl(document.documentElement);
    try {
        new MutationObserver(function (muts) {
            muts.forEach(function (m) {
                if (m.addedNodes) m.addedNodes.forEach(function (n) { scanEl(n); });
            });
        }).observe(document.documentElement || document, { childList: true, subtree: true });
    } catch (e) {}
    try {
        var of = window.fetch;
        if (of) {
            window.fetch = function () {
                try {
                    var a0 = arguments[0];
                    post(typeof a0 === 'string' ? a0 : (a0 && a0.url), 'FETCH');
                } catch (e) {}
                return of.apply(this, arguments);
            };
        }
    } catch (e) {}
    try {
        var oo = XMLHttpRequest.prototype.open;
        XMLHttpRequest.prototype.open = function (m, u) {
            try { post(u, 'XHR'); } catch (e) {}
            return oo.apply(this, arguments);
        };
    } catch (e) {}
})();
""";

    private void DetachFromCurrentCore()
    {
        var cwv = _attachedCwv;
        if (cwv is not null)
        {
            try
            {
                if (_webResourceRequestedHandler is not null)
                    cwv.WebResourceRequested -= _webResourceRequestedHandler;
            }
            catch { }
            try
            {
                if (_responseReceivedHandler is not null)
                    cwv.WebResourceResponseReceived -= _responseReceivedHandler;
            }
            catch { }
            try
            {
                if (_navigationCompletedHandler is not null)
                    cwv.NavigationCompleted -= _navigationCompletedHandler;
            }
            catch { }
            try
            {
                if (_webMessageReceivedHandler is not null)
                    cwv.WebMessageReceived -= _webMessageReceivedHandler;
            }
            catch { }
            try
            {
                if (_documentTitleChangedHandler is not null)
                    cwv.DocumentTitleChanged -= _documentTitleChangedHandler;
            }
            catch { }
        }
        _webResourceRequestedHandler = null;
        _responseReceivedHandler = null;
        _navigationCompletedHandler = null;
        _webMessageReceivedHandler = null;
        _documentTitleChangedHandler = null;
        _attachedCwv = null;
    }

    private CoreWebView2? GetActiveCoreWebView2()
    {
        if (_webShell is WebShellService wss && wss.Shell is WebShell ws)
        {
            // 通过反射访问私有 _active（避免公开内部字段）
            var f = typeof(WebShell).GetField("_active", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            if (f?.GetValue(ws) is WebView2 wv) return wv.CoreWebView2;
        }
        return null;
    }

    private void OnWebResourceRequested(CoreWebView2 sender, CoreWebView2WebResourceRequestedEventArgs args)
    {
        try
        {
            var uri = args.Request?.Uri;
            if (string.IsNullOrEmpty(uri)) return;
            // 全量过滤器下会收到海量请求：只认媒体扩展名；.ts 分片在请求层跳过（避免 HLS 分片刷屏）
            ClassifyByUrl(uri, out var ext);
            if (ext.Equals("ts", StringComparison.OrdinalIgnoreCase)) return;
            var kind = ClassifyMimeOrUrl(uri, mediaType: string.Empty);
            if (kind == SnifferResourceKind.Unknown) return;
            var pageUrl = sender.Source ?? string.Empty;
            string mime = args.Request?.Headers?.GetHeader("Accept") ?? string.Empty;
            // WebResourceRequested 阶段尚未拿到 Content-Length，记 0
            AddResource(uri, kind, Path.GetFileName(new Uri(uri).AbsolutePath), mime, 0, pageUrl);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "处理 WebResourceRequested 异常");
        }
    }

    private void OnWebResourceResponseReceived(CoreWebView2 sender, CoreWebView2WebResourceResponseReceivedEventArgs args)
    {
        try
        {
            var uri = args.Request?.Uri;
            if (string.IsNullOrEmpty(uri)) return;
            string mediaType = string.Empty, disposition = string.Empty, lengthText = string.Empty;
            try { mediaType = args.Response?.Headers?.GetHeader("Content-Type") ?? string.Empty; } catch { }
            try { disposition = args.Response?.Headers?.GetHeader("Content-Disposition") ?? string.Empty; } catch { }
            try { lengthText = args.Response?.Headers?.GetHeader("Content-Length") ?? string.Empty; } catch { }
            var kind = ClassifyMimeOrUrl(uri, mediaType, disposition);
            if (kind == SnifferResourceKind.Unknown) return;
            ClassifyByUrl(uri, out var ext);
            if (ext.Equals("ts", StringComparison.OrdinalIgnoreCase)) return; // 分片刷屏
            long size = long.TryParse(lengthText, out var l) ? l : 0;
            var pageUrl = sender.Source ?? string.Empty;
            var headersSummary = BuildHeadersSummary(mediaType, lengthText, disposition);
            AddResource(uri, kind, Path.GetFileName(new Uri(uri).AbsolutePath), mediaType, size, pageUrl,
                headersSummary: headersSummary);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "处理 WebResourceResponseReceived 异常");
        }
    }

    private void OnWebMessageReceived(CoreWebView2 sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            var msg = e.TryGetWebMessageAsString();
            if (msg == "aitb-open-sniffer")
            {
                _dispatcher.TryEnqueue(() => OpenSnifferRequested?.Invoke(this, EventArgs.Empty));
                return;
            }
            if (string.IsNullOrEmpty(msg) || !msg.StartsWith('{')) return;

            using var doc = JsonDocument.Parse(msg);
            if (!doc.RootElement.TryGetProperty("type", out var tProp)) return;
            var type = tProp.GetString() ?? string.Empty;
            switch (type)
            {
                case "aitb-idm-ready":
                    _logger.Debug("IDM 桥接已就绪：{Url}", doc.RootElement.GetProperty("pageUrl").GetString());
                    return;
                case "aitb-idm":
                    // 原始 port 报文：{type:'aitb-idm', opcode, args:[...], meta:{cid,...}, pageUrl}
                    // 目前仅做 Debug 日志保留；真正入库的是 aitb-idm-res（桥接端已 cook 过 URL/tag）
                    if (_logger.IsEnabled(Serilog.Events.LogEventLevel.Verbose))
                    {
                        var op = doc.RootElement.GetProperty("opcode").GetInt32();
                        var pUrl = doc.RootElement.GetProperty("pageUrl").GetString() ?? string.Empty;
                        _logger.Verbose("IDM port→C#：op={Op} page={Page} raw={Raw}",
                            op, pUrl, msg.Length <= 512 ? msg : msg[..512]);
                    }
                    return;
                case "aitb-idm-res":
                    {
                        // bridge 端已经把 URL/tag 翻译成最通用的形式
                        var url = doc.RootElement.GetProperty("url").GetString() ?? string.Empty;
                        var tag = doc.RootElement.GetProperty("tag").GetString() ?? string.Empty;
                        var origin = doc.RootElement.TryGetProperty("origin", out var oProp) ? (oProp.GetString() ?? string.Empty) : "idm";
                        var pageUrl = doc.RootElement.TryGetProperty("pageUrl", out var pProp) ? (pProp.GetString() ?? string.Empty)
                                                                                                : (sender.Source ?? string.Empty);
                        var title = doc.RootElement.TryGetProperty("title", out var tiProp) ? tiProp.GetString() : null;
                        if (string.IsNullOrEmpty(url)) return;
                        var kind = ClassifyMimeOrUrl(url, string.Empty);
                        ClassifyByUrl(url, out var ext);
                        if (ext.Equals("ts", StringComparison.OrdinalIgnoreCase)) return; // .ts 分片防刷屏
                        if (kind == SnifferResourceKind.Unknown)
                        {
                            // IDM 能嗅探到但我们的 ClassifyMimeOrUrl 没识别 → 至少按直链通用下载分类
                            if (ext.Length == 0) return; // 完全没有扩展名的未知请求丢弃
                            kind = SnifferResourceKind.Video; // 当作视频候选
                            _logger.Debug("IDM 上报资源类型不明确，按 Video 兜底：{Url}", url);
                        }
                        string name;
                        try { name = string.IsNullOrWhiteSpace(title) ? Path.GetFileName(new Uri(url).AbsolutePath) : title!; }
                        catch { name = Path.GetFileName(url); }
                        AddResource(url, kind, name, string.Empty, 0, pageUrl,
                            tagHint: tag, origin: origin);
                    }
                    return;
                case "aitb-media":
                    {
                        var item = JsonSerializer.Deserialize<HookReportItem>(msg);
                        if (item is { Type: "aitb-media" } && !string.IsNullOrEmpty(item.Url))
                        {
                            var kind = ClassifyMimeOrUrl(item.Url, string.Empty);
                            if (kind == SnifferResourceKind.Unknown) return;
                            ClassifyByUrl(item.Url, out var ext);
                            if (ext.Equals("ts", StringComparison.OrdinalIgnoreCase)) return;
                            AddResource(item.Url, kind, Path.GetFileName(new Uri(item.Url).AbsolutePath),
                                string.Empty, 0, sender.Source ?? string.Empty, tagHint: item.Tag, origin: "aitb-media");
                        }
                    }
                    return;
            }
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "处理网页嗅探消息失败");
        }
    }

    private sealed class HookReportItem
    {
        public string Type { get; set; } = string.Empty;
        public string Url { get; set; } = string.Empty;
        public string Tag { get; set; } = string.Empty;
    }

    /// <summary>按 URL 扩展名 + MIME/Content-Disposition 综合判定资源类型（IDM 的多信号判定思路）。</summary>
    private static SnifferResourceKind ClassifyMimeOrUrl(string url, string mediaType, string? disposition = null)
    {
        var byUrl = ClassifyByUrl(url, out _);
        if (byUrl != SnifferResourceKind.Unknown) return byUrl;
        var mime = (mediaType ?? string.Empty).Split(';')[0].Trim().ToLowerInvariant();
        if (mime.Length > 0)
        {
            if (mime.Contains("mpegurl") || mime.Contains("dash+xml")) return SnifferResourceKind.M3u8;
            if (mime.StartsWith("video/")) return SnifferResourceKind.Video;
            if (mime.StartsWith("audio/")) return SnifferResourceKind.Audio;
            if (mime.Contains("vtt") || mime.Contains("srt") || mime.Contains("ass")) return SnifferResourceKind.Subtitle;
        }
        if (!string.IsNullOrEmpty(disposition) && disposition.Contains("attachment", StringComparison.OrdinalIgnoreCase))
        {
            // attachment 下载且扩展名为媒体 → 按扩展名归类（ClassifyByUrl 已处理）；此处不再兜底
        }
        return SnifferResourceKind.Unknown;
    }

    /// <inheritdoc/>
    public async Task<int> ScanCurrentPageAsync()
    {
        if (!_webShell.IsAvailable) return 0;
        try
        {
            var script = @"
(function () {
    var result = [];
    function add(u, tag) {
        if (!u) return;
        try { u = new URL(u, location.href).href; } catch(e) { return; }
        if (u.indexOf('data:') === 0 || u.indexOf('blob:') === 0) return;
        result.push({ url: u, tag: tag || '' });
    }
    document.querySelectorAll('video,audio,source,iframe,embed,object,track').forEach(function (el) {
        var src = el.currentSrc || el.src || el.getAttribute('data-src');
        if (src) add(src, el.tagName);
    });
    // performance 资源时间线：能捕获 XHR/fetch 拉取的 m3u8/mpd 等动态资源
    try {
        performance.getEntriesByType('resource').forEach(function (e) { add(e.name, 'PERF'); });
    } catch (e) {}
    // 扫 innerHTML 中 http(s) 链接匹配媒体后缀
    var html = document.documentElement.outerHTML || '';
    var m = html.match(/https?:\/\/[^\s\x22'<>]+?\.(mp4|webm|m4v|mov|m3u8|mpd|mp3|wav|flac|aac|m4a|ogg|opus|vtt|srt|ass)(\?[^\s\x22'<>]*)?/gi);
    if (m) m.forEach(function (u) { add(u, 'HTML'); });
    return result;
})();";
            var json = await _webShell.ExecuteScriptOnActiveAsync(script);
            if (string.IsNullOrEmpty(json) || json == "null") return 0;
            var list = JsonSerializer.Deserialize<List<UrlScanItem>>(json);
            if (list is null) return 0;
            int added = 0;
            var pageUrl = _webShell.CurrentUrl ?? string.Empty;
            foreach (var it in list)
            {
                if (string.IsNullOrEmpty(it.Url)) continue;
                var kind = ClassifyByUrl(it.Url, out var ext2);
                if (kind == SnifferResourceKind.Unknown) continue;
                // performance 条目会包含海量 HLS/DASH 分片：跳过 .ts 防刷屏（DOM 元素直链场景由上面元素扫描兜底）
                if (ext2.Equals("ts", StringComparison.OrdinalIgnoreCase) && it.Tag != "VIDEO") continue;
                var title = Path.GetFileName(new Uri(it.Url).AbsolutePath);
                if (AddResource(it.Url, kind, title, string.Empty, 0, pageUrl)) added++;
            }
            return added;
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "扫描当前页面 DOM 失败");
            return 0;
        }
    }

    // 临时 DOM 扫描 JSON 类型
    private sealed class UrlScanItem { public string Url { get; set; } = string.Empty; public string Tag { get; set; } = string.Empty; }

    /// <inheritdoc/>
    public void ClearResources()
    {
        _dispatcher.TryEnqueue(() => Resources.Clear());
        lock (_lock)
        {
            _seenUrls.Clear();
            _titleCounters.Clear();
        }
    }

    private bool AddResource(string url, SnifferResourceKind kind, string title, string mime, long size, string pageUrl,
        string? tagHint = null, string? origin = null, string? headersSummary = null)
    {
        lock (_lock)
        {
            if (!_seenUrls.Add(url)) return false;
        }

        // ===== 用户需求 1：显示名 = 网页标题（而非原始 URL 文件名）=====
        // 规则：优先用当前网页标题；无标题时回退到传入的原始文件名；最后补上真实扩展名；
        // 同一页出现同名资源时追加序号 (2)、(3) 避免重名。
        ClassifyByUrl(url, out var ext);
        var pageTitle = _currentPageTitle;
        var baseTitle = !string.IsNullOrWhiteSpace(pageTitle) ? pageTitle : title;
        baseTitle = SanitizeFileName(baseTitle);
        if (string.IsNullOrWhiteSpace(baseTitle)) baseTitle = "media";
        var finalTitle = baseTitle;
        if (ext.Length > 0 && !finalTitle.EndsWith("." + ext, StringComparison.OrdinalIgnoreCase))
        {
            finalTitle += "." + ext;
        }
        lock (_lock)
        {
            if (_titleCounters.TryGetValue(finalTitle, out var n))
            {
                n++;
                _titleCounters[finalTitle] = n;
                finalTitle += $" ({n})";
            }
            else
            {
                _titleCounters[finalTitle] = 1;
            }
        }

        var item = new SnifferResourceItem
        {
            Url = url,
            Kind = kind,
            Title = finalTitle,
            Extension = ext,
            PageTitle = pageTitle ?? string.Empty,
            MimeType = mime ?? string.Empty,
            SizeBytes = size,
            PageUrl = pageUrl,
            HeadersSummary = headersSummary ?? string.Empty,
            DetectedAtUtc = DateTime.UtcNow
        };
        _dispatcher.TryEnqueue(() => Resources.Add(item));
        ResourceDetected?.Invoke(this, item);
        _logger.Information("嗅探到资源：{Kind} {Title} -> {Url}（来源：{Origin}/{Tag}）",
            kind, item.Title, url, origin ?? "webview", tagHint ?? string.Empty);

        // 9.2：检测到视频资源时在网页右上角叠加"已捕捉到视频"悬浮按钮
        if (kind == SnifferResourceKind.Video || kind == SnifferResourceKind.M3u8)
        {
            _ = InjectCaptureBadgeAsync();
        }
        return true;
    }

    /* ==================== IDM 桥接宿主（AddHostObjectToScript 暴露给网页） ==================== */

    /// <summary>
    /// 由 idm_bridge.js 通过 chrome.webview.hostObjects.sync.snifferBridge 同步调用。
    /// 网页侧约定两个公开方法：
    ///   ReportResource(json)   — bridge 已解析好 URL/tag/origin 的资源上报
    ///   Post(json)             — 原始 IDM port 报文 (opcode+args)
    /// </summary>
    private void HandleBridgeResource(string json)
    {
        try
        {
            if (string.IsNullOrEmpty(json)) return;
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("type", out var t)) return;
            switch (t.GetString())
            {
                case "aitb-idm-res":
                    {
                        var url = doc.RootElement.TryGetProperty("url", out var u) ? (u.GetString() ?? string.Empty) : string.Empty;
                        if (string.IsNullOrEmpty(url)) return;
                        var tag = doc.RootElement.TryGetProperty("tag", out var g) ? (g.GetString() ?? string.Empty) : string.Empty;
                        var origin = doc.RootElement.TryGetProperty("origin", out var o) ? (o.GetString() ?? string.Empty) : "idm";
                        var pageUrl = doc.RootElement.TryGetProperty("pageUrl", out var p) ? (p.GetString() ?? string.Empty) : string.Empty;
                        var title = doc.RootElement.TryGetProperty("title", out var ti) ? ti.GetString() : null;
                        var kind = ClassifyMimeOrUrl(url, string.Empty);
                        ClassifyByUrl(url, out var ext);
                        if (ext.Equals("ts", StringComparison.OrdinalIgnoreCase)) return;
                        if (kind == SnifferResourceKind.Unknown)
                        {
                            if (ext.Length == 0) return;
                            kind = SnifferResourceKind.Video;
                        }
                        string name;
                        try { name = string.IsNullOrWhiteSpace(title) ? Path.GetFileName(new Uri(url).AbsolutePath) : title!; }
                        catch { name = Path.GetFileName(url); }
                        AddResource(url, kind, name, string.Empty, 0, pageUrl, tagHint: tag, origin: origin);
                    }
                    break;
                default:
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "处理 IDM 桥接资源上报失败");
        }
    }

    /// <summary>IDM port 原始报文（仅 Debug 记录，资源入库走 ReportResource/aitb-idm-res）。</summary>
    private void HandleBridgePost(string json)
    {
        try
        {
            if (string.IsNullOrEmpty(json)) return;
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("opcode", out var op))
                _logger.Verbose("IDM hostObject port 报文：op={Op} raw={Raw}", op.GetInt32(),
                    json.Length <= 256 ? json : json[..256]);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "解析 IDM port 报文失败");
        }
    }

    /// <summary>
    /// 通过 AddHostObjectToScript 暴露给网页的 COM 可见宿主对象。
    /// 注意：WinRT/COM 方法只能暴露"公开方法"，参数/返回值使用基本类型（string/int）最稳妥。
    /// WebView2 .NET (Core) 要求宿主对象 public + COM 可见（AutoDual 自动生成 IDispatch 接口）。
    /// 即使 AddHostObjectToScript 运行期失败，idm_bridge.js 也会退回 postMessage 通道，功能不受影响。
    /// </summary>
    [ComVisible(true)]
    [ClassInterface(ClassInterfaceType.AutoDual)]
    public sealed class SnifferBridgeHost
    {
        private readonly Action<string> _reportResource;
        private readonly Action<string> _post;
        private readonly ILogger _logger;

        public SnifferBridgeHost(Action<string> reportResource, Action<string> post, ILogger logger)
        {
            _reportResource = reportResource ?? throw new ArgumentNullException(nameof(reportResource));
            _post = post ?? throw new ArgumentNullException(nameof(post));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <summary>网页侧：ReportResource("{type:'aitb-idm-res', url, tag, origin, pageUrl, title}")</summary>
        public void ReportResource(string json)
        {
            try { _reportResource(json); }
            catch (Exception ex) { _logger.Warning(ex, "SnifferBridgeHost.ReportResource 异常"); }
        }

        /// <summary>网页侧：Post("{type:'aitb-idm', opcode, args}")</summary>
        public void Post(string json)
        {
            try { _post(json); }
            catch (Exception ex) { _logger.Warning(ex, "SnifferBridgeHost.Post 异常"); }
        }
    }

    /// <summary>在当前活动网页注入/更新"已捕捉到视频"悬浮按钮（点击经 postMessage 通知宿主）。</summary>
    private async Task InjectCaptureBadgeAsync()
    {
        try
        {
            if (!_webShell.IsAvailable) return;
            const string script = """
(function () {
    var BADGE_ID = 'aitb-sniff-badge';
    var b = document.getElementById(BADGE_ID);
    if (!b) {
        b = document.createElement('div');
        b.id = BADGE_ID;
        b.setAttribute('data-count', '0');
        b.style.cssText = 'position:fixed;top:12px;right:12px;z-index:2147483647;padding:6px 14px;'
            + 'background:linear-gradient(90deg,#00C6D7,#2E7CF6);color:#fff;font-size:13px;'
            + 'font-family:Segoe UI,sans-serif;border-radius:16px;box-shadow:0 2px 8px rgba(0,0,0,.35);'
            + 'cursor:pointer;user-select:none;display:flex;align-items:center;gap:6px;';
        var icon = document.createElement('span');
        icon.textContent = '\u29CD';
        var label = document.createElement('span');
        label.id = 'aitb-sniff-badge-text';
        label.textContent = '\u5DF2\u6355\u6349\u5230\u89C6\u9891';
        b.appendChild(icon);
        b.appendChild(label);
        b.addEventListener('click', function () {
            try { window.chrome.webview.postMessage('aitb-open-sniffer'); } catch (e) {}
        });
        (document.body || document.documentElement).appendChild(b);
    }
    var n = parseInt(b.getAttribute('data-count') || '0', 10) + 1;
    b.setAttribute('data-count', String(n));
    var t = document.getElementById('aitb-sniff-badge-text');
    if (t) t.textContent = '\u5DF2\u6355\u6349\u5230\u89C6\u9891 (' + n + ')';
})();
""";
            await _webShell.ExecuteScriptOnActiveAsync(script);
        }
        catch (Exception ex)
        {
            // 注入失败不影响嗅探主流程（如页面尚未完成导航）
            _logger.Warning(ex, "注入网页捕捉按钮失败");
        }
    }

    internal static SnifferResourceKind ClassifyByUrl(string url, out string extension)
    {
        extension = string.Empty;
        if (string.IsNullOrWhiteSpace(url)) return SnifferResourceKind.Unknown;
        try
        {
            var u = new Uri(url);
            var path = u.AbsolutePath;
            var dot = path.LastIndexOf('.');
            if (dot < 0) return SnifferResourceKind.Unknown;
            var ext = path[(dot + 1)..].ToLowerInvariant();
            // 切查询参数
            int q = ext.IndexOf('?');
            if (q > 0) ext = ext[..q];
            extension = ext;
            return ext switch
            {
                "m3u8" => SnifferResourceKind.M3u8,
                "mp4" or "webm" or "m4v" or "mov" or "mkv" or "avi" or "flv" or "ts" or "mpd" => SnifferResourceKind.Video,
                "mp3" or "wav" or "flac" or "aac" or "m4a" or "ogg" or "opus" or "wma" => SnifferResourceKind.Audio,
                "vtt" or "srt" or "ass" or "ssa" => SnifferResourceKind.Subtitle,
                _ => SnifferResourceKind.Unknown
            };
        }
        catch { return SnifferResourceKind.Unknown; }
    }

    // ==================== 下载 ====================

    /// <summary>把响应头拼成可读摘要（供"查看详情"展示）。</summary>
    private static string BuildHeadersSummary(string? contentType, string? contentLength, string? disposition)
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(contentType)) sb.Append("Content-Type: ").Append(contentType).Append('\n');
        if (!string.IsNullOrWhiteSpace(contentLength)) sb.Append("Content-Length: ").Append(contentLength).Append('\n');
        if (!string.IsNullOrWhiteSpace(disposition)) sb.Append("Content-Disposition: ").Append(disposition);
        return sb.ToString().TrimEnd('\n');
    }

    /// <summary>
    /// 构造带"防盗链/鉴权"头的下载请求（用户需求 2 的核心修复）：
    /// Referer = 资源来源页面；User-Agent = 浏览器 UA；Cookie = 从 WebView2 CookieManager 取同会话 Cookie。
    /// 缺这些头时很多站点直接 403/空响应，导致此前"下载坏了"。
    /// </summary>
    private async Task<HttpRequestMessage> BuildDownloadRequestAsync(string url, string? referer)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("User-Agent", DefaultUserAgent);
        req.Headers.TryAddWithoutValidation("Accept", "*/*");
        if (!string.IsNullOrWhiteSpace(referer))
        {
            req.Headers.TryAddWithoutValidation("Referer", referer);
        }
        // 拷贝同源 Cookie（与网页会话一致），绕过登录/防盗链校验
        try
        {
            var cookie = await ReadCookieHeaderAsync(url);
            if (!string.IsNullOrEmpty(cookie))
                req.Headers.TryAddWithoutValidation("Cookie", cookie);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "读取 WebView2 Cookie 失败（将以无 Cookie 方式下载）：{Url}", url);
        }
        return req;
    }

    /// <summary>
    /// 读取同会话 Cookie 头。CoreWebView2 是单线程公寓对象，CookieManager 只能在创建它的 UI 线程访问；
    /// 下载线程直接访问会抛 COMException 0x802A000C（只能从创建对象的线程调用该方法），
    /// 于是所有分片请求都退化成"无 Cookie"→ 防盗链站点直接 403。这里统一切回 UI 线程读取。
    /// </summary>
    private async Task<string?> ReadCookieHeaderAsync(string url)
    {
        var cwv = _attachedCwv;
        if (cwv is null) return null;
        if (_dispatcher.HasThreadAccess) return await BuildCookieHeaderAsync(cwv, url);

        var tcs = new TaskCompletionSource<string?>();
        if (!_dispatcher.TryEnqueue(async () =>
        {
            try { tcs.TrySetResult(await BuildCookieHeaderAsync(cwv, url)); }
            catch (Exception ex) { tcs.TrySetException(ex); }
        }))
        {
            return null; // UI 线程已退出（关闭中），按无 Cookie 处理
        }
        return await tcs.Task;
    }

    /// <summary>把 WebView2 中该 URL 的 Cookie 拼成 "a=1; b=2" 形式。</summary>
    private static async Task<string?> BuildCookieHeaderAsync(CoreWebView2 cwv, string url)
    {
        var cookies = await cwv.CookieManager.GetCookiesAsync(url);
        if (cookies is not { Count: > 0 }) return null;
        var sb = new StringBuilder();
        foreach (var c in cookies)
        {
            if (c is null) continue;
            if (sb.Length > 0) sb.Append("; ");
            sb.Append(c.Name).Append('=').Append(c.Value);
        }
        return sb.Length > 0 ? sb.ToString() : null;
    }

    /// <summary>带正确请求头的 GET（仅读响应头，不缓冲正文）。</summary>
    private async Task<HttpResponseMessage> SendWithHeadersAsync(string url, string? referer)
    {
        var req = await BuildDownloadRequestAsync(url, referer);
        return await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
    }

    /// <summary>带正确请求头读取文本（m3u8 清单等）；失败时带上 HTTP 状态码与 URL，便于界面显示。</summary>
    private async Task<string> FetchStringAsync(string url, string? referer)
    {
        using var resp = await SendWithHeadersAsync(url, referer);
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}：{url}", null, resp.StatusCode);
        return await resp.Content.ReadAsStringAsync();
    }

    /// <summary>同名文件自动追加 (1)、(2)… 避免覆盖。</summary>
    private static string EnsureUniquePath(string dir, string fileName)
    {
        var path = Path.Combine(dir, fileName);
        if (!File.Exists(path)) return path;
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var ext = Path.GetExtension(fileName);
        int i = 1;
        do
        {
            path = Path.Combine(dir, $"{stem} ({i}){ext}");
            i++;
        } while (File.Exists(path));
        return path;
    }

    /// <inheritdoc/>
    public async Task<SnifferDownloadTask> DownloadAsync(SnifferResourceItem item, string? saveDir = null, int m3u8Concurrency = 32)
    {
        if (item is null) throw new ArgumentNullException(nameof(item));
        var dir = string.IsNullOrWhiteSpace(saveDir) ? DefaultSaveDir : saveDir;
        Directory.CreateDirectory(dir);

        var task = new SnifferDownloadTask
        {
            Title = item.Title,
            Url = item.Url,
            Referer = item.PageUrl,
            Status = "初始化…"
        };
        _dispatcher.TryEnqueue(() => Downloads.Insert(0, task));
        try
        {
            if (item.Kind == SnifferResourceKind.M3u8)
            {
                await DownloadM3u8Async(task, dir, Math.Max(1, m3u8Concurrency));
            }
            else
            {
                await DownloadSingleFileAsync(task, dir);
            }
            UpdateTask(task, () =>
            {
                task.IsCompleted = true;
                task.Status = "完成";
            });
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "下载失败：{Title}", item.Title);
            UpdateTask(task, () =>
            {
                task.IsFailed = true;
                task.Status = $"失败：{ex.Message}";
            });
        }
        return task;
    }

    /// <summary>
    /// 在 UI（Dispatcher）线程上更新下载任务属性。
    /// SnifferDownloadTask 继承 ObservableObject，属性变更会经 WinRT 通知 XAML 绑定；
    /// 若在后台线程（Task.Run 的分片线程）直接赋值，会抛 COMException 0x8001010E
    /// （"只能从创建对象的线程调用该方法"），进而让整个 m3u8 下载崩溃——
    /// 这正是日志里"下载失败"的直接原因，也是"m3u8 下载用不了"的根因。
    /// </summary>
    private void UpdateTask(SnifferDownloadTask task, Action action)
    {
        if (_dispatcher.HasThreadAccess)
        {
            action();
            return;
        }
        _dispatcher.TryEnqueue(() => action());
    }

    private async Task DownloadSingleFileAsync(SnifferDownloadTask task, string dir)
    {
        var name = SanitizeFileName(task.Title);
        if (string.IsNullOrWhiteSpace(name) || name.Length < 2) name = "download_" + Guid.NewGuid().ToString("N")[..8];
        var path = EnsureUniquePath(dir, name);
        UpdateTask(task, () => task.Status = "下载中…");
        using var resp = await SendWithHeadersAsync(task.Url, task.Referer);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"下载失败（HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}）：{task.Url}");
        var contentLength = resp.Content.Headers.ContentLength ?? 0;
        UpdateTask(task, () =>
        {
            task.TotalBytes = contentLength;
            task.LocalPath = path;
        });
        long total = 0;
        await using (var fs = File.Create(path))
        {
            var buf = new byte[64 * 1024];
            var stream = await resp.Content.ReadAsStreamAsync();
            int n;
            while ((n = await stream.ReadAsync(buf, 0, buf.Length)) > 0)
            {
                await fs.WriteAsync(buf, 0, n);
                total += n;
                var received = total;
                UpdateTask(task, () =>
                {
                    task.ReceivedBytes = received;
                    if (contentLength > 0) task.Progress = (double)received / contentLength;
                });
            }
        }
        UpdateTask(task, () =>
        {
            task.Progress = 1.0;
            task.ReceivedBytes = total;
            if (total > task.TotalBytes || task.TotalBytes == 0) task.TotalBytes = total;
        });
    }

    private async Task DownloadM3u8Async(SnifferDownloadTask task, string dir, int concurrency)
    {
        UpdateTask(task, () => task.Status = "解析 m3u8…");
        var referer = task.Referer;
        var manifest = await FetchStringAsync(task.Url, referer);
        var baseUri = new Uri(task.Url);

        // Master playlist（#EXT-X-STREAM-INF）：取最高带宽的子 playlist 递归解析为媒体分片
        if (manifest.Contains("#EXT-X-STREAM-INF", StringComparison.Ordinal))
        {
            UpdateTask(task, () => task.Status = "检测到 Master Playlist，选择子清单…");
            var mediaUrl = PickBestMediaManifest(manifest, baseUri);
            if (string.IsNullOrEmpty(mediaUrl)) throw new InvalidOperationException("Master Playlist 未解析到可用子清单");
            _logger.Information("Master Playlist → 子清单：{Url}", mediaUrl);
            manifest = await FetchStringAsync(mediaUrl, referer);
            baseUri = new Uri(mediaUrl);
        }

        // 解析下载单元（普通分片 + fMP4 初始化段 #EXT-X-MAP，按清单出现顺序排列）
        var parts = ParseM3u8Parts(manifest, baseUri, out var keyInfo);
        if (parts.Count == 0) throw new InvalidOperationException("m3u8 未解析到任何分片");
        bool hasInit = parts.Any(p => p.IsInitSegment);

        // 解析加密密钥（参考 M3u8Downloader_H CryptM3uDownloader.BeforeDownload + M3uKeyInfoHelper）
        if (keyInfo is not null && keyInfo.KeyBytes is null && !keyInfo.Method.Equals("NONE", StringComparison.OrdinalIgnoreCase))
        {
            UpdateTask(task, () => task.Status = "获取加密密钥…");
            await ResolveM3u8KeyAsync(keyInfo, baseUri, referer);
            if (keyInfo.IsEncrypted)
                _logger.Information("m3u8 密钥已就绪：{Method}, key(base64)={B64}, iv={Iv}",
                    keyInfo.Method, Convert.ToBase64String(keyInfo.KeyBytes!), keyInfo.IV is null ? "(默认全0)" : Convert.ToHexString(keyInfo.IV));
            else
                _logger.Warning("m3u8 密钥解析失败（{Method}），将按未加密数据拼接", keyInfo.Method);
        }

        // 输出文件名：含 fMP4 初始化段 → .mp4；否则 .ts。
        // 原来直接沿用网页标题（常以 .m3u8 结尾）导致输出文件扩展名错误、播放器不识别。
        var title = SanitizeFileName(task.Title);
        title = Path.GetFileNameWithoutExtension(title);
        if (string.IsNullOrWhiteSpace(title)) title = "download_" + Guid.NewGuid().ToString("N")[..8];
        title += hasInit ? ".mp4" : ".ts";
        var outputPath = EnsureUniquePath(dir, title);
        UpdateTask(task, () => task.LocalPath = outputPath);

        // 临时分片目录
        var segDir = Path.Combine(dir, $"__seg_{Guid.NewGuid():N}");
        Directory.CreateDirectory(segDir);
        try
        {
            UpdateTask(task, () => task.Status = $"下载分片（并发 {concurrency}，共 {parts.Count}）…");

            // 首错即取消：SemaphoreSlim + Task.WhenAll 默认会抛出第一个异常，但其它分片仍在跑，
            // 这里用 CancellationTokenSource 在首个失败时 Cancel，及时停止剩余请求。
            using var cts = new CancellationTokenSource();
            var token = cts.Token;
            var sem = new SemaphoreSlim(concurrency, concurrency);
            // TotalBytes/ReceivedBytes 改为"累计已写入字节数"（原来用分片计数会误导 UI）；
            // Progress 仍按已完成分片数 / 总分片数 计算。
            long totalBytes = 0;
            int done = 0;
            Exception? firstError = null;

            var tasks = new List<Task<(int order, string file)>>(parts.Count);
            foreach (var part in parts)
            {
                var p = part;
                tasks.Add(Task.Run(async () =>
                {
                    await sem.WaitAsync(token);
                    try
                    {
                        var segPath = Path.Combine(segDir, $"{p.Order:000000}.part");
                        using var resp = await SendWithHeadersAsync(p.Url, referer);
                        if (!resp.IsSuccessStatusCode)
                            throw new InvalidOperationException(
                                $"{(p.IsInitSegment ? "初始化段" : "分片")} {p.Order} 下载失败（HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}）：{p.Url}");

                        long written;
                        if (p.IsInitSegment)
                        {
                            // fMP4 初始化段按 HLS 规范不加密，原样写入；合并时排在其后续媒体分片之前
                            var bytes = await resp.Content.ReadAsByteArrayAsync(token);
                            await File.WriteAllBytesAsync(segPath, bytes, token);
                            written = bytes.LongLength;
                        }
                        else if (keyInfo is { IsEncrypted: true })
                        {
                            // 加密分片：整块读入 → AES-CBC/PKCS7 解密（参考 CryptM3uDownloader.HandleData + CryptExtensions.AesDecrypt）
                            var cipher = await resp.Content.ReadAsByteArrayAsync(token);
                            byte[] plain;
                            try
                            {
                                plain = AesDecryptCore(cipher, keyInfo.KeyBytes!, keyInfo.IV);
                            }
                            catch (CryptographicException ex)
                            {
                                throw new InvalidOperationException($"分片 {p.Order} 解密失败（请确认 KEY/IV 是否正确）：{p.Url}", ex);
                            }
                            await File.WriteAllBytesAsync(segPath, plain, token);
                            written = plain.LongLength;
                        }
                        else
                        {
                            await using (var fs = File.Create(segPath))
                            {
                                await (await resp.Content.ReadAsStreamAsync(token)).CopyToAsync(fs, token);
                                written = fs.Length;
                            }
                        }

                        Interlocked.Add(ref totalBytes, written);
                        int completed = Interlocked.Increment(ref done);
                        var snapBytes = Interlocked.Read(ref totalBytes);
                        UpdateTask(task, () =>
                        {
                            task.ReceivedBytes = snapBytes;
                            task.TotalBytes = snapBytes;
                            task.Progress = (double)completed / parts.Count;
                        });
                        return (p.Order, segPath);
                    }
                    catch (Exception ex)
                    {
                        // 记录首个错误并取消剩余分片（首个异常会被包装成可读信息抛出）
                        Interlocked.CompareExchange(ref firstError, ex, null);
                        cts.Cancel();
                        throw;
                    }
                    finally
                    {
                        sem.Release();
                    }
                }));
            }

            try
            {
                await Task.WhenAll(tasks);
            }
            catch (Exception) when (firstError is not null)
            {
                throw new InvalidOperationException($"m3u8 分片下载失败：{firstError.Message}", firstError);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"m3u8 分片下载失败：{ex.Message}", ex);
            }

            var files = tasks.Select(t => t.Result).OrderBy(f => f.order).ToList();

            UpdateTask(task, () => task.Status = "合并分片…");
            await using (var output = File.Create(outputPath))
            {
                foreach (var (_, file) in files)
                {
                    if (!File.Exists(file)) continue;
                    await using var fs = File.OpenRead(file);
                    await fs.CopyToAsync(output);
                }
            }
            // 基础校验：合并结果不能为空
            if (new FileInfo(outputPath).Length == 0)
                throw new InvalidOperationException("合并结果为空");
            UpdateTask(task, () => task.Progress = 1.0);
        }
        finally
        {
            try { Directory.Delete(segDir, recursive: true); } catch { }
        }
    }

    /// <summary>m3u8 加密信息（参考 M3u8Downloader_H 的 M3UKeyInfo/M3uKeyInfoHelper）。</summary>
    internal sealed class M3u8KeyInfo
    {
        /// <summary>加密方法（AES-128/AES-192/AES-256/NONE）。</summary>
        public string Method { get; set; } = "AES-128";
        public string Uri { get; set; } = string.Empty;
        public byte[]? IV { get; set; }
        /// <summary>解析后的密钥字节（AES-128 = 16 字节）。</summary>
        public byte[]? KeyBytes { get; set; }
        public bool IsEncrypted => !Method.Equals("NONE", StringComparison.OrdinalIgnoreCase) && KeyBytes is { Length: > 0 };
    }

    /// <summary>
    /// 下载并解析 m3u8 加密密钥（参考 M3u8Downloader_H：CryptM3uDownloader.BeforeDownload 的拉取逻辑
    /// + M3uKeyInfoHelper.TryParseKey 的 长度/编码 判定规则）。
    /// AES-128: 16 字节原始 或 24 字符 Base64；AES-192: 24/32；AES-256: 32/44。
    /// </summary>
    private async Task ResolveM3u8KeyAsync(M3u8KeyInfo key, Uri baseUri, string? referer)
    {
        if (string.IsNullOrEmpty(key.Uri)) throw new InvalidDataException("m3u8 声明了加密但缺少 KEY URI");
        // KEY URI 相对路径 → 绝对化（支持本地 file://）
        var keyUri = new Uri(baseUri, key.Uri);
        byte[] raw;
        if (keyUri.IsFile)
        {
            raw = await File.ReadAllBytesAsync(keyUri.OriginalString);
        }
        else
        {
            using var resp = await SendWithHeadersAsync(keyUri.AbsoluteUri, referer);
            if (!resp.IsSuccessStatusCode)
                throw new InvalidOperationException(
                    $"获取 m3u8 密钥失败（HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}）：{keyUri.AbsoluteUri}");
            raw = await resp.Content.ReadAsByteArrayAsync();
        }
        key.KeyBytes = ParseKeyBytes(key.Method, raw);
    }

    /// <summary>
    /// 按加密方法把原始 key 字节规范化（M3uKeyInfoHelper.TryParseKey 等价实现，但判定更宽松）。
    /// 规则：先按原始密钥字节（AES-128=16 / AES-192=24 / AES-256=32）直接使用；
    /// 否则去掉首尾空白后按 Base64 解码，若解码结果长度也是 16/24/32 则接受；
    /// 两者都不满足才抛出带明确中文说明的异常（原实现仅做长度相等判断，长度不符时
    /// 会直接抛 InvalidCastException 且信息不清晰）。
    /// </summary>
    internal static byte[] ParseKeyBytes(string method, byte[] data)
    {
        if (data is { Length: > 0 })
        {
            // 1) 原始密钥字节
            if (data.Length is 16 or 24 or 32) return data;

            // 2) 文本形式（Base64）：先去首尾空白再解码
            try
            {
                var text = Encoding.UTF8.GetString(data).Trim();
                if (text.Length > 0)
                {
                    var decoded = Convert.FromBase64String(text);
                    if (decoded.Length is 16 or 24 or 32) return decoded;
                }
            }
            catch (FormatException)
            {
                // 不是合法 Base64，落到下面统一报错
            }
        }
        throw new InvalidDataException(
            $"无法解析的密钥（{method}，{data.Length} 字节）：既不是 16/24/32 字节的原始密钥，也不是可解码的 Base64");
    }

    /// <summary>AES-CBC/PKCS7 解密（等价 CryptExtensions.AesDecrypt；IV 为空时用 16 字节全 0）。</summary>
    internal static byte[] AesDecryptCore(byte[] content, byte[] aesKey, byte[]? aesIv)
    {
        using Aes aes = Aes.Create();
        aes.Padding = PaddingMode.PKCS7;
        aes.Mode = CipherMode.CBC;
        aes.Key = aesKey;
        aes.IV = aesIv ?? new byte[16];
        using var dec = aes.CreateDecryptor(aes.Key, aes.IV);
        return dec.TransformFinalBlock(content, 0, content.Length);
    }

    /// <summary>Master Playlist：选 BANDWIDTH 最高的子媒体清单（等价 MediaManifestAttributeReader 的选取意图）。</summary>
    internal static string? PickBestMediaManifest(string manifest, Uri baseUri)
    {
        string? best = null;
        long bestBw = -1;
        var lines = manifest.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (!line.StartsWith("#EXT-X-STREAM-INF", StringComparison.Ordinal)) continue;
            var bw = 0L;
            var mBw = Regex.Match(line, "BANDWIDTH=(\\d+)", RegexOptions.IgnoreCase);
            if (mBw.Success) long.TryParse(mBw.Groups[1].Value, out bw);
            // 子清单 URL 在 STREAM-INF 的下一行
            for (int j = i + 1; j < lines.Length; j++)
            {
                var candidate = lines[j];
                if (candidate.StartsWith("#")) continue;
                if (bw > bestBw)
                {
                    bestBw = bw;
                    best = candidate;
                }
                break;
            }
        }
        if (string.IsNullOrWhiteSpace(best)) return null;
        try { return new Uri(baseUri, best).AbsoluteUri; } catch { return best; }
    }

    /// <summary>m3u8 下载单元：普通媒体分片，或 fMP4 初始化段（#EXT-X-MAP）。按清单出现顺序排序。</summary>
    internal sealed class M3u8Part
    {
        public string Url { get; init; } = string.Empty;
        /// <summary>是否为 #EXT-X-MAP 初始化段（按 HLS 规范不加密，须排在其后续媒体分片之前）。</summary>
        public bool IsInitSegment { get; init; }
        /// <summary>#EXT-X-BYTERANGE（当前暂不支持 Range 下载，仅记录备用）。</summary>
        public string? ByteRange { get; init; }
        /// <summary>清单中出现顺序，用于合并排序。</summary>
        public int Order { get; init; }
    }

    /// <summary>
    /// 解析 m3u8，返回按出现顺序排列的下载单元（含 #EXT-X-MAP 初始化段）。
    /// 参考 M3u8Downloader_H：MapAttributeReader 解析 #EXT-X-MAP 的 URI + BYTERANGE；
    /// M3uFileInfoExtension 在生成合并清单时把 MAP 写在媒体分片之前——fMP4 播放必需。
    /// 说明：#EXT-X-BYTERANGE 目前仅记录、不用于下载（Range 请求暂未支持），
    ///       但分片 URL 仍按行读取，不会因此错位。
    /// </summary>
    internal static List<M3u8Part> ParseM3u8Parts(string manifest, Uri baseUri, out M3u8KeyInfo? key)
    {
        var parts = new List<M3u8Part>();
        key = null;
        int order = 0;
        var lines = manifest.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var line in lines)
        {
            if (line.StartsWith("#EXT-X-KEY", StringComparison.Ordinal))
            {
                // #EXT-X-KEY:METHOD=AES-128,URI="https://...",IV=0x...
                var mMethod = Regex.Match(line, "METHOD=([A-Za-z0-9-]+)");
                if (mMethod.Success)
                {
                    var method = mMethod.Groups[1].Value.ToUpperInvariant();
                    if (method == "NONE")
                    {
                        // METHOD=NONE：分片未加密，丢弃之前的 key
                        key = null;
                        continue;
                    }
                    key ??= new M3u8KeyInfo();
                    key.Method = method;
                }
                var mUri = Regex.Match(line, "URI=\"([^\"]+)\"");
                if (mUri.Success)
                {
                    key ??= new M3u8KeyInfo();
                    key.Uri = mUri.Groups[1].Value;
                }
                var mIv = Regex.Match(line, "IV=0x([0-9A-Fa-f]+)");
                if (mIv.Success && key is not null)
                {
                    key.IV = Convert.FromHexString(mIv.Groups[1].Value.PadLeft(32, '0'));
                }
                continue;
            }
            if (line.StartsWith("#EXT-X-MAP", StringComparison.Ordinal))
            {
                // #EXT-X-MAP:URI="init.mp4"[,BYTERANGE="length@offset"]（fMP4 初始化段）
                var mUri = Regex.Match(line, "URI=\"([^\"]+)\"");
                if (mUri.Success)
                {
                    var mRange = Regex.Match(line, "BYTERANGE=\"([^\"]+)\"");
                    parts.Add(new M3u8Part
                    {
                        Url = Absolutize(baseUri, mUri.Groups[1].Value),
                        IsInitSegment = true,
                        ByteRange = mRange.Success ? mRange.Groups[1].Value : null,
                        Order = order++,
                    });
                }
                continue;
            }
            if (line.StartsWith("#EXT-X-BYTERANGE", StringComparison.Ordinal))
            {
                // 分片级 Range 下载暂不支持：此处仅跳过（不影响分片 URL 按行解析，不会错位）
                continue;
            }
            if (line.StartsWith("#")) continue;
            if (string.IsNullOrWhiteSpace(line)) continue;
            parts.Add(new M3u8Part { Url = Absolutize(baseUri, line), Order = order++ });
        }
        return parts;
    }

    /// <summary>把（相对）URL 绝对化；失败时返回原串。</summary>
    private static string Absolutize(Uri baseUri, string url)
    {
        try { return new Uri(baseUri, url).AbsoluteUri; } catch { return url; }
    }

    /// <summary>兼容旧调用：仅返回普通媒体分片 URL（不含 #EXT-X-MAP 初始化段）。</summary>
    internal static List<string> ParseM3u8Segments(string manifest, Uri baseUri, out M3u8KeyInfo? key)
    {
        var parts = ParseM3u8Parts(manifest, baseUri, out key);
        var list = new List<string>(parts.Count);
        foreach (var p in parts)
            if (!p.IsInitSegment) list.Add(p.Url);
        return list;
    }

    internal static string SanitizeFileName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return string.Empty;
        var invalids = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(name);
        foreach (var c in invalids) sb.Replace(c, '_');
        return sb.ToString().Trim('.', ' ');
    }

    public void Dispose()
    {
        StopSniffing();
        _http.Dispose();
    }
}
