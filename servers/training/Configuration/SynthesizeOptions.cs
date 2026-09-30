using System.ComponentModel.DataAnnotations;

namespace Z.Vtuber.Training.Configuration;

/// <summary>合成编排配置（节 <c>Synthesize</c>）。</summary>
public sealed class SynthesizeOptions
{
    public const string SectionName = "Synthesize";

    /// <summary>
    /// 情感向量生成所用可用模型 id（Models 管理配置）；缺省再取首个启用模型；均无则情绪降级中性。
    /// </summary>
    public string? DefaultEmotionModelId { get; set; }

    /// <summary>参考音频候选数上限（音色/情绪逐候选兜底）。</summary>
    [Range(1, 1000)]
    public int ReferenceCandidateLimit { get; set; } = 16;
}
