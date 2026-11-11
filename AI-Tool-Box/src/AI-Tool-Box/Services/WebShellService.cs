using AI_Tool_Box.Controls;
using AI_Tool_Box.Models;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using Serilog;
using System.Text.Json;

namespace AI_Tool_Box.Services;

/// <summary>WebShell 服务实现：持有 WebShell 控件引用，对外暴露导航、内容抓取、书签/历史/下载能力。</summary>
public sealed class WebShellService : IWebShellService
{
    private readonly ILogger _logger;
    private WebShell? _shell;

    /// <summary>持有 WebShell 控件（用于 SnifferService 等内部组件通过反射取 _active）。</summary>
    internal WebShell? Shell => _shell;

    /// <summary>初始化 WebShellService。</summary>
    public WebShellService(ILogger logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>绑定 WebShell 控件实例。</summary>
    internal void Attach(WebShell shell)
    {
        _shell = shell;
        _logger.Information("WebShellService 已绑定 WebShell 控件");
    }

    /// <inheritdoc/>
    public bool IsAvailable => _shell?.IsAvailable ?? false;

    /// <inheritdoc/>
    public bool IsAddressBarVisible { get; private set; }

    /// <inheritdoc/>
    public bool IsVisible => _shell?.Visibility == Microsoft.UI.Xaml.Visibility.Visible;

    /// <inheritdoc/>
    public bool IsAiAssistOpen
    {
        get => _shell?.IsAiAssistOpen ?? false;
        set { if (_shell is not null) _shell.IsAiAssistOpen = value; }
    }

    /// <inheritdoc/>
    public string? CurrentUrl => _shell?.CurrentUrl;

    /// <inheritdoc/>
    public event EventHandler<BookmarkItem>? BookmarkAdded
    {
        add { if (_shell is not null) _shell.BookmarkAdded += value; }
        remove { if (_shell is not null) _shell.BookmarkAdded -= value; }
    }

    /// <inheritdoc/>
    public event EventHandler<HistoryItem>? HistoryAdded
    {
        add { if (_shell is not null) _shell.HistoryAdded += value; }
        remove { if (_shell is not null) _shell.HistoryAdded -= value; }
    }

    /// <inheritdoc/>
    public event EventHandler<DownloadItem>? DownloadChanged
    {
        add { if (_shell is not null) _shell.DownloadChanged += value; }
        remove { if (_shell is not null) _shell.DownloadChanged -= value; }
    }

    /// <inheritdoc/>
    public void SetVisibility(bool visible, bool addressBarVisible)
    {
        if (_shell is null)
        {
            return;
        }
        _shell.Visibility = visible ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;
        _shell.SetAddressBarVisible(visible && addressBarVisible);
        IsAddressBarVisible = visible && addressBarVisible;
    }

    /// <inheritdoc/>
    public async Task<string?> GetContentAsync()
    {
        if (_shell is null)
        {
            _logger.Warning("WebShell 未绑定，无法抓取内容");
            return null;
        }
        return await _shell.GetContentAsync();
    }

    /// <inheritdoc/>
    public async Task<byte[]?> CapturePreviewAsync()
    {
        if (_shell is null) return null;
        var f = typeof(WebShell).GetField("_active", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        if (f?.GetValue(_shell) is not WebView2 wv || wv.CoreWebView2 is null) return null;
        try
        {
            var ras = new Windows.Storage.Streams.InMemoryRandomAccessStream();
            await wv.CoreWebView2.CapturePreviewAsync(Microsoft.Web.WebView2.Core.CoreWebView2CapturePreviewImageFormat.Png, ras);
            if (ras.Size == 0) return null;
            ras.Seek(0);
            var bytes = new byte[(int)ras.Size];
            using var reader = new Windows.Storage.Streams.DataReader(ras);
            await reader.LoadAsync((uint)ras.Size);
            reader.ReadBytes(bytes);
            return bytes;
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "网页截图失败");
            return null;
        }
    }

    /// <summary>取当前活动标签页的 CoreWebView2（无可用页面返回 null）。</summary>
    private CoreWebView2? GetActiveCoreWebView2()
    {
        if (_shell is null) return null;
        var f = typeof(WebShell).GetField("_active", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        return f?.GetValue(_shell) is WebView2 wv ? wv.CoreWebView2 : null;
    }

    /// <summary>全局 Esc 取消标志：置位后当前拾取循环会尽快退出并移除拾取层。</summary>
    private volatile bool _pickCancelRequested;

    /// <inheritdoc/>
    public void CancelActivePick()
    {
        _pickCancelRequested = true;
        _logger.Information("收到取消拾取请求（Esc）");
    }

    /// <inheritdoc/>
    public async Task<string?> PickElementOnPageAsync(CancellationToken ct = default)
    {
        var payload = await PickRawPayloadAsync(regionMode: false, ct);
        return payload is null ? null : ComposePickedHtml(payload);
    }

    /// <inheritdoc/>
    public async Task<RegionPickResult?> PickRegionAsync(CancellationToken ct = default)
    {
        var payload = await PickRawPayloadAsync(regionMode: true, ct);
        if (payload is null) return null;
        try
        {
            using var d = JsonDocument.Parse(payload);
            var sel = ReadString(d.RootElement, "selector");
            var label = ReadString(d.RootElement, "label");
            return string.IsNullOrWhiteSpace(sel) ? null : new RegionPickResult(sel, label);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>运行网页元素拾取器，返回选中元素的原始 JSON 片段；取消/超时/无页面返回 null。
    /// regionMode=true 时只返回区域身份（selector/label），不含 html/outline 内容。</summary>
    private async Task<string?> PickRawPayloadAsync(bool regionMode, CancellationToken ct)
    {
        var core = GetActiveCoreWebView2();
        if (core is null)
        {
            _logger.Warning("页面拾取：当前没有可用的网页");
            return null;
        }

        _pickCancelRequested = false;
        try
        {
            var injectResult = await core.ExecuteScriptAsync(BuildPickerStart(regionMode));
            var probe = await core.ExecuteScriptAsync(JsPickerProbe);
            _logger.Information("页面拾取注入：result={Result} probe={Probe} 地址栏={Url}",
                injectResult, probe, _shell?.CurrentUrl);

            var probeState = string.Empty;
            using (var pd = ParseScriptResult(probe))
            {
                if (pd is not null) probeState = ReadString(pd.RootElement, "state");
            }
            if (probeState is "missing" or "error")
            {
                _logger.Warning("页面拾取：脚本未能在当前页面生效（state={State}）", probeState);
                return null;
            }

            _logger.Information("页面拾取已开启");

            var deadline = DateTime.UtcNow.AddMinutes(3);
            while (true)
            {
                if (ct.IsCancellationRequested || _pickCancelRequested || DateTime.UtcNow > deadline)
                {
                    await TryDestroyPickerAsync(core);
                    _logger.Information("页面拾取已取消（{Reason}）",
                        _pickCancelRequested ? "用户按 Esc" : (ct.IsCancellationRequested ? "用户取消" : "超时"));
                    return null;
                }

                string raw;
                try
                {
                    raw = await core.ExecuteScriptAsync(JsPickerPoll);
                }
                catch (Exception ex)
                {
                    // 页面跳转/关闭后脚本上下文失效
                    _logger.Warning(ex, "页面拾取轮询失败（页面可能已跳转）");
                    return null;
                }

                var state = string.Empty;
                using (var doc = ParseScriptResult(raw))
                {
                    if (doc is not null) state = ReadString(doc.RootElement, "state");
                }

                switch (state)
                {
                    case "done":
                    {
                        using var doc = ParseScriptResult(raw);
                        var payload = doc is null ? string.Empty : ReadString(doc.RootElement, "payload");
                        await TryDestroyPickerAsync(core);
                        _logger.Information("页面拾取已完成：{Chars} 字符", payload.Length);
                        return payload;
                    }
                    case "error":
                    {
                        using var doc = ParseScriptResult(raw);
                        var err = doc is null ? string.Empty : ReadString(doc.RootElement, "error");
                        await TryDestroyPickerAsync(core);
                        _logger.Warning("页面拾取脚本异常：{Error}", err);
                        return null;
                    }
                    case "cancelled":
                    case "gone":
                        await TryDestroyPickerAsync(core);
                        return null;
                    default:
                        await Task.Delay(80);
                        break;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "页面元素拾取失败");
            return null;
        }
    }

    /// <summary>移除网页上的拾取层（保留已取到的结果）。</summary>
    private static async Task TryDestroyPickerAsync(CoreWebView2 core)
    {
        try
        {
            await core.ExecuteScriptAsync(
                "(function(){var p=window.__atbPicker;if(p&&p.destroy){p.destroy();}return true;})()");
        }
        catch
        {
            // 页面已关闭或跳转：忽略
        }
    }

    /// <summary>把拾取器返回的 JSON 片段拼成要交给 AI 的结构化 HTML。</summary>
    private static string? ComposePickedHtml(string payload)
    {
        if (string.IsNullOrWhiteSpace(payload)) return null;
        try
        {
            using var d = JsonDocument.Parse(payload);
            var root = d.RootElement;
            var tag = ReadString(root, "tag");
            var title = ReadString(root, "title");
            var outline = ReadString(root, "outline");
            var html = ReadString(root, "html");
            if (string.IsNullOrWhiteSpace(html)) return null;

            var head = $"<!-- 页面拾取：<{tag}>{(string.IsNullOrWhiteSpace(title) ? string.Empty : " " + title)} -->";
            var composed = head + "\n" + outline + "\n\n" + html;
            if (composed.Length > 14000) composed = composed[..14000] + "\n…（已截断）";
            return composed;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string ReadString(JsonElement obj, string name)
        => obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? (v.GetString() ?? string.Empty) : string.Empty;

    /// <summary>ExecuteScriptAsync 结果是“字符串的 JSON 形式”，需先解包再解析。</summary>
    private static JsonDocument? ParseScriptResult(string raw)
    {
        if (string.IsNullOrEmpty(raw) || raw == "null") return null;
        string text;
        try
        {
            text = JsonSerializer.Deserialize<string>(raw) ?? string.Empty;
        }
        catch (JsonException)
        {
            text = raw;
        }
        if (string.IsNullOrWhiteSpace(text) || text == "null") return null;
        try
        {
            return JsonDocument.Parse(text);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <inheritdoc/>
    public async Task<bool> ClickAsync(string selector)
    {
        var core = GetActiveCoreWebView2();
        if (core is null || string.IsNullOrWhiteSpace(selector))
        {
            _logger.Warning("按选择器点击失败：无可用网页或选择器为空");
            return false;
        }
        try
        {
            var js = "(function(){var el=document.querySelector(" + JsString(selector) + ");"
                   + "if(!el)return JSON.stringify({ok:false,reason:'not-found'});"
                   + "try{el.scrollIntoView({block:'center',inline:'center'});}catch(e0){}"
                   + "try{el.focus();}catch(e1){}"
                   + "try{el.click();}catch(e2){return JSON.stringify({ok:false,reason:String(e2)});}"
                   + "return JSON.stringify({ok:true,tag:el.tagName});})()";
            var raw = await core.ExecuteScriptAsync(js);
            var ok = ReadOk(raw);
            _logger.Information("按选择器点击：{Selector} → {Ok}", selector, ok);
            return ok;
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "按选择器点击失败：{Selector}", selector);
            return false;
        }
    }

    /// <inheritdoc/>
    public async Task<bool> SetValueAsync(string selector, string text)
    {
        var core = GetActiveCoreWebView2();
        if (core is null || string.IsNullOrWhiteSpace(selector))
        {
            _logger.Warning("按选择器填入文本失败：无可用网页或选择器为空");
            return false;
        }
        try
        {
            var js = "(function(){var el=document.querySelector(" + JsString(selector) + ");"
                   + "if(!el)return JSON.stringify({ok:false,reason:'not-found'});"
                   + "var t=" + JsString(text ?? string.Empty) + ";"
                   + "try{el.scrollIntoView({block:'center',inline:'center'});}catch(e0){}"
                   + "try{el.focus();}catch(e1){}"
                   + "var tag=(el.tagName||'').toLowerCase();"
                   + "if(tag==='textarea'||tag==='input'){"
                   + "var proto=(el instanceof HTMLTextAreaElement)?HTMLTextAreaElement.prototype:HTMLInputElement.prototype;"
                   + "var setter=Object.getOwnPropertyDescriptor(proto,'value').set;"
                   + "setter.call(el,t);"
                   + "el.dispatchEvent(new Event('input',{bubbles:true}));"
                   + "el.dispatchEvent(new Event('change',{bubbles:true}));"
                   + "return JSON.stringify({ok:true,mode:'value'});}"
                   + "if(el.isContentEditable){"
                   + "try{var r=document.createRange();r.selectNodeContents(el);var s=window.getSelection();s.removeAllRanges();s.addRange(r);"
                   + "document.execCommand('insertText',false,t);}catch(e2){el.textContent=t;}"
                   + "el.dispatchEvent(new Event('input',{bubbles:true}));"
                   + "return JSON.stringify({ok:true,mode:'contenteditable'});}"
                   + "try{el.value=t;el.dispatchEvent(new Event('input',{bubbles:true}));return JSON.stringify({ok:true,mode:'prop'});}catch(e3){}"
                   + "return JSON.stringify({ok:false,reason:'unsupported'});})()";
            var raw = await core.ExecuteScriptAsync(js);
            var ok = ReadOk(raw);
            _logger.Information("按选择器填入文本：{Selector} 长度 {Len} → {Ok}", selector, text?.Length ?? 0, ok);
            return ok;
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "按选择器填入文本失败：{Selector}", selector);
            return false;
        }
    }

    /// <inheritdoc/>
    public async Task<string?> ReadTextAsync(string selector)
    {
        var core = GetActiveCoreWebView2();
        if (core is null || string.IsNullOrWhiteSpace(selector)) return null;
        try
        {
            var js = "(function(){var el=document.querySelector(" + JsString(selector) + ");"
                   + "if(!el)return null;return String(el.innerText||el.textContent||'');})()";
            var raw = await core.ExecuteScriptAsync(js);
            return UnwrapString(raw);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "按选择器读取文本失败：{Selector}", selector);
            return null;
        }
    }

    /// <summary>把 C# 字符串安全嵌入 JS 字面量（含引号转义，防注入）。</summary>
    private static string JsString(string s) => JsonSerializer.Serialize(s);

    /// <summary>解析脚本返回的 {"ok":true/false} 结构。</summary>
    private static bool ReadOk(string raw)
    {
        using var doc = ParseScriptResult(raw);
        return doc is not null
               && doc.RootElement.ValueKind == JsonValueKind.Object
               && doc.RootElement.TryGetProperty("ok", out var o)
               && o.ValueKind == JsonValueKind.True;
    }

    /// <summary>解包 ExecuteScriptAsync 的“字符串的 JSON 形式”为原始字符串。</summary>
    private static string? UnwrapString(string raw)
    {
        if (string.IsNullOrEmpty(raw) || raw == "null") return null;
        try
        {
            return JsonSerializer.Deserialize<string>(raw);
        }
        catch (JsonException)
        {
            return raw;
        }
    }

    /// <summary>构造拾取器注入脚本：regionMode=true 时为“区域拾取”（只取区域身份，不采集区域内文本内容）。</summary>
    private static string BuildPickerStart(bool regionMode)
        => "window.__ATB_REGION=" + (regionMode ? "true" : "false") + ";" + JsPickerStart;

    /// <summary>注入网页的元素拾取器：悬停高亮 + 点击取结构化结果（页面拾取取 HTML；区域拾取只取区域身份），Esc 取消。</summary>
    private const string JsPickerStart = """
        (function(){
          if(window.__atbPicker&&window.__atbPicker.destroy){ try{window.__atbPicker.destroy();}catch(e){} }
          function clean(root,max){
            var c=root.cloneNode(true);
            var junk=c.querySelectorAll('script,style,noscript,iframe,canvas,link,meta,svg');
            for(var i=0;i<junk.length;i++){ junk[i].parentNode&&junk[i].parentNode.removeChild(junk[i]); }
            var all=c.querySelectorAll('*');
            for(var j=0;j<all.length;j++){
              if(j>240){ if(all[j].parentNode) all[j].parentNode.removeChild(all[j]); continue; }
              var at=all[j].attributes;
              for(var k=at.length-1;k>=0;k--){
                var n=at[k].name.toLowerCase();
                if(n.indexOf('on')===0||n==='style'||n.indexOf('data-')===0){ all[j].removeAttribute(at[k].name); }
              }
            }
            var h=c.outerHTML||'';
            return h.length>max?h.slice(0,max)+'…':h;
          }
          function outline(root){
            var lines=[]; var count=0;
            function walk(el,depth){
              if(!el||depth>4||count>200) return;
              var tag=(el.tagName||'').toLowerCase();
              if(!tag||['script','style','noscript','svg','iframe','canvas','template'].indexOf(tag)>=0) return;
              var attrs='';
              if(el.id) attrs+=' id="'+el.id+'"';
              var cls=(typeof el.className==='string')?el.className.trim():'';
              if(cls) attrs+=' class="'+(cls.length>60?cls.slice(0,60):cls)+'"';
              if(tag==='a'&&el.getAttribute('href')) attrs+=' href="'+el.getAttribute('href').slice(0,80)+'"';
              if(el.getAttribute('role')) attrs+=' role="'+el.getAttribute('role')+'"';
              count++;
              var text='';
              if(el.children.length===0){
                text=(el.textContent||'').replace(/\s+/g,' ').trim();
                if(text.length>160) text=text.slice(0,160)+'…';
              }
              lines.push('  '.repeat(Math.min(depth,4))+'<'+tag+attrs+'>'+(text?' '+text:''));
              for(var c=0;c<el.children.length;c++){ walk(el.children[c],depth+1); }
            }
            walk(root,0);
            return lines.join('\n');
          }
          function cssPath(el){
            try{
              if(!el||el.nodeType!==1) return '';
              if(el===document.body) return 'body';
              var idOk=/^[A-Za-z_][\w-]*$/;
              if(el.id&&idOk.test(el.id)) return '#'+el.id;
              var parts=[]; var node=el; var guard=0;
              while(node&&node.nodeType===1&&node!==document.documentElement&&guard++<12){
                var tag=node.tagName.toLowerCase();
                if(node.id&&idOk.test(node.id)){ parts.unshift('#'+node.id); break; }
                var cls=(typeof node.className==='string')?node.className.trim().split(/\s+/):[];
                var good=[];
                for(var i=0;i<cls.length&&good.length<2;i++){
                  var c=cls[i];
                  if(c&&idOk.test(c)&&!/\d{4,}/.test(c)) good.push(c);
                }
                var seg=tag;
                if(good.length){ seg+='.'+good.join('.'); }
                var parent=node.parentNode;
                if(parent&&parent.children){
                  var sibs=[];
                  for(var j=0;j<parent.children.length;j++){ if(parent.children[j].tagName===node.tagName) sibs.push(parent.children[j]); }
                  if(sibs.length>1){ seg+=':nth-of-type('+(sibs.indexOf(node)+1)+')'; }
                }
                parts.unshift(seg);
                node=parent;
              }
              return parts.join(' > ');
            }catch(e){ return ''; }
          }
          var host=document.body||document.documentElement;
          var overlay=document.createElement('div');
          overlay.id='atb_pick_box';
          overlay.setAttribute('style','position:fixed !important;z-index:2147483647 !important;pointer-events:none !important;display:none;background:rgba(0,255,170,0.18) !important;border:2px solid #00FFAA !important;box-shadow:0 0 0 2px rgba(0,255,170,0.45),0 0 14px rgba(0,255,170,0.9) !important;box-sizing:border-box !important;margin:0 !important;padding:0 !important;');
          var label=document.createElement('div');
          label.id='atb_pick_label';
          label.setAttribute('style','position:fixed !important;z-index:2147483647 !important;pointer-events:none !important;display:none;background:#00261d !important;color:#baffea !important;border:1px solid #00FFAA !important;font:12px/1.4 Consolas,monospace !important;padding:2px 6px !important;border-radius:3px !important;white-space:nowrap !important;margin:0 !important;');
          var hint=document.createElement('div');
          hint.id='atb_pick_hint';
          hint.setAttribute('style','position:fixed !important;z-index:2147483647 !important;pointer-events:none !important;left:50% !important;transform:translateX(-50%) !important;top:12px !important;background:rgba(0,20,16,0.92) !important;color:#baffea !important;border:1px solid #00FFAA !important;font:13px/1.4 "Microsoft YaHei",sans-serif !important;padding:6px 12px !important;border-radius:6px !important;box-shadow:0 2px 10px rgba(0,0,0,0.45) !important;white-space:nowrap !important;margin:0 !important;');
          hint.textContent=(window.__ATB_REGION?'区域拾取中：移动鼠标高亮目标区域，单击选取，按 Esc 取消':'页面拾取中：移动鼠标高亮区域，单击选中，按 Esc 取消');
          host.appendChild(overlay);
          host.appendChild(label);
          host.appendChild(hint);
          var current=null;
          function describe(el){
            var tag=(el.tagName||'').toLowerCase();
            var s='<'+tag;
            if(el.id) s+='#'+el.id;
            var cls=(typeof el.className==='string')?el.className.trim().split(/\s+/)[0]:'';
            if(cls) s+='.'+cls;
            var r=el.getBoundingClientRect();
            return s+'>  '+Math.round(r.width)+'×'+Math.round(r.height);
          }
          function paint(el){
            if(!el||el===overlay||el===label||el===hint) return;
            current=el;
            var r=el.getBoundingClientRect();
            overlay.style.left=r.left+'px'; overlay.style.top=r.top+'px';
            overlay.style.width=Math.max(0,r.width)+'px'; overlay.style.height=Math.max(0,r.height)+'px';
            overlay.style.display='block';
            label.textContent=describe(el);
            label.style.display='block';
            label.style.left=Math.max(2,Math.min(r.left,window.innerWidth-240))+'px';
            label.style.top=(r.top>26?r.top-22:r.bottom+4)+'px';
          }
          function onMove(e){
            var el=document.elementFromPoint(e.clientX,e.clientY);
            if(!el||el===overlay||el===label||el===hint) return;
            paint(el);
          }
          function blockClick(e){
            e.preventDefault(); e.stopPropagation();
            if(e.stopImmediatePropagation){ e.stopImmediatePropagation(); }
          }
          function finish(state,payload){
            var p=window.__atbPicker;
            if(p){ p.state=state; p.payload=payload||''; }
            overlay.style.display='none'; label.style.display='none';
            hint.style.display='none';
            document.removeEventListener('mousemove',onMove,true);
            window.removeEventListener('mousemove',onMove,true);
            document.removeEventListener('pointermove',onMove,true);
            window.removeEventListener('pointermove',onMove,true);
            document.removeEventListener('pointerdown',onPress,true);
            window.removeEventListener('pointerdown',onPress,true);
            document.removeEventListener('mousedown',onPress,true);
            window.removeEventListener('mousedown',onPress,true);
            document.removeEventListener('keydown',onKey,true);
            window.removeEventListener('keydown',onKey,true);
            document.removeEventListener('scroll',onScroll,true);
            window.removeEventListener('scroll',onScroll,true);
          }
          function onPress(e){
            blockClick(e);
            onClick(e);
          }
          function onClick(e){
            e.preventDefault(); e.stopPropagation();
            if(e.stopImmediatePropagation){ e.stopImmediatePropagation(); }
            var el=current||document.elementFromPoint(e.clientX,e.clientY);
            if(!el){ finish('cancelled',''); return; }
            var tag=(el.tagName||'').toLowerCase();
            var title='';
            var hd=el.querySelector?el.querySelector('h1,h2,h3,h4'):null;
            if(hd&&hd.textContent) title=hd.textContent.replace(/\s+/g,' ').trim().slice(0,60);
            if(!title&&el.getAttribute&&el.getAttribute('aria-label')) title=el.getAttribute('aria-label');
            if(!title&&el.id) title='#'+el.id;
            var data=window.__ATB_REGION
              ? {selector:cssPath(el),label:describe(el),tag:tag}
              : {tag:tag,title:title,selector:cssPath(el),outline:outline(el).slice(0,2500),html:clean(el,6000)};
            finish('done',JSON.stringify(data));
          }
          function onKey(e){
            if(e.key==='Escape'){ finish('cancelled',''); }
            blockClick(e);
          }
          function onScroll(){ if(current) paint(current); }
          var blockTypes=['click','auxclick','contextmenu','submit','dragstart','pointerup','mouseup'];
          function attachBlocks(){
            for(var i=0;i<blockTypes.length;i++){
              document.addEventListener(blockTypes[i],blockClick,true);
              window.addEventListener(blockTypes[i],blockClick,true);
            }
          }
          function detachBlocks(){
            for(var i=0;i<blockTypes.length;i++){
              document.removeEventListener(blockTypes[i],blockClick,true);
              window.removeEventListener(blockTypes[i],blockClick,true);
            }
          }
          window.__atbPicker={
            state:'active', payload:'', error:'',
            destroy:function(){
              try{ overlay.parentNode&&overlay.parentNode.removeChild(overlay); }catch(e){}
              try{ label.parentNode&&label.parentNode.removeChild(label); }catch(e){}
              try{ hint.parentNode&&hint.parentNode.removeChild(hint); }catch(e){}
              try{ finish('destroyed',''); }catch(e){}
              detachBlocks();
              try{ delete window.__atbPicker; }catch(e){}
            }
          };
          document.addEventListener('mousemove',onMove,true);
          window.addEventListener('mousemove',onMove,true);
          document.addEventListener('pointermove',onMove,true);
          window.addEventListener('pointermove',onMove,true);
          document.addEventListener('pointerdown',onPress,true);
          window.addEventListener('pointerdown',onPress,true);
          document.addEventListener('mousedown',onPress,true);
          window.addEventListener('mousedown',onPress,true);
          document.addEventListener('keydown',onKey,true);
          window.addEventListener('keydown',onKey,true);
          document.addEventListener('scroll',onScroll,true);
          window.addEventListener('scroll',onScroll,true);
          attachBlocks();
          return true;
        })()
        """;

    /// <summary>轮询拾取器状态：active / done / cancelled / gone。</summary>
    private const string JsPickerPoll = """
        (function(){
          var p=window.__atbPicker;
          if(!p) return JSON.stringify({state:'gone',payload:''});
          return JSON.stringify({state:p.state||'active',payload:p.payload||'',error:p.error||''});
        })()
        """;

    /// <summary>注入后的自检：确认脚本确实运行在当前可见页面（排查“注入到了别的标签页”）。</summary>
    private const string JsPickerProbe = """
        (function(){
          var p=window.__atbPicker;
          return JSON.stringify({
            href:String(location.href).slice(0,120),
            title:String(document.title).slice(0,60),
            state:p?(p.state||''):'missing',
            error:p?(p.error||''):'',
            box:!!document.getElementById('atb_pick_box'),
            top:window===window.top,
            frames:window.length
          });
        })()
        """;

    /// <inheritdoc/>
    public Task<bool> NavigateAsync(string url) => _shell?.NavigateAsync(url) ?? Task.FromResult(false);

    /// <inheritdoc/>
    public Task<IReadOnlyList<BookmarkItem>> LoadBookmarksAsync()
        => _shell?.LoadBookmarksAsync() ?? Task.FromResult<IReadOnlyList<BookmarkItem>>(Array.Empty<BookmarkItem>());

    /// <inheritdoc/>
    public Task<bool> AddBookmarkAsync()
        => _shell?.AddBookmarkAsync() ?? Task.FromResult(false);

    /// <inheritdoc/>
    public Task RemoveBookmarkAsync(string url)
    {
        if (_shell is not null)
        {
            return _shell.RemoveBookmarkAsync(url);
        }
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<HistoryItem>> LoadHistoryAsync(int limit = 200)
        => _shell?.LoadHistoryAsync(limit) ?? Task.FromResult<IReadOnlyList<HistoryItem>>(Array.Empty<HistoryItem>());

    /// <inheritdoc/>
    public Task ClearHistoryAsync()
    {
        if (_shell is not null)
        {
            return _shell.ClearHistoryAsync();
        }
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<DownloadItem>> LoadDownloadsAsync()
        => _shell?.LoadDownloadsAsync() ?? Task.FromResult<IReadOnlyList<DownloadItem>>(Array.Empty<DownloadItem>());

    /// <inheritdoc/>
    public void NewTab() => _shell?.NewTab();

    /// <inheritdoc/>
    public void CloseTab(string url) => _shell?.CloseTab(url);

    /// <inheritdoc/>
    public Task<bool> InjectToActiveElementAsync(string text)
        => _shell?.InjectToActiveElementAsync(text) ?? Task.FromResult(false);

    /// <inheritdoc/>
    public void GoBack() => _shell?.GoBack();

    /// <inheritdoc/>
    public void GoForward() => _shell?.GoForward();

    /// <inheritdoc/>
    public void Refresh() => _shell?.Refresh();

    /// <inheritdoc/>
    public async Task<string> ExecuteScriptOnActiveAsync(string script)
    {
        if (_shell is null) return "null";
        // 通过反射访问 WebShell 私有 _active
        var f = typeof(WebShell).GetField("_active", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        if (f?.GetValue(_shell) is WebView2 wv && wv.CoreWebView2 is not null)
        {
            try { return await wv.CoreWebView2.ExecuteScriptAsync(script) ?? "null"; }
            catch (Exception ex) { _logger.Warning(ex, "执行脚本失败"); return "null"; }
        }
        return "null";
    }

    private EventHandler? _activePageChanged;

    /// <inheritdoc/>
    public event EventHandler? ActivePageChanged
    {
        add
        {
            _activePageChanged += value;
            // 若已有 shell，立即触发一次
            if (_shell is not null) value?.Invoke(this, EventArgs.Empty);
        }
        remove { _activePageChanged -= value; }
    }

    /// <summary>当 WebShell 切换标签（需 WebShell 主动调用，见下方 RaiseActiveChanged 内部调用位）时触发。</summary>
    internal void RaiseActivePageChanged() => _activePageChanged?.Invoke(this, EventArgs.Empty);
}
