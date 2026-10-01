using System.ComponentModel.DataAnnotations;

namespace Z.Vtuber.Training.Configuration;

/// <summary>合成编排配置（节 <c>Synthesize</c>）。</summary>
public sealed class SynthesizeOptions
{
    public const string SectionName = "Synthesize";

    /// <summary>参考音频候选数上限（音色/情绪逐候选兜底）。</summary>
    [Range(1, 1000)]
    public int ReferenceCandidateLimit { get; set; } = 16;

    /// <summary>参与拼接的数据条数上限（拼音短句多命中按数据去重后截断）。</summary>
    [Range(1, 1000)]
    public int MergedReferenceLimit { get; set; } = 8;

    /// <summary>
    /// 参与拼接的片段总时长上限（毫秒，按 <c>endMs</c> 为空时以 <c>Ai:AudioClipDefaultDurationMs</c> 兜底的片段时长累加）——
    /// 与 recording 侧 <c>Recording:AudioMergeMaxTotalDurationMs</c> 同口径，避免合并请求因总时长超限被 400 后静默降级为单片段。
    /// </summary>
    [Range(1, 86_400_000)]
    public int MergedReferenceMaxTotalDurationMs { get; set; } = 600_000;
}
