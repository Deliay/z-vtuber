using System.ComponentModel.DataAnnotations;

namespace Z.Vtuber.Recording.Configuration;

public sealed class RecordingOptions
{
    public const string SectionName = "Recording";

    /// <summary>录播文件存储根目录（按 {roomId}/ 子目录组织）。</summary>
    [Required]
    public string StorageDir { get; set; } = "/data/recordings";

    /// <summary>
    /// 本服务可达的基础地址，用于拼接 recording-started/finished 事件中的实时音频与录播文件 URL
    /// （实时音频 /api/{roomId}/stream/audio、录播文件 /api/{roomId}/recordings/{fileName}）。
    /// </summary>
    [Required]
    public string PublicBaseUrl { get; set; } = "http://recording";

    /// <summary>开播轮询间隔（秒）。</summary>
    [Range(1, 3600)]
    public int PollIntervalSeconds { get; set; } = 30;

    /// <summary>录制锁 TTL（秒）。</summary>
    [Range(1, 3600)]
    public int LockTtlSeconds { get; set; } = 15;

    /// <summary>录制锁续期间隔（秒）。</summary>
    [Range(1, 3600)]
    public int LockRenewIntervalSeconds { get; set; } = 5;

    /// <summary>录制期间 fencing 校验（锁归属）间隔（毫秒）。</summary>
    [Range(50, 60000)]
    public int FencingIntervalMilliseconds { get; set; } = 2000;

    /// <summary>判定 recording 状态过期的阈值（秒）：超过该时长未被心跳刷新则视为陈旧，可用于纠正残留状态。</summary>
    [Range(1, 86400)]
    public int StaleStatusThresholdSeconds { get; set; } = 180;

    /// <summary>每个音频订阅者的缓冲上限（字节）。</summary>
    [Range(1024, 64 * 1024 * 1024)]
    public int AudioBufferBytes { get; set; } = 262144;

    /// <summary>playurl 未提供码率时的回退默认码率（kbps）。</summary>
    [Range(1, 100000)]
    public int DefaultBitrateKbps { get; set; } = 4000;

    /// <summary>playurl 未提供码率时是否用 ffprobe 短时探测，仍不可得回退默认码率。</summary>
    public bool ProbeBitrate { get; set; } = true;

    /// <summary>ffprobe 可执行文件路径（ProbeBitrate 启用时使用）。</summary>
    [Required]
    public string FfprobePath { get; set; } = "ffprobe";

    /// <summary>ffmpeg 可执行文件路径。</summary>
    [Required]
    public string FfmpegPath { get; set; } = "ffmpeg";

    /// <summary>拉流携带的 Referer。</summary>
    [Required]
    public string Referer { get; set; } = "https://live.bilibili.com/";

    /// <summary>拉流携带的 User-Agent。</summary>
    [Required]
    public string UserAgent { get; set; } =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36";

    public Av1Options Av1 { get; set; } = new();

    public sealed class Av1Options
    {
        /// <summary>是否启用 AV1 二压输出；无 GPU/NVENC 环境可置 false 降级为仅 RAW + 音频。</summary>
        public bool Enabled { get; set; } = true;
    }
}
