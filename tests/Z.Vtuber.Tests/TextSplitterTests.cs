using Z.Vtuber.Training.Synthesis;

namespace Z.Vtuber.Tests;

/// <summary>断句纯函数：固定分隔符 <c>[，。,.\s]+</c>、Trim、去空段、保序、纯标点为空。</summary>
public class TextSplitterTests
{
    [Theory]
    [InlineData("你好，世界。", new[] { "你好", "世界" })]
    [InlineData("a,b.c\td\ne", new[] { "a", "b", "c", "d", "e" })]
    [InlineData("  前后空白  ", new[] { "前后空白" })]
    public void Split_SeparatesAndTrims(string text, string[] expected)
    {
        Assert.Equal(expected, TextSplitter.Split(text));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("，。 , . \t")]
    public void Split_NoContent_ReturnsEmpty(string text)
    {
        Assert.Empty(TextSplitter.Split(text));
    }

    [Fact]
    public void Split_MixedPunctuationAndGaps_DropsEmptySegments_PreservesOrder()
    {
        var result = TextSplitter.Split("，第一句。。第二句，,  第三句。");

        Assert.Equal(new[] { "第一句", "第二句", "第三句" }, result);
    }

    [Fact]
    public void Split_IsDeterministic()
    {
        const string text = "甲，乙。丙";
        Assert.Equal(TextSplitter.Split(text), TextSplitter.Split(text));
    }
}
