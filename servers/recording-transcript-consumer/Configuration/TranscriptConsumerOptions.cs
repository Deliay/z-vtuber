using System.ComponentModel.DataAnnotations;

namespace Z.Vtuber.RecordingTranscript.Consumer.Configuration;

public sealed class TranscriptConsumerOptions
{
    public const string SectionName = "TranscriptConsumer";

    /// <summary>扫描可执行任务（PENDING / RUNNING 无锁）的周期（秒）。</summary>
    [Range(1, 3600)]
    public int ScanIntervalSeconds { get; set; } = 10;

    /// <summary>任务锁 TTL（秒）。</summary>
    [Range(1, 3600)]
    public int LockTtlSeconds { get; set; } = 30;

    /// <summary>任务锁续期间隔（秒）。</summary>
    [Range(1, 3600)]
    public int LockRenewIntervalSeconds { get; set; } = 10;

    /// <summary>音频有界缓冲上限（秒），满则暂停读 ffmpeg stdout 形成背压。</summary>
    [Range(1, 3600)]
    public int AudioBufferSeconds { get; set; } = 60;

    /// <summary>对账补建周期（秒）。</summary>
    [Range(1, 86400)]
    public int ReconcileIntervalSeconds { get; set; } = 300;

    /// <summary>扫描器单轮最多处理的任务数。</summary>
    [Range(1, 10000)]
    public int ScanBatchSize { get; set; } = 50;

    /// <summary>ffmpeg 可执行文件路径。</summary>
    [Required]
    public string FfmpegPath { get; set; } = "ffmpeg";

    /// <summary>FunASR realtime websocket 端点。</summary>
    [Required]
    public string AsrEndpoint { get; set; } = "wss://dashscope.aliyuncs.com/api-ws/v1/inference";

    /// <summary>FunASR 模型名。</summary>
    [Required]
    public string AsrModel { get; set; } = "qwen-fun-asr-streaming";

    /// <summary>
    /// FunASR websocket 停滞超时（秒）：超过该时长未收到任何服务端消息（含等待 task-finished 阶段）
    /// 即视为识别卡死，抛异常使任务 FAILED 可重试，避免任务永久悬挂并占住并发额度。
    /// </summary>
    [Range(1, 3600)]
    public int AsrIdleTimeoutSeconds { get; set; } = 60;
}

public sealed class RecordingServiceOptions
{
    public const string SectionName = "RecordingService";
    public const string DefaultBaseUrl = "http://recording/";

    /// <summary>recording 服务内网地址（拼接实时音频流与录播文件 URL）。</summary>
    [Required]
    public string BaseUrl { get; set; } = DefaultBaseUrl;
}
