using System.ComponentModel.DataAnnotations;

namespace Z.Vtuber.Training.Configuration;

/// <summary>training 服务配置。</summary>
public sealed class TrainingOptions
{
    /// <summary>recording-transcript-api 内网地址配置节（与既有服务的 TranscriptService 口径一致）。</summary>
    public const string SectionName = "TranscriptService";

    public const string DefaultBaseUrl = "http://recording-transcript-api";
}

/// <summary>AI 模型调用配置（节 <c>Ai</c>）。</summary>
public sealed class AiOptions
{
    public const string SectionName = "Ai";

    /// <summary>模型调用/模型同步请求超时（秒）。</summary>
    [Range(1, 3600)]
    public int RequestTimeoutSeconds { get; set; } = 120;

    /// <summary>音频片段字节上限（超过 → 400 audio too large）。默认 8 MiB。</summary>
    [Range(1, 1_073_741_824)]
    public long MaxAudioBytes { get; set; } = 8 * 1024 * 1024;

    /// <summary>
    /// 条目 <c>endMs</c> 为空时服务端兜底片段时长（毫秒）——recording 片段端点要求 endMs 必填，
    /// 与前端 <c>trainingAudioClipUrl</c> 同口径（默认 300000 = 5 分钟）。
    /// </summary>
    [Range(1, 3_600_000)]
    public int AudioClipDefaultDurationMs { get; set; } = 300_000;
}

/// <summary>情绪识别任务配置（节 <c>EmotionTask</c>）。</summary>
public sealed class EmotionTaskOptions
{
    public const string SectionName = "EmotionTask";

    /// <summary>扫描可执行任务的周期（秒）。</summary>
    [Range(1, 3600)]
    public int ScanIntervalSeconds { get; set; } = 10;

    /// <summary>扫描器单轮最多领取的任务数。</summary>
    [Range(1, 10000)]
    public int ScanBatchSize { get; set; } = 10;

    /// <summary>任务锁 TTL（秒）。</summary>
    [Range(1, 3600)]
    public int LockTtlSeconds { get; set; } = 30;

    /// <summary>任务锁续期间隔（秒）。</summary>
    [Range(1, 3600)]
    public int LockRenewIntervalSeconds { get; set; } = 10;

    /// <summary>并发消费上限（低并发防限流）。</summary>
    [Range(1, 100)]
    public int MaxConcurrentTasks { get; set; } = 2;

    /// <summary>单任务最大执行次数（含失败重试），超限置 FAILED 不再接管。</summary>
    [Range(1, 100)]
    public int MaxAttempts { get; set; } = 3;
}

/// <summary>recording 服务地址配置（节 <c>RecordingService</c>）。</summary>
public sealed class RecordingServiceOptions
{
    public const string SectionName = "RecordingService";

    public const string DefaultBaseUrl = "http://recording";

    /// <summary>recording 服务内网地址（取音频片段）。</summary>
    [Required]
    public string BaseUrl { get; set; } = DefaultBaseUrl;
}
