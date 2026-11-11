using System.Text.Json;
using System.Text.Json.Serialization;

namespace AI_Tool_Box.Services;

/// <summary>局域网传输协议 JSON 序列化选项（camelCase，对齐 LocalSend）。</summary>
internal static class LanJson
{
    /// <summary>共享序列化选项。</summary>
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}

/// <summary>UDP 多播发现消息（MulticastDto，字段对齐 LocalSend v2）。</summary>
internal sealed class MulticastDto
{
    public string Alias { get; set; } = string.Empty;
    public string Version { get; set; } = "2.1";
    public string? DeviceModel { get; set; }
    public string? DeviceType { get; set; }
    public string Fingerprint { get; set; } = string.Empty;
    public int? Port { get; set; }
    public string? Protocol { get; set; }
    public bool? Download { get; set; }

    /// <summary>true = 主动宣告（v2 字段 announce / v1 字段 announcement）。</summary>
    public bool? Announce { get; set; }
    public bool? Announcement { get; set; }
}

/// <summary>设备信息 DTO（register 请求体 / info 与 register 响应体）。</summary>
internal sealed class DeviceInfoDto
{
    public string Alias { get; set; } = string.Empty;
    public string Version { get; set; } = "2.1";
    public string? DeviceModel { get; set; }
    public string? DeviceType { get; set; }
    public string Fingerprint { get; set; } = string.Empty;
    public int? Port { get; set; }
    public string? Protocol { get; set; }
    public bool? Download { get; set; }
}

/// <summary>文件元数据（LocalSend FileDto 精简版）。</summary>
internal sealed class FileMetaDto
{
    public string Id { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public long Size { get; set; }
    public string? FileType { get; set; }

    /// <summary>SHA256（十六进制；大文件可为空表示跳过校验）。</summary>
    public string? Hash { get; set; }
    public string? Preview { get; set; }
}

/// <summary>prepare-upload 请求体。</summary>
internal sealed class PrepareUploadRequestDto
{
    public DeviceInfoDto? Info { get; set; }
    public Dictionary<string, FileMetaDto> Files { get; set; } = new();
}

/// <summary>prepare-upload 响应体：files 值 = 接受时的 token，或 "declined"/"busy"。</summary>
internal sealed class PrepareUploadResponseDto
{
    public string SessionId { get; set; } = string.Empty;
    public Dictionary<string, string> Files { get; set; } = new();
}
