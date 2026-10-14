using Z.Vtuber.Training.Contracts;

namespace Z.Vtuber.Training.Synthesis;

/// <summary>
/// 八维情感向量工具：维度顺序固定 <c>happy,angry,sad,afraid,disgusted,melancholic,surprised,calm</c>
/// （与 <see cref="EmotionResult"/> 一致）。仅服务内存情绪匹配（<see cref="EmotionMatcher"/>）的查询/候选向量化。
/// </summary>
public static class EmotionVector
{
    /// <summary>八维顺序。</summary>
    public static readonly string[] Dimensions =
        ["happy", "angry", "sad", "afraid", "disgusted", "melancholic", "surprised", "calm"];

    /// <summary><see cref="EmotionResult"/> → 八维向量（顺序见 <see cref="Dimensions"/>）。</summary>
    public static float[] FromEmotion(EmotionResult emotion) =>
    [
        (float)emotion.Happy,
        (float)emotion.Angry,
        (float)emotion.Sad,
        (float)emotion.Afraid,
        (float)emotion.Disgusted,
        (float)emotion.Melancholic,
        (float)emotion.Surprised,
        (float)emotion.Calm,
    ];
}
