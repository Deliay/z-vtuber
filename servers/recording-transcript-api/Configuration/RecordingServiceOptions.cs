using System.ComponentModel.DataAnnotations;

namespace Z.Vtuber.RecordingTranscript.Api.Configuration;

public sealed class RecordingServiceOptions
{
    public const string SectionName = "RecordingService";
    public const string DefaultBaseUrl = "http://recording/";

    /// <summary>recording 服务内网地址（批量查询录播文件磁盘状态）。</summary>
    [Required]
    public string BaseUrl { get; set; } = DefaultBaseUrl;
}
