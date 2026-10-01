using Z.Vtuber.Training.Synthesis;

namespace Z.Vtuber.Tests;

/// <summary>拼音转换：无声调小写、同音字同音节、ASCII 连续段归一、非中文丢弃、落库 join 可还原、确定性。</summary>
public class PinyinConverterTests
{
    [Fact]
    public void ToSyllables_HanyuToLowercaseToneless()
    {
        Assert.Equal(new[] { "jin", "tian" }, PinyinConverter.ToSyllables("今天"));
    }

    [Fact]
    public void ToSyllables_Homophones_ShareSameSyllables()
    {
        // 「的/得/地」默认读音中 的/得 同为 de（地读 di），与输入断句口径无关，仅验证同音节归并。
        Assert.Equal(
            PinyinConverter.ToSyllables("今天得天气"),
            PinyinConverter.ToSyllables("今天的天气"));
    }

    [Fact]
    public void ToSyllables_AsciiLettersAndDigits_CollapseToSingleLowercaseToken()
    {
        Assert.Equal(
            new[] { "ni", "hao", "abc123" },
            PinyinConverter.ToSyllables("你好ABC123"));
    }

    [Fact]
    public void ToSyllables_DropsPunctuationAndWhitespace()
    {
        Assert.Equal(
            new[] { "ni", "hao", "world" },
            PinyinConverter.ToSyllables("你好，world!"));
    }

    [Fact]
    public void ToPinyin_JoinsWithSingleSpace_AndSplitRestoresSyllables()
    {
        var pinyin = PinyinConverter.ToPinyin("你好world");
        Assert.Equal("ni hao world", pinyin);
        Assert.Equal(new[] { "ni", "hao", "world" }, PinyinConverter.Split(pinyin));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("，。 !")]
    public void ToSyllables_NoPronounceableContent_ReturnsEmpty(string text)
    {
        Assert.Empty(PinyinConverter.ToSyllables(text));
        Assert.Empty(PinyinConverter.Split(PinyinConverter.ToPinyin(text)));
    }

    [Fact]
    public void ToSyllables_IsDeterministic()
    {
        const string text = "重复文本ABC";
        Assert.Equal(PinyinConverter.ToSyllables(text), PinyinConverter.ToSyllables(text));
    }
}
