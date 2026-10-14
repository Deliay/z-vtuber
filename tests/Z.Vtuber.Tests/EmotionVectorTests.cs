using Z.Vtuber.Training.Contracts;
using Z.Vtuber.Training.Synthesis;

namespace Z.Vtuber.Tests;

/// <summary>
/// 八维情感向量工具：<see cref="EmotionVector.FromEmotion"/> 顺序与 <see cref="EmotionVector.Dimensions"/>
/// （内存情绪匹配的查询/候选向量化）。
/// </summary>
public class EmotionVectorTests
{
    private static EmotionResult Emotion(
        double happy = 0, double angry = 0, double sad = 0, double afraid = 0,
        double disgusted = 0, double melancholic = 0, double surprised = 0, double calm = 0,
        double weight = 0, string description = "") =>
        new(weight, description, happy, angry, sad, afraid, disgusted, melancholic, surprised, calm);

    [Fact]
    public void FromEmotion_UsesFixedDimensionOrder()
    {
        var vector = EmotionVector.FromEmotion(
            Emotion(happy: 0.1, angry: 0.2, sad: 0.3, afraid: 0.4, disgusted: 0.5,
                melancholic: 0.6, surprised: 0.7, calm: 0.8));

        Assert.Equal([0.1f, 0.2f, 0.3f, 0.4f, 0.5f, 0.6f, 0.7f, 0.8f], vector);
        Assert.Equal(
            ["happy", "angry", "sad", "afraid", "disgusted", "melancholic", "surprised", "calm"],
            EmotionVector.Dimensions);
    }
}
