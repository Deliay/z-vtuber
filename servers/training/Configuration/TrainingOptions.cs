namespace Z.Vtuber.Training.Configuration;

/// <summary>training 服务配置。</summary>
public sealed class TrainingOptions
{
    /// <summary>recording-transcript-api 内网地址配置节（与既有服务的 TranscriptService 口径一致）。</summary>
    public const string SectionName = "TranscriptService";

    public const string DefaultBaseUrl = "http://recording-transcript-api";
}
