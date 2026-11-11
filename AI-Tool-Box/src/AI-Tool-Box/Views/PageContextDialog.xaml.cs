using System.Globalization;
using System.Text;
using System.Text.Json;
using AI_Tool_Box.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using Serilog;
using Windows.Foundation;
using Windows.Storage.Streams;

namespace AI_Tool_Box.Views;

/// <summary>网页区块条目：勾选后把其结构化 HTML 拼进提问。</summary>
public sealed partial class PageBlockItem : ObservableObject
{
    /// <summary>区块标签名（如 article / main）。</summary>
    public string Tag { get; init; } = string.Empty;

    /// <summary>区块标题（首个小标题 / aria-label / id）。</summary>
    public string Title { get; init; } = string.Empty;

    /// <summary>标题旁的辅助说明（标签 + 文本长度）。</summary>
    public string Subtitle { get; init; } = string.Empty;

    /// <summary>已清洗并截断的结构化 HTML。</summary>
    public string Html { get; init; } = string.Empty;

    /// <summary>是否勾选。</summary>
    [ObservableProperty] private bool _isSelected;
}

/// <summary>“选取网页内容”弹窗：按区块勾选，或对页面截图框选后反推对应 HTML 节点。</summary>
public sealed partial class PageContextDialog : ContentDialog
{
    private readonly ILogger _logger;
    private readonly List<PageBlockItem> _blocks = new();

    private IWebShellService? _shell;
    private double _clientWidth = 1;
    private double _clientHeight = 1;
    private double _shotWidth = 1;
    private double _shotHeight = 1;
    private Point? _dragStart;
    private bool _ready;

    /// <summary>用户确认后要附加的结构化 HTML（空表示未选取）。</summary>
    public string ResultHtml { get; private set; } = string.Empty;

    /// <summary>附加摘要文案（如“2 个区块，约 4.1k 字符”）。</summary>
    public string ResultSummary { get; private set; } = string.Empty;

    /// <summary>初始化弹窗。</summary>
    public PageContextDialog()
    {
        InitializeComponent();
        _logger = App.Services.GetService(typeof(ILogger)) as ILogger ?? Serilog.Log.Logger;
        BlockList.ItemsSource = _blocks;
        _ready = true;
    }

    /// <summary>ShowAsync 前调用：绑定网页外壳并抓取区块与截图。</summary>
    /// <param name="shell">网页外壳服务。</param>
    public async Task PrepareAsync(IWebShellService shell)
    {
        _shell = shell;
        await RefreshAsync();
    }

    /// <summary>抓取（或重新抓取）区块列表与页面截图。</summary>
    private async Task RefreshAsync()
    {
        if (_shell is null || !_shell.IsAvailable)
        {
            SetStatus("当前没有打开的网页：请先在网页导航里打开一个页面。");
            ShotHint.Text = "当前没有可截取的网页。";
            return;
        }

        SetStatus("正在读取页面结构…");
        _blocks.Clear();
        BlockPreview.Text = "勾选左侧区块后，这里会显示将要发给 AI 的结构化 HTML。";

        // 1) 视口尺寸（截图坐标换算用）
        var viewport = await _shell.ExecuteScriptOnActiveAsync(JsViewport);
        using (var doc = ParseObject(viewport))
        {
            if (doc is not null)
            {
                var root = doc.RootElement;
                if (root.TryGetProperty("w", out var w) && w.TryGetDouble(out var cw) && cw > 0) _clientWidth = cw;
                if (root.TryGetProperty("h", out var h) && h.TryGetDouble(out var ch) && ch > 0) _clientHeight = ch;
            }
        }

        // 2) 语义区块列表
        var raw = await _shell.ExecuteScriptOnActiveAsync(JsBlocks);
        using (var doc = ParseObject(raw))
        {
            if (doc is null)
            {
                SetStatus("页面结构解析失败（可能是空白页或浏览器内部页）。");
            }
            else if (doc.RootElement.TryGetProperty("blocks", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var b in arr.EnumerateArray())
                {
                    var tag = ReadString(b, "tag");
                    var title = ReadString(b, "title");
                    var html = ReadString(b, "html");
                    var len = b.TryGetProperty("len", out var lv) && lv.TryGetDouble(out var l) ? (int)l : 0;
                    _blocks.Add(new PageBlockItem
                    {
                        Tag = tag,
                        Title = string.IsNullOrWhiteSpace(title) ? $"<{tag}>" : title,
                        Subtitle = $"<{tag}> · 文本 {len} 字 · HTML {html.Length} 字符",
                        Html = html
                    });
                }
            }
        }

        if (_blocks.Count == 0)
        {
            SetStatus("没有识别到可成块的区域：可改用“截图框选”方式。");
        }
        else
        {
            SetStatus($"已识别 {_blocks.Count} 个区块：勾选需要的部分（可多选）。");
        }

        // 3) 页面截图
        await LoadScreenshotAsync();
    }

    /// <summary>截取当前可视区域并显示，用于拖框选取。</summary>
    private async Task LoadScreenshotAsync()
    {
        if (_shell is null) return;
        var bytes = await _shell.CapturePreviewAsync();
        if (bytes is null || bytes.Length == 0)
        {
            ShotHint.Text = "截图失败：请等页面加载完成后再点“重新抓取”。";
            return;
        }

        try
        {
            var ras = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(ras))
            {
                writer.WriteBytes(bytes);
                await writer.StoreAsync();
                await writer.FlushAsync();
                writer.DetachStream();
            }
            ras.Seek(0);

            var bmp = new BitmapImage();
            await bmp.SetSourceAsync(ras);
            ShotImage.Source = bmp;

            // 按截图宽高比缩放到弹窗内可显示尺寸
            var ratio = bmp.PixelHeight <= 0 || bmp.PixelWidth <= 0 ? 0.5 : (double)bmp.PixelHeight / bmp.PixelWidth;
            var w = 720d;
            var h = w * ratio;
            const double maxH = 230;
            if (h > maxH)
            {
                h = maxH;
                w = h / ratio;
            }
            _shotWidth = w;
            _shotHeight = h;
            ShotHost.Width = w;
            ShotHost.Height = h;
            ShotCanvas.Width = w;
            ShotCanvas.Height = h;

            ShotHint.Text = $"在截图上按住左键拖出需要的区域（页面视口 {_clientWidth:0}×{_clientHeight:0} CSS 像素）。";
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "网页截图显示失败");
            ShotHint.Text = "截图显示失败，可改用“按区块选择”。";
        }
    }

    /// <summary>重新抓取。</summary>
    private async void OnReloadClick(object sender, RoutedEventArgs e)
    {
        ShotImage.Source = null;
        ShotPreview.Text = "框选后，这里会显示反推得到的结构化 HTML。";
        await RefreshAsync();
    }

    /// <summary>切换选取方式时更新提示。</summary>
    private void OnModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready) return;
        HintText.Text = Modes.SelectedIndex == 1
            ? "截图框选：拖框后按坐标反推命中的 HTML 节点，只把该节点的结构化 HTML 交给 AI。"
            : "按区块选择：只把勾选的语义区块发给 AI，避免整页内容倾倒。";
    }

    /// <summary>选中区块时预览其 HTML。</summary>
    private void OnBlockSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (BlockList.SelectedItem is not PageBlockItem item)
        {
            BlockPreview.Text = "勾选左侧区块后，这里会显示将要发给 AI 的结构化 HTML。";
            return;
        }
        BlockPreview.Text = $"[{item.Title}]\n{item.Subtitle}\n\n{item.Html}";
    }

    /// <summary>开始拖框。</summary>
    private void OnShotPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (ShotImage.Source is null) return;
        var point = e.GetCurrentPoint(ShotCanvas);
        if (!point.Properties.IsLeftButtonPressed) return;

        _dragStart = point.Position;
        ShotCanvas.CapturePointer(e.Pointer);
        Canvas.SetLeft(ShotRect, point.Position.X);
        Canvas.SetTop(ShotRect, point.Position.Y);
        ShotRect.Width = 0;
        ShotRect.Height = 0;
        ShotRect.Visibility = Visibility.Visible;
        e.Handled = true;
    }

    /// <summary>拖框过程中更新矩形。</summary>
    private void OnShotPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_dragStart is null) return;
        UpdateRect(_dragStart.Value, e.GetCurrentPoint(ShotCanvas).Position);
        e.Handled = true;
    }

    /// <summary>松开鼠标：按坐标反推元素并生成结构化 HTML。</summary>
    private async void OnShotPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_dragStart is null) return;
        var start = _dragStart.Value;
        _dragStart = null;
        ShotCanvas.ReleasePointerCapture(e.Pointer);
        e.Handled = true;

        var rect = NormalizeRect(start, e.GetCurrentPoint(ShotCanvas).Position);
        if (rect.Width < 12 || rect.Height < 12)
        {
            ShotRect.Visibility = Visibility.Collapsed;
            SetStatus("框选范围太小：请拖出至少 12×12 的区域。");
            return;
        }
        await PickElementAsync(rect);
    }

    private void UpdateRect(Point start, Point current)
    {
        var rect = NormalizeRect(start, current);
        Canvas.SetLeft(ShotRect, rect.X);
        Canvas.SetTop(ShotRect, rect.Y);
        ShotRect.Width = rect.Width;
        ShotRect.Height = rect.Height;
    }

    private static Rect NormalizeRect(Point a, Point b) => new(
        Math.Min(a.X, b.X), Math.Min(a.Y, b.Y),
        Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));

    /// <summary>把截图上框选的像素范围换算成 CSS 坐标，调用 elementFromPoint 反推目标节点。</summary>
    private async Task PickElementAsync(Rect rect)
    {
        if (_shell is null) return;

        var scaleX = _clientWidth / Math.Max(1, _shotWidth);
        var scaleY = _clientHeight / Math.Max(1, _shotHeight);
        var left = Clamp(rect.X * scaleX, 0, _clientWidth - 1);
        var top = Clamp(rect.Y * scaleY, 0, _clientHeight - 1);
        var right = Clamp((rect.X + rect.Width) * scaleX, 1, _clientWidth);
        var bottom = Clamp((rect.Y + rect.Height) * scaleY, 1, _clientHeight);
        var cx = (left + right) / 2;
        var cy = (top + bottom) / 2;

        var script = JsPick
            .Replace("__X__", Num(cx))
            .Replace("__Y__", Num(cy))
            .Replace("__L__", Num(left))
            .Replace("__T__", Num(top))
            .Replace("__R__", Num(right))
            .Replace("__B__", Num(bottom));

        var raw = await _shell.ExecuteScriptOnActiveAsync(script);
        using var doc = ParseObject(raw);
        if (doc is null || !doc.RootElement.TryGetProperty("ok", out var ok) || ok.ValueKind != JsonValueKind.True)
        {
            SetStatus("该位置没有取到页面元素，请换个区域重新框选。");
            return;
        }

        var tag = ReadString(doc.RootElement, "tag");
        var title = ReadString(doc.RootElement, "title");
        var outline = ReadString(doc.RootElement, "outline");
        var html = ReadString(doc.RootElement, "html");
        if (string.IsNullOrWhiteSpace(html))
        {
            SetStatus("取到的节点内容为空，请换一处区域。");
            return;
        }

        var composed = $"<!-- 截图框选：<{tag}> {title} -->\n{outline}\n\n{html}";
        if (composed.Length > 14000) composed = composed[..14000] + "\n…（已截断）";

        ResultHtml = composed;
        ResultSummary = $"截图框选 1 个区域 <{tag}>，约 {composed.Length} 字符";
        ShotPreview.Text = composed;
        SetStatus($"已识别 <{tag}>{(string.IsNullOrWhiteSpace(title) ? string.Empty : " " + title)}：点“附带选中内容”即可附加。");
    }

    /// <summary>确认按钮：按当前方式收集结果；无有效内容时阻止关闭。</summary>
    private void OnPrimaryClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        if (Modes.SelectedIndex == 1)
        {
            if (string.IsNullOrEmpty(ResultHtml))
            {
                args.Cancel = true;
                SetStatus("还没有框选区域：请先在截图上拖出一个矩形。");
            }
            return;
        }

        var picked = _blocks.Where(b => b.IsSelected).ToList();
        if (picked.Count == 0)
        {
            args.Cancel = true;
            SetStatus("请至少勾选一个区块（也可以切到“截图框选”）。");
            return;
        }

        var sb = new StringBuilder();
        var index = 1;
        foreach (var b in picked)
        {
            sb.Append("<!-- 区块 ").Append(index++).Append(": <").Append(b.Tag).Append("> ")
              .Append(b.Title).AppendLine(" -->");
            sb.AppendLine(b.Html);
            sb.AppendLine();
        }
        var html = sb.ToString();
        if (html.Length > 14000) html = html[..14000] + "\n…（已截断）";

        ResultHtml = html;
        ResultSummary = $"{picked.Count} 个区块，约 {html.Length} 字符";
        ShotPreview.Text = html;
    }

    private void SetStatus(string text) => StatusText.Text = text;

    private static double Clamp(double v, double min, double max) => Math.Max(min, Math.Min(max, v));

    private static string Num(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);

    private static string ReadString(JsonElement obj, string name)
        => obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? (v.GetString() ?? string.Empty) : string.Empty;

    /// <summary>ExecuteScriptAsync 结果是“字符串的 JSON 形式”，需先解包再解析。</summary>
    private static JsonDocument? ParseObject(string raw)
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

    /// <summary>读取页面视口 CSS 尺寸。</summary>
    private const string JsViewport =
        "(function(){var d=document.documentElement;return JSON.stringify({w:d.clientWidth,h:d.clientHeight});})()";

    /// <summary>把页面拆成语义区块（外层优先，跳过嵌套重复），每个区块附清洗后的 HTML 片段。</summary>
    private const string JsBlocks = """
        (function(){
          function clean(root,max){
            var c=root.cloneNode(true);
            var junk=c.querySelectorAll('script,style,noscript,iframe,canvas,link,meta,svg');
            for(var i=0;i<junk.length;i++){ junk[i].parentNode && junk[i].parentNode.removeChild(junk[i]); }
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
          var els=[];
          var sels='header,main,article,section,aside,nav,footer,form,table,[role=main],[role=navigation],[role=complementary],[role=article]';
          var cand=document.querySelectorAll(sels);
          for(var i=0;i<cand.length;i++){ els.push(cand[i]); }
          if(els.length<3 && document.body){
            for(var j=0;j<document.body.children.length;j++){ els.push(document.body.children[j]); }
          }
          var accepted=[]; var out=[];
          for(var m=0;m<els.length && out.length<40;m++){
            var el=els[m];
            if(!el||!el.innerText) continue;
            var txt=(el.innerText||'').replace(/\s+/g,' ').trim();
            if(txt.length<40) continue;
            var dup=false;
            for(var n=0;n<accepted.length;n++){
              if(accepted[n].contains(el)||el.contains(accepted[n])){ dup=true; break; }
            }
            if(dup) continue;
            var tag=(el.tagName||'').toLowerCase();
            var hd=el.querySelector('h1,h2,h3,h4,h5');
            var title=(hd&&hd.textContent?hd.textContent:'').replace(/\s+/g,' ').trim();
            if(!title){ title=el.getAttribute('aria-label')||el.getAttribute('title')||''; }
            if(!title){ title=el.id?('#'+el.id):''; }
            if(!title&&typeof el.className==='string'&&el.className.trim()){ title='.'+el.className.trim().split(/\s+/)[0]; }
            if(!title){ title='<'+tag+'>'; }
            accepted.push(el);
            out.push({tag:tag,title:title.slice(0,60),len:txt.length,html:clean(el,1800)});
          }
          return JSON.stringify({ok:true,blocks:out});
        })()
        """;

    /// <summary>按框选坐标反推元素：以中心点 elementFromPoint 起步，向上收敛到覆盖框选范围的块。</summary>
    private const string JsPick = """
        (function(){
          function clean(root,max){
            var c=root.cloneNode(true);
            var junk=c.querySelectorAll('script,style,noscript,iframe,canvas,link,meta,svg');
            for(var i=0;i<junk.length;i++){ junk[i].parentNode && junk[i].parentNode.removeChild(junk[i]); }
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
          function outline(el,depth,maxDepth,acc){
            if(!el||depth>maxDepth||acc.count>200) return;
            var tag=(el.tagName||'').toLowerCase();
            if(!tag||['script','style','noscript','svg','iframe','canvas','template'].indexOf(tag)>=0) return;
            var attrs='';
            if(el.id) attrs+=' id="'+el.id+'"';
            var cls=(typeof el.className==='string')?el.className.trim():'';
            if(cls) attrs+=' class="'+(cls.length>60?cls.slice(0,60):cls)+'"';
            if(tag==='a'&&el.getAttribute('href')) attrs+=' href="'+el.getAttribute('href').slice(0,80)+'"';
            if(el.getAttribute('role')) attrs+=' role="'+el.getAttribute('role')+'"';
            acc.count++;
            var text='';
            if(el.children.length===0){
              text=(el.textContent||'').replace(/\s+/g,' ').trim();
              if(text.length>160) text=text.slice(0,160)+'…';
            }
            acc.s.push('  '.repeat(depth)+'<'+tag+attrs+'>'+(text?' '+text:''));
            for(var c=0;c<el.children.length;c++){ outline(el.children[c],depth+1,maxDepth,acc); }
          }
          var x=__X__, y=__Y__;
          var el=document.elementFromPoint(x,y);
          if(!el){ return JSON.stringify({ok:false,msg:'empty'}); }
          var l=__L__, t=__T__, r=__R__, b=__B__;
          var area=(r-l)*(b-t);
          var target=el;
          var cur=el;
          while(cur&&cur.parentElement){
            var rc=cur.getBoundingClientRect();
            var iw=Math.min(rc.right,r)-Math.max(rc.left,l);
            var ih=Math.min(rc.bottom,b)-Math.max(rc.top,t);
            var inter=(iw>0?iw:0)*(ih>0?ih:0);
            if(area>0 && inter/area>0.6) target=cur;
            var ptag=(cur.parentElement.tagName||'').toLowerCase();
            if(ptag==='body'||ptag==='html') break;
            cur=cur.parentElement;
          }
          var acc={s:[],count:0};
          outline(target,0,4,acc);
          var title='';
          var hd=target.querySelector&&target.querySelector('h1,h2,h3,h4');
          if(hd&&hd.textContent){ title=hd.textContent.replace(/\s+/g,' ').trim().slice(0,60); }
          if(!title&&target.getAttribute&&target.getAttribute('aria-label')){ title=target.getAttribute('aria-label'); }
          return JSON.stringify({
            ok:true,
            tag:(target.tagName||'').toLowerCase(),
            title:title,
            text:(target.innerText||'').replace(/\s+/g,' ').trim().slice(0,400),
            outline:acc.s.join('\n').slice(0,2500),
            html:clean(target,6000)
          });
        })()
        """;
}
