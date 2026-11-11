/* =========================================================
 * IDM Integration Module — WebView2 宿主适配桥 (idm_bridge.js)
 * ---------------------------------------------------------
 * 原版 IDM 扩展 (Tonec FZE / Internet Download Manager Integration Module v6.42+)
 * 依赖 MV2 的 chrome/browser.* 扩展 API，通过 chrome.runtime.connect
 * 建立 Port 与 background 原生程序 (idm.exe via nativeMessaging/WebSocket)
 * 通讯。这里将扩展 API 做最小化桩实现，把 content.js 向 port 发送的
 * [opcode, ...args] 消息转成 {type:'aitb-idm', opcode, args} 事件，通过
 *   (a) chrome.webview.hostObjects.sync.snifferBridge  (C# 侧 AddHostObjectToScript)
 *   (b) window.chrome.webview.postMessage                       (WebView2 WebMessageReceived)
 * 双通道回传到 C# SnifferService 处理。
 *
 * 注入顺序（SnifferService.BuildHookScript）:
 *   ① idm_bridge.js (本文件，先建 chrome/browser runtime 环境)
 *   ② debug.js       (可选，console.log/dir 的 debug 改写，防反调试)
 *   ③ document.js    (ISOLATED-WORLD 侧：XHR/fetch Accept-header 加标 vnd.t1c)
 *   ④ content.js     (MAIN 世界：MutationObserver 媒体/链接扫描 + port 上报)
 * ========================================================= */
(function () {
    "use strict";
    if (window.__aitb_idm_bridge_installed__) return;
    window.__aitb_idm_bridge_installed__ = true;

    var IS_TOP = window.self === window.top;
    var ORIGIN = window.origin || document.origin || location.origin;

    /* ---------- 工具：统一把消息送到 C# SnifferService ---------- */
    function emitCooked(opcode, args, meta) {
        try {
            var payload = {
                type: "aitb-idm",
                opcode: opcode | 0,
                args: args || [],
                meta: meta || null,
                pageUrl: location.href,
                frame: IS_TOP ? "top" : "sub"
            };
            // 通道 A：同步 hostObject（C# AddHostObjectToScript("snifferBridge", ...)）
            try {
                var ho = (window.chrome && window.chrome.webview && window.chrome.webview.hostObjects
                    && window.chrome.webview.hostObjects.sync) ?
                    window.chrome.webview.hostObjects.sync.snifferBridge : null;
                if (ho && typeof ho.Post === "function") {
                    try { ho.Post(JSON.stringify(payload)); } catch (_) { /* sync host unavailable */ }
                }
            } catch (_) { }
            // 通道 B：postMessage（WebMessageReceived 兜底）
            try {
                if (window.chrome && window.chrome.webview && typeof window.chrome.webview.postMessage === "function") {
                    window.chrome.webview.postMessage(JSON.stringify(payload));
                }
            } catch (_) { }
        } catch (_) { }
    }

    /* =========================================================
     * Part 1. browser / chrome 根对象桩
     * ========================================================= */
    if (typeof browser === "undefined") {
        // content.js 开头有: "undefined"==typeof browser&&(browser=chrome);
        // 这里先搭好 chrome 本体，browser 让它被 content.js 自然赋值亦可
    }
    if (typeof chrome === "undefined") window.chrome = {};
    var chrome = window.chrome;

    /* =========================================================
     * Part 2. chrome.runtime / browser.runtime 桩
     *   IDM content.js 依赖：
     *     browser.runtime.connect({name: (P?"top":"sub")+...}).postMessage([opcode,...])
     *     port.onMessage.addListener / onDisconnect.addListener
     *     browser.runtime.lastError
     *     (仅 background.js 用的 getFrameId / getURL / i18n / getManifest 不必实现)
     * ========================================================= */
    var _portSeq = 1;
    var _pendingCallbacks = {}; // cid -> {onMessage:[...], onDisconnect:[...]}
    var _lastError = null;

    function makePort(name, cid) {
        var listeners = { onMessage: [], onDisconnect: [] };
        _pendingCallbacks[cid] = listeners;
        return {
            name: name || "",
            id: cid,
            sender: null,
            disconnect: function () {
                var arr = listeners.onDisconnect;
                delete _pendingCallbacks[cid];
                for (var i = 0; i < arr.length; i++) {
                    try { arr[i](this); } catch (_) { }
                }
            },
            postMessage: function (msg) {
                // IDM content.js 发给 port 的全部是数组：[opcode, arg1, arg2, ...]
                if (!Array.isArray(msg)) return;
                var opcode = msg[0] | 0;
                var rest = msg.slice(1);
                dispatchPortMessage(cid, opcode, rest);
            },
            onMessage: {
                addListener: function (fn) { if (typeof fn === "function") listeners.onMessage.push(fn); },
                removeListener: function (fn) {
                    var a = listeners.onMessage, i = a.indexOf(fn); if (i >= 0) a.splice(i, 1);
                },
                hasListener: function (fn) { return listeners.onMessage.indexOf(fn) >= 0; },
                addRules: function () { },
                removeRules: function () { },
                getRules: function () { }
            },
            onDisconnect: {
                addListener: function (fn) { if (typeof fn === "function") listeners.onDisconnect.push(fn); },
                removeListener: function (fn) {
                    var a = listeners.onDisconnect, i = a.indexOf(fn); if (i >= 0) a.splice(i, 1);
                }
            }
        };
    }

    function dispatchPortMessage(cid, opcode, args) {
        /* ----- 立即把底层 port 消息甩给 C# 全量记录（便于开发调试） ----- */
        emitCooked(opcode, args, { cid: cid });

        /* ----- 将 content.js 上报的高价值 opcode 翻译成 SnifferResourceItem 字段 -----
         *  opcode 速查表（来自 content.js/background.js 实际报文）:
         *   21 [Qc/navigate] :           [href,title,...]       导航/文档元信息
         *   23 [Tc/media state]:         [flag, elementId, force?, ... , localName, src/data, title, rect]
         *          args[1]=elementId; args[5]=localName, args[6]=src(or data)
         *   24 [Sc/page title/dpi]:      [flag, title, titleInfo?, pixelRatio, offset]
         *   25 [La/beforeload]:          [elementId, localName, url]
         *   27 [Gc/linkPanel batch]:     [flag, linkPanelId, count, [ [url, kind, download, text], ... ], href, title]
         *          batch 在 args[3]
         *   34 [script/outerHTML]:       [title?, null?, textScript]
         *   36 [36, requestId, accept]:  XHR/fetch accept 协商
         *   37 [site fingerprint cfg]:   [1, 1/2, payload]
         *   41 [B/new media create]:     [elementId, localName, src||data, a.a(link), a.c(type), d(rect)]
         * ---------------------------------------------------------------- */
        try {
            switch (opcode) {
                case 25: { // beforeload：最直接的多媒体资源声明
                    var eid = args[0], tag = (args[1] || "").toString().toLowerCase(), url = args[2] || "";
                    if (url) reportResource(url, "idm-op25-" + tag, tag.toUpperCase());
                    break;
                }
                case 41: { // 新元素创建：f.B → [41, elementId, localName, src||data, a.a, a.c, d]
                    var local = (args[1] || "").toString().toLowerCase(), src = args[2] || "";
                    if (src) reportResource(src, "idm-op41-" + local, local.toUpperCase());
                    break;
                }
                case 23: { // 媒体元素状态变更（最常触发）：args[1]=elementId
                    var eid23 = args[1];
                    // f.Fb → [23, flag, elementId, force?, ... , localName(args[5]), src||data(args[6]), title, rect]
                    try {
                        var lc = (args[5] || "").toString().toLowerCase();
                        var sc = args[6] || "";
                        if (sc && typeof sc === "string") reportResource(sc, "idm-op23-" + lc, lc.toUpperCase());
                    } catch (_) { }
                    // 回退：DOM 实时扫描（覆盖 src 在 extras 里缺失的情况）
                    try {
                        if (typeof document !== "undefined" && typeof document.querySelectorAll === "function") {
                            var list = document.querySelectorAll("video,audio,source,embed,object,track,iframe");
                            for (var i = 0; i < list.length; i++) {
                                var el = list[i], s = el.currentSrc || el.src || el.data || el.getAttribute("data-src") || "";
                                if (s) reportResource(s, "idm-op23-refresh", (el.tagName || el.nodeName || "EL").toString().toUpperCase());
                            }
                        }
                    } catch (_) { }
                    break;
                }
                case 39: { // a 元素 click：f.Sb → [39, hasDownloadAttr, a.href, a.download]
                    var dl39 = !!args[1];
                    var href = args[2] || "";
                    if (href && typeof href === "string") {
                        reportResource(href, "idm-op39-click", dl39 ? "A-DOWNLOAD" : "A");
                    }
                    break;
                }
                case 27: { // 链接面板：批量 [url, kind(1=txt/2=a/3=img/4=iframe/5=script), downloadAttr, labelText]
                    var batch = args.length >= 4 ? args[3] : null;
                    if (Array.isArray(batch)) {
                        for (var j = 0; j < batch.length; j++) {
                            var row = batch[j];
                            if (!Array.isArray(row) || !row[0]) continue;
                            var u = row[0], kd = row[1] | 0, dl = row[2] || null, txt = row[3] || "";
                            if (kd === 1 || kd === 2 || kd === 4 || kd === 5) {
                                // 文本中提取 URL / A 链接 / iframe / script src
                                reportResource(u, "idm-op27-k" + kd,
                                    kd === 1 ? "TXT" : kd === 2 ? "A" : kd === 4 ? "IFRAME" : "SCRIPT",
                                    { label: typeof txt === "string" ? txt : "", download: dl });
                            }
                        }
                    }
                    break;
                }
            }
        } catch (_) { /* content.js 字段变形时兜底不崩 */ }
    }

    /* ---------- 通用：提交一个媒体/链接资源给 SnifferService ---------- */
    var _seen = {};
    function reportResource(url, origin, tag, extra) {
        if (!url || typeof url !== "string") return;
        if (url.indexOf("data:") === 0 || url.indexOf("blob:") === 0) return;
        try { url = new URL(url, location.href).href; } catch (_) { return; }
        var key = origin + "|" + url;
        if (_seen[key]) return;
        _seen[key] = true;
        try {
            var r = {
                type: "aitb-idm-res",
                url: url,
                origin: origin || "idm",
                tag: tag || "",
                pageUrl: location.href,
                title: document.title || "",
                extra: extra || null
            };
            if (window.chrome && window.chrome.webview && typeof window.chrome.webview.postMessage === "function")
                window.chrome.webview.postMessage(JSON.stringify(r));
            try {
                var ho2 = window.chrome && window.chrome.webview && window.chrome.webview.hostObjects
                    && window.chrome.webview.hostObjects.sync && window.chrome.webview.hostObjects.sync.snifferBridge;
                if (ho2 && typeof ho2.ReportResource === "function")
                    ho2.ReportResource(JSON.stringify(r));
            } catch (_) { }
        } catch (_) { }
    }

    /* ---------- chrome.runtime.connect / lastError / getManifest / onMessage ---------- */
    if (!chrome.runtime) chrome.runtime = {};
    chrome.runtime.connect = function (info) {
        var name = (info && info.name) ? info.name.toString() : (IS_TOP ? "top" : "sub");
        var cid = "aitb-" + (_portSeq++) + "-" + Date.now().toString(36);
        return makePort(name, cid);
    };
    Object.defineProperty(chrome.runtime, "lastError", {
        configurable: true, enumerable: true,
        get: function () { return _lastError; }
    });
    chrome.runtime.getManifest = chrome.runtime.getManifest || function () {
        return { manifest_version: 2, version: "6.42.32", name: "IDM Integration Module" };
    };
    chrome.runtime.getURL = chrome.runtime.getURL || function (p) {
        return (typeof p === "string" && p.charAt(0) === "/") ? p : "/" + (p || "");
    };
    chrome.runtime.id = "idm-integration-module@tonec";
    chrome.runtime.sendMessage = chrome.runtime.sendMessage || function () { };
    if (!chrome.runtime.onMessage) chrome.runtime.onMessage = {
        addListener: function () { }, removeListener: function () { }, hasListener: function () { return false; }
    };

    /* =========================================================
     * Part 3. chrome.storage (content.js 不直接用，但防止 background.js 片段在页面有残留时报错)
     * ========================================================= */
    if (!chrome.storage) chrome.storage = {
        local: { get: function (k, cb) { if (cb) cb({}); }, set: function () { }, remove: function () { } },
        sync: { get: function (k, cb) { if (cb) cb({}); }, set: function () { }, remove: function () { } }
    };

    /* =========================================================
     * Part 4. 与 document.js 的 handshake
     *   document.js (ISOLATED-WORLD 注入) 在加载完成后会向 window 发：
     *      window.postMessage([1229212977], "/");
     *   content.js 监听 message 收到 opcode 1229212978 后再开启：
     *      h.open/h.send/h.setRequestHeader 替换 & fetch 包装
     *   这里的桥接响应 opcode 1229212977：
     *      [1229212978, u=0, enabledBC=true, enabledDOC=true, pattern=空, captureRX=空]
     *   即启用 XHR & Fetch 两部分 hook，但当前正则留空 → 匹配全部请求交给 SnifferService
     *   WebResourceResponseReceived 兜底再过滤；避免 vnd.t1c 改标影响宿主。
     * ========================================================= */
    window.addEventListener("message", function (ev) {
        try {
            var data = ev.data;
            if (!Array.isArray(data)) return;
            // 同源窗口内消息，仅处理 IDM 私有 opcode
            if (data[0] === 1229212977) {
                // 回复 [1229212978, u, bc_enabled, doc_enabled, url_pattern, capture_pattern]
                // 这里 bc(XHR)/doc(fetch) 都置 true：若网站有反爬 Accept 冲突会导致异常
                // 则 document.js 内部 try/catch 已经自行保护，不会影响页面运行。
                var reply = [1229212978, 0, true, true, "", ""];
                try {
                    if (IS_TOP) window.postMessage(reply, "/");
                    else (window.parent || window.top).postMessage(reply, "*");
                } catch (_) { window.postMessage(reply, "*"); }
                return;
            }
            if (data[0] === 1229212979) {
                // document.js 发 requestId + accept → 直接转发给 C# 记录
                emitCooked(1229212979, [data[1], data[2]], null);
                return;
            }
            if (data[0] === 1229212980) {
                // document.js 请求 window 内属性 (getFrameId, DOM 读取) → 直接返回 undefined
                try {
                    var token = data[1], attr = data[3] || "";
                    var val = void 0;
                    try {
                        var target = (data[2] ? document.querySelector('[__idm_id__="' + data[2] + '"]') : null) || window;
                        if (target && attr) {
                            // 简化：仅读取 src/href/data/currentSrc 这些常用字段
                            if (attr === "src") val = target.src;
                            else if (attr === "href") val = target.href;
                            else if (attr === "data-src") val = target.getAttribute && target.getAttribute("data-src");
                        }
                    } catch (_) { val = void 0; }
                    var r2 = [1229212981, token, val];
                    try { window.postMessage(r2, "/"); } catch (_) { window.postMessage(r2, "*"); }
                } catch (_) { }
                return;
            }
        } catch (_) { /* 防外部污染 */ }
    }, false);

    /* =========================================================
     * Part 5. (可选) 直接在主世界再扫一轮 video/audio/iframe src，补充 IDM MutationObserver
     *   启动时机： DOMContentLoaded / 立刻 (如果 document.readyState 已完成)
     * ========================================================= */
    function quickScan() {
        try {
            if (typeof document === "undefined") return;
            var all = document.querySelectorAll("video,audio,source,embed,object,iframe");
            for (var i = 0; i < all.length; i++) {
                var el = all[i], s = el.currentSrc || el.src || el.data || el.getAttribute("data-src") || "";
                if (s) reportResource(s, "idm-bridge-scan", (el.tagName || "").toString().toUpperCase());
            }
        } catch (_) { }
    }
    try {
        if (document.readyState === "interactive" || document.readyState === "complete") quickScan();
        else document.addEventListener("DOMContentLoaded", quickScan, false);
    } catch (_) { }
    // 5s 后再补一次（SPA 延迟加载）
    try { setTimeout(quickScan, 5000); setTimeout(quickScan, 15000); } catch (_) { }

    /* =========================================================
     * Part 6. browser.browserAction / contextMenus / tabs / i18n / webNavigation /
     *         downloads / proxy / management — 只给最小空桩，不抛 undefined
     * ========================================================= */
    var noop = function () { };
    if (!chrome.browserAction) chrome.browserAction = {
        onClicked: { addListener: noop, removeListener: noop },
        setIcon: noop, setTitle: noop, setBadgeText: noop, setBadgeBackgroundColor: noop,
        enable: noop, disable: noop
    };
    if (!chrome.contextMenus) chrome.contextMenus = {
        create: function () { return ""; }, removeAll: noop, update: noop, onShown: { addListener: noop, removeListener: noop }
    };
    if (!chrome.tabs) chrome.tabs = {
        query: function (_, cb) { if (cb) cb([]); }, remove: noop, reload: noop,
        onActivated: { addListener: noop }, onUpdated: { addListener: noop }, onRemoved: { addListener: noop },
        onAttached: { addListener: noop }, executeScript: function (_, __, cb) { if (cb) cb(); }
    };
    if (!chrome.webNavigation) chrome.webNavigation = {
        onCreatedNavigationTarget: { addListener: noop }, onCommitted: { addListener: noop },
        onHistoryStateUpdated: { addListener: noop }, getFrame: function (_, cb) { if (cb) cb(null); },
        getAllFrames: function (_, cb) { if (cb) cb(null); }
    };
    if (!chrome.windows) chrome.windows = {
        getAll: function (_, cb) { if (cb) cb([]); }, getLastFocused: function (cb) { if (cb) cb({ id: 0 }); },
        get: function (id, cb) { if (cb) cb({ id: id }); },
        onCreated: { addListener: noop }, onRemoved: { addListener: noop },
        onFocusChanged: { addListener: noop }, onBoundsChanged: { addListener: noop },
        update: noop, create: function (opt, cb) { if (cb) cb({ id: -1, tabs: [] }); }
    };
    if (!chrome.webRequest) chrome.webRequest = {
        onBeforeRequest: { addListener: noop }, onBeforeSendHeaders: { addListener: noop },
        onHeadersReceived: { addListener: noop }, onResponseStarted: { addListener: noop },
        onErrorOccurred: { addListener: noop }, onBeforeRedirect: { addListener: noop }
    };
    if (!chrome.downloads) chrome.downloads = {
        search: function (_, cb) { if (cb) cb([]); }, erase: function (_, cb) { if (cb) cb([]); },
        pause: noop, cancel: noop, resume: noop,
        setUiOptions: noop, setShelfEnabled: noop,
        onCreated: { addListener: noop }, onDeterminingFilename: { addListener: noop }
    };
    if (!chrome.proxy) chrome.proxy = { settings: { get: function (_, cb) { if (cb) cb({ value: { mode: "system" } }); }, onChange: { addListener: noop } } };
    if (!chrome.cookies) chrome.cookies = { getAll: function (_, cb) { if (cb) cb([]); }, set: noop, remove: noop };
    if (!chrome.management) chrome.management = { uninstallSelf: function () { } };
    if (!chrome.i18n) chrome.i18n = { getUILanguage: function () { return "zh-CN"; } };
    if (!chrome.notifications) chrome.notifications = { create: noop, clear: noop };
    if (!chrome.system) chrome.system = {};
    if (!chrome.system.display) chrome.system.display = { getInfo: function (_, cb) { if (cb) cb([{ displayZoomFactor: 1 }]); } };
    if (!chrome.idle) chrome.idle = { onStateChanged: { addListener: noop }, setDetectionInterval: noop };
    if (!chrome.extension) chrome.extension = { isAllowedIncognitoAccess: function (cb) { if (cb) cb(true); } };

    /* =========================================================
     * Part 7. 同步对象上的其他扩展 API 也放在 browser（IDM content.js 开头做 alias：
     *           "undefined"==typeof browser&&(browser=chrome);）
     *   所以有上面 chrome.* 就已经够用。不再额外覆写。
     * ========================================================= */

    // 告知 C# 侧桥已就绪
    try {
        var hi = { type: "aitb-idm-ready", pageUrl: location.href, top: IS_TOP };
        if (window.chrome && window.chrome.webview && typeof window.chrome.webview.postMessage === "function")
            window.chrome.webview.postMessage(JSON.stringify(hi));
    } catch (_) { }
})();
