using System.Globalization;
using Z.Vtuber.Training.Contracts;

namespace Z.Vtuber.Training.Synthesis;

/// <summary>
/// 八维情感向量工具：维度顺序固定 <c>happy,angry,sad,afraid,disgusted,melancholic,surprised,calm</c>
/// （与 <see cref="EmotionResult"/> 及上游 index-tts-api <c>emo_vector</c> 一致）。
/// </summary>
public static class EmotionVector
{
    /// <summary>八维顺序（与上游 <c>emo_vector</c> 一致）。</summary>
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

    /// <summary>八值逗号分隔（不变文化、去尾零），如 <c>0,0,0.8,0,0,0,0,0</c>。</summary>
    public static string Format(IReadOnlyList<float> vector) =>
        string.Join(",", vector.Select(v => ((double)v).ToString("0.#####", CultureInfo.InvariantCulture)));

    /// <summary>
    /// <c>emo_alpha</c> 归一：<c>clamp(emotionControlWeight / 10, 0, 1)</c>；无情绪标注 → <paramref name="defaultAlpha"/>。
    /// </summary>
    public static double NormalizeEmoAlpha(EmotionResult? emotion, double defaultAlpha)
    {
        if (emotion is null)
        {
            return defaultAlpha;
        }

        var value = emotion.EmotionControlWeight / 10d;
        if (double.IsNaN(value))
        {
            return defaultAlpha;
        }

        return Math.Clamp(value, 0d, 1d);
    }
}
