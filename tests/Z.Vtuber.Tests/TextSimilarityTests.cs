using Z.Vtuber.Training.Synthesis;

namespace Z.Vtuber.Tests;

/// <summary>
/// 音色匹配的字符相似度口径：归一化编辑距离（Levenshtein ratio），
/// 同时反映内容差异与长度差异，取值 [0,1]，对称、确定性。
/// </summary>
public class TextSimilarityTests
{
    [Fact]
    public void Identical_ReturnsOne()
    {
        Assert.Equal(1d, TextSimilarity.Ratio("今天天气很好", "今天天气很好"), 6);
    }

    [Fact]
    public void BothEmpty_ReturnsOne()
    {
        Assert.Equal(1d, TextSimilarity.Ratio(string.Empty, string.Empty), 6);
    }

    [Fact]
    public void OneEmpty_ReturnsZero()
    {
        Assert.Equal(0d, TextSimilarity.Ratio(string.Empty, "abc"), 6);
        Assert.Equal(0d, TextSimilarity.Ratio("abc", string.Empty), 6);
    }

    [Fact]
    public void SingleSubstitution_ReflectsContentAndLength()
    {
        // 6 字替换 1 字：1 - 1/6。
        Assert.Equal(1d - 1d / 6d, TextSimilarity.Ratio("今天天气真好", "今天天气很好"), 6);
    }

    [Fact]
    public void LengthDifference_IsPenalized()
    {
        // "abc" → "abcdef" 需 3 次插入：1 - 3/6 = 0.5。
        Assert.Equal(0.5d, TextSimilarity.Ratio("abc", "abcdef"), 6);
    }

    [Fact]
    public void IsSymmetric()
    {
        var a = "要合成的文本";
        var b = "要生成的声音";
        Assert.Equal(TextSimilarity.Ratio(a, b), TextSimilarity.Ratio(b, a), 9);
    }

    [Fact]
    public void MoreSimilarText_ScoresHigher()
    {
        var query = "今天天气真好";
        var close = TextSimilarity.Ratio(query, "今天天气很好");
        var far = TextSimilarity.Ratio(query, "完全无关的长文本内容");
        Assert.True(close > far);
    }
}
