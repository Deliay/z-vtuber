using Z.Vtuber.Training.Synthesis;

namespace Z.Vtuber.Tests;

/// <summary>拼音相似度：音节级归一化编辑距离——同序列为 1、近音高分、长度惩罚、对称、空序列口径。</summary>
public class PinyinSimilarityTests
{
    private static IReadOnlyList<string> S(params string[] tokens) => tokens;

    [Fact]
    public void Ratio_IdenticalSequence_IsOne()
    {
        Assert.Equal(1d, PinyinSimilarity.Ratio(S("jin", "tian"), S("jin", "tian")));
    }

    [Fact]
    public void Ratio_BothEmpty_IsOne()
    {
        Assert.Equal(1d, PinyinSimilarity.Ratio(S(), S()));
    }

    [Fact]
    public void Ratio_OneEmpty_IsZero()
    {
        Assert.Equal(0d, PinyinSimilarity.Ratio(S("jin"), S()));
        Assert.Equal(0d, PinyinSimilarity.Ratio(S(), S("jin")));
    }

    [Fact]
    public void Ratio_SingleSubstitution_ReflectsContentDifference()
    {
        // [jin,tian,qi] vs [jin,tian,hao]：1 处替换 / 长度 3
        Assert.Equal(2d / 3d, PinyinSimilarity.Ratio(S("jin", "tian", "qi"), S("jin", "tian", "hao")), 5);
    }

    [Fact]
    public void Ratio_LengthPenalty_UsesLongerSequenceAsDenominator()
    {
        // 单音节 vs 4 音节：距离 3、分母 4 → 0.25
        Assert.Equal(0.25d, PinyinSimilarity.Ratio(S("a"), S("a", "b", "c", "d")), 5);
    }

    [Fact]
    public void Ratio_NearSound_ScoresHigherThanUnrelated()
    {
        var query = S("wo", "xi", "huan", "chi", "ping", "guo");
        var near = S("wo", "xi", "huan", "chi", "ping", "guo2"); // 近似（仅尾音节不同）
        var unrelated = S("jin", "tian", "tian", "qi");

        Assert.True(PinyinSimilarity.Ratio(query, near) > PinyinSimilarity.Ratio(query, unrelated));
    }

    [Fact]
    public void Ratio_IsSymmetric()
    {
        var a = S("jin", "tian", "qi");
        var b = S("jin", "tian", "hao", "ma");
        Assert.Equal(PinyinSimilarity.Ratio(a, b), PinyinSimilarity.Ratio(b, a), 10);
    }
}
