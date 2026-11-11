using AI_Tool_Box.Models;

namespace AI_Tool_Box.Services;

/// <summary>网页外壳服务契约：管理多标签 WebView2、地址栏、书签、历史、下载，并对外暴露当前网页内容供 SnifferPage 等页面跨页面抓取。</summary>
public interface IWebShellService
{
    /// <summary>获取一个值，指示地址栏是否可见（仅网址导航模式显示）。</summary>
    bool IsAddressBarVisible { get; }

    /// <summary>获取一个值，指示 WebShell 是否可见（仅网址导航/AI/嗅探器模式显示）。</summary>
    bool IsVisible { get; }

    /// <summary>获取一个值，指示当前是否有可抓取的网页内容。</summary>
    bool IsAvailable { get; }

    /// <summary>抓取当前网页正文文本；若无可用页面返回 null。</summary>
    /// <returns>正文文本或 null。</returns>
    Task<string?> GetContentAsync();

    /// <summary>截取当前活动页可视区域为 PNG（用于“截图框选网页内容”）。</summary>
    /// <returns>PNG 字节；无可用页面或失败返回 null。</returns>
    Task<byte[]?> CapturePreviewAsync();

    /// <summary>进入“开发者工具”式元素拾取：网页上悬停高亮、点击选中对应 HTML 区域。</summary>
    /// <param name="ct">取消令牌（用户再次点击按钮或按 Esc 时取消）。</param>
    /// <returns>结构化 HTML 片段（含元素大纲）；取消、超时或无页面时返回 null。</returns>
    Task<string?> PickElementOnPageAsync(CancellationToken ct = default);

    /// <summary>进入“区域拾取”模式（借鉴页面拾取的交互，但只取区域身份）：点击网页元素，
    /// 返回该区域的结构化标识（CSS 选择器 + 结构描述），<b>不采集区域内的文本内容</b>
    /// ——同一站点每题布局相同，选择器可复用；区域内文字每题不同，不能固化。</summary>
    /// <param name="ct">取消令牌（用户再次点击按钮或按 Esc 时取消）。</param>
    /// <returns>区域拾取结果；取消、超时或无页面时返回 null。</returns>
    Task<RegionPickResult?> PickRegionAsync(CancellationToken ct = default);

    /// <summary>请求取消当前正在进行的页面/区域拾取（供全局 Esc 快捷键调用）。
    /// 即使焦点不在网页内（例如停在“拾取”按钮上）也能生效。</summary>
    void CancelActivePick();

    /// <summary>按 CSS 选择器点击当前页面元素（自动滚动到可视区）。</summary>
    /// <param name="selector">CSS 选择器。</param>
    /// <returns>是否找到并点击成功。</returns>
    Task<bool> ClickAsync(string selector);

    /// <summary>按 CSS 选择器清空并写入文本（触发 input/change，兼容 textarea/input/contenteditable）。</summary>
    /// <param name="selector">CSS 选择器。</param>
    /// <param name="text">待写入文本。</param>
    /// <returns>是否写入成功。</returns>
    Task<bool> SetValueAsync(string selector, string text);

    /// <summary>按 CSS 选择器读取元素文本（innerText/textContent）。</summary>
    /// <param name="selector">CSS 选择器。</param>
    /// <returns>元素文本；元素不存在或无可用页面时返回 null。</returns>
    Task<string?> ReadTextAsync(string selector);

    /// <summary>获取或设置 WebShell 可见性；切换为 false 时隐藏整个网页区域。</summary>
    /// <param name="visible">是否可见。</param>
    /// <param name="addressBarVisible">地址栏是否可见（仅 visible=true 时有效）。</param>
    void SetVisibility(bool visible, bool addressBarVisible);

    /// <summary>在当前活动标签页导航到指定 URL。</summary>
    /// <param name="url">目标 URL。</param>
    /// <returns>导航是否成功发起。</returns>
    Task<bool> NavigateAsync(string url);

    /// <summary>获取当前活动标签页的 URL。</summary>
    string? CurrentUrl { get; }

    /// <summary>新书签添加事件。</summary>
    event EventHandler<BookmarkItem>? BookmarkAdded;

    /// <summary>历史记录新增事件。</summary>
    event EventHandler<HistoryItem>? HistoryAdded;

    /// <summary>下载记录新增/更新事件。</summary>
    event EventHandler<DownloadItem>? DownloadChanged;

    /// <summary>加载所有书签。</summary>
    /// <returns>书签列表。</returns>
    Task<IReadOnlyList<BookmarkItem>> LoadBookmarksAsync();

    /// <summary>添加当前页面为书签。</summary>
    /// <returns>是否添加成功。</returns>
    Task<bool> AddBookmarkAsync();

    /// <summary>删除书签。</summary>
    /// <param name="url">目标 URL。</param>
    Task RemoveBookmarkAsync(string url);

    /// <summary>加载历史记录（最近 N 条）。</summary>
    /// <param name="limit">最大条数。</param>
    /// <returns>历史记录列表。</returns>
    Task<IReadOnlyList<HistoryItem>> LoadHistoryAsync(int limit = 200);

    /// <summary>清空历史记录。</summary>
    Task ClearHistoryAsync();

    /// <summary>加载所有下载记录。</summary>
    /// <returns>下载记录列表。</returns>
    Task<IReadOnlyList<DownloadItem>> LoadDownloadsAsync();

    /// <summary>新建一个空白标签页。</summary>
    void NewTab();

    /// <summary>关闭指定 URL 对应的标签页。</summary>
    /// <param name="url">标签页 URL。</param>
    void CloseTab(string url);

    /// <summary>将文本注入到当前网页中获得焦点的输入框（textarea/input contenteditable 等）。</summary>
    /// <param name="text">待注入文本。</param>
    /// <returns>是否执行成功（若当前无可用 WebView2 返回 false）。</returns>
    Task<bool> InjectToActiveElementAsync(string text);

    /// <summary>后退到上一页（当前活动标签）。</summary>
    void GoBack();

    /// <summary>前进到下一页（当前活动标签）。</summary>
    void GoForward();

    /// <summary>刷新当前活动标签页。</summary>
    void Refresh();

    /// <summary>在当前活动 WebView2 执行 JS（返回 JSON 序列化结果字符串；无可用 WebView2 返回 "null"）。</summary>
    /// <param name="script">JavaScript 代码。</param>
    Task<string> ExecuteScriptOnActiveAsync(string script);

    /// <summary>活动标签或活动 CoreWebView2 切换时触发（用于嗅探器重挂钩）。</summary>
    event EventHandler? ActivePageChanged;

    /// <summary>右侧 AI 辅助侧栏是否展开。true=开，false=关。</summary>
    bool IsAiAssistOpen { get; set; }
}
