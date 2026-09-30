using Z.Vtuber.Training.Contracts;
using Z.Vtuber.Training.Synthesis;

namespace Z.Vtuber.Tests;

/// <summary>
/// 八维情感向量工具：<see cref="EmotionVector.FromEmotion"/> 顺序、<see cref="EmotionVector.Format"/>
/// 逗号分隔口径、<see cref="EmotionVector.NormalizeEmoAlpha"/> 归一（clamp weight/10，缺省默认值）。
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

    [Fact]
    public void Format_CommaSeparatedWithoutTrailingZeros()
    {
        Assert.Equal("0,0,0.8,0,0,0,0,0", EmotionVector.Format([0f, 0f, 0.8f, 0f, 0f, 0f, 0f, 0f]));
        Assert.Equal("1,0.5,0.25,0,0,0,0,1", EmotionVector.Format([1f, 0.5f, 0.25f, 0f, 0f, 0f, 0f, 1f]));
    }

    [Fact]
    public void Format_UsesInvariantCulture()
    {
        // 小数点在不变文化下固定为 '.'（不受运行文化影响）。
        Assert.Equal("0.5", EmotionVector.Format([0.5f]).Split(',')[0]);
    }

    [Theory]
    [InlineData(0d, 0d)]
    [InlineData(5d, 0.5d)]
    [InlineData(10d, 1d)]
    [InlineData(15d, 1d)]
    [InlineData(-2d, 0d)]
    public void NormalizeEmoAlpha_ClampsWeightOverTen(double weight, double expected)
    {
        Assert.Equal(expected, EmotionVector.NormalizeEmoAlpha(Emotion(weight: weight), 1.0));
    }

    [Fact]
    public void NormalizeEmoAlpha_NullEmotion_ReturnsDefault()
    {
        Assert.Equal(0.7, EmotionVector.NormalizeEmoAlpha(null, 0.7));
    }
}
