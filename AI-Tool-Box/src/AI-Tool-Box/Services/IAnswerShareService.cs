using System.Collections.ObjectModel;
using AI_Tool_Box.Models;

namespace AI_Tool_Box.Services;

/// <summary>题库分享服务：本地持久化 + 局域网广播发现 + UDP 查询 + TCP 返回命中答案 + 黑白名单。
/// UDP 端口：9529（Hello/Query 广播）
/// TCP 端口：9530（推送匹配结果）</summary>
public interface IAnswerShareService : IDisposable
{
    /// <summary>本机题库（UI 直接绑定 ObservableCollection）。</summary>
    ObservableCollection<AnswerItem> LocalItems { get; }

    /// <summary>局域网共享返回的命中结果（来源各异）。</summary>
    ObservableCollection<AnswerMatch> LanResults { get; }

    /// <summary>题库共享在线设备（从 UDP Hello 维护）。</summary>
    ObservableCollection<LanPeer> OnlinePeers { get; }

    /// <summary>共享设置：Mode + 黑白名单。</summary>
    AnswerShareSettings Settings { get; }

    /// <summary>是否已开启共享（UDP+TCP 监听）。</summary>
    bool IsSharing { get; }

    /// <summary>本机 IP，用于 UDP FromIp 字段。</summary>
    string LocalIp { get; }

    /// <summary>本机 TCP 监听端口（默认 9530，冲突自动 +1）。</summary>
    int LocalTcpPort { get; }

    /// <summary>持久化设置文件路径。</summary>
    string SettingsPath { get; }

    /// <summary>持久化题库文件路径。</summary>
    string AnswersPath { get; }

    /// <summary>开启题库共享：UDP 广播 Hello + TCP 接收结果；监听 UDP Query 返回给请求方答案。</summary>
    bool StartSharing();

    /// <summary>停止共享。</summary>
    void StopSharing();

    /// <summary>添加/更新本机题库；立即写入 JSON 文件。</summary>
    void SaveLocal(AnswerItem item);

    /// <summary>删除本机题库条目。</summary>
    bool DeleteLocal(string id);

    /// <summary>本地搜索（简单包含匹配，忽略大小写+空格）。返回 Top limit。</summary>
    IEnumerable<AnswerItem> SearchLocal(string keyword, int limit = 20);

    /// <summary>清空 LanResults，并通过 UDP 广播关键词查询局域网所有设备；等待 timeout 秒后自动把累计结果绑定到 LanResults。</summary>
    /// <returns>任务在 timeout 后完成。</returns>
    Task SearchLanAsync(string keyword, int timeoutSeconds = 5);

    /// <summary>保存 Settings 到 JSON。</summary>
    void SaveSettings();

    /// <summary>重新加载 Settings。</summary>
    void LoadSettings();
}
