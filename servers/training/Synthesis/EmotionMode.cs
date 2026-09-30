namespace Z.Vtuber.Training.Synthesis;

/// <summary>
/// 情绪参考生成模式：
/// <para><see cref="Llm"/>（默认）：用配置的可用模型对输入文本做情绪识别得到八维查询向量，再在
/// <c>item_emotion_vectors</c> 检索 top-1 命中条目作为情绪参考。</para>
/// <para><see cref="Dataset"/>：跳过大模型，直接使用文本向量命中（音色候选）条目自身的
/// <c>Extensions.emotion</c> 作为情绪参考；命中条目无标注则沿音色候选向下找，全无则降级中性。</para>
/// </summary>
public enum EmotionMode
{
    /// <summary>大模型生成情感向量 + Milvus 八维检索（默认，行为不变）。</summary>
    Llm = 0,

    /// <summary>跳过大模型，直接使用向量命中条目自身的情绪标注。</summary>
    Dataset = 1,
}
