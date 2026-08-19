using Z.Vtuber.Live;

namespace Z.Vtuber.Tests;

public class LiveEventTextExtractorTests
{
    [Fact]
    public void ExtractText_ReturnsNull_WhenTypeIsDanmakuAndPayloadIsEmpty()
    {
        var result = LiveEventTextExtractor.ExtractText("danmaku", "{}");
        Assert.Null(result);
    }

    [Fact]
    public void ExtractText_ReturnsText_WhenTypeIsDanmakuAndInfoExists()
    {
        // 真实弹幕 payload 格式：{"info":["...","弹幕内容",...]}
        var payload = """{"info":["18","这是弹幕内容","123456","username"]}""";
        var result = LiveEventTextExtractor.ExtractText("danmaku", payload);
        Assert.Equal("这是弹幕内容", result);
    }

    [Fact]
    public void ExtractText_ReturnsNull_WhenTypeIsDanmakuAndInfoIsNotArray()
    {
        var payload = """{"info":"not array"}""";
        var result = LiveEventTextExtractor.ExtractText("danmaku", payload);
        Assert.Null(result);
    }

    [Fact]
    public void ExtractText_ReturnsNull_WhenTypeIsDanmakuAndInfoLengthLessThan2()
    {
        var payload = """{"info":["18"]}""";
        var result = LiveEventTextExtractor.ExtractText("danmaku", payload);
        Assert.Null(result);
    }

    [Fact]
    public void ExtractText_ReturnsNull_WhenTypeIsDanmakuAndSecondElementIsNotString()
    {
        var payload = """{"info":["18",123]}""";
        var result = LiveEventTextExtractor.ExtractText("danmaku", payload);
        Assert.Null(result);
    }

    [Fact]
    public void ExtractText_ReturnsNull_WhenTypeIsSuperChatAndPayloadIsEmpty()
    {
        var result = LiveEventTextExtractor.ExtractText("super_chat", "{}");
        Assert.Null(result);
    }

    [Fact]
    public void ExtractText_ReturnsText_WhenTypeIsSuperChatAndMessageExists()
    {
        // 真实 SC payload 格式：{"data":{"message":"SC消息内容",...}}
        var payload = """{"data":{"message":"这是SC消息","price":30}}""";
        var result = LiveEventTextExtractor.ExtractText("super_chat", payload);
        Assert.Equal("这是SC消息", result);
    }

    [Fact]
    public void ExtractText_ReturnsNull_WhenTypeIsSuperChatAndDataIsNotObject()
    {
        var payload = """{"data":"not object"}""";
        var result = LiveEventTextExtractor.ExtractText("super_chat", payload);
        Assert.Null(result);
    }

    [Fact]
    public void ExtractText_ReturnsNull_WhenTypeIsSuperChatAndMessageIsMissing()
    {
        var payload = """{"data":{"price":30}}""";
        var result = LiveEventTextExtractor.ExtractText("super_chat", payload);
        Assert.Null(result);
    }

    [Fact]
    public void ExtractText_ReturnsNull_WhenTypeIsSuperChatAndMessageIsNotString()
    {
        var payload = """{"data":{"message":123}}""";
        var result = LiveEventTextExtractor.ExtractText("super_chat", payload);
        Assert.Null(result);
    }

    [Fact]
    public void ExtractText_ReturnsNull_WhenTypeIsOtherType()
    {
        var payload = """{"data":{"message":"test"}}""";
        var result = LiveEventTextExtractor.ExtractText("gift", payload);
        Assert.Null(result);
    }

    [Fact]
    public void ExtractText_ReturnsNull_WhenPayloadIsInvalidJson()
    {
        var result = LiveEventTextExtractor.ExtractText("danmaku", "invalid json");
        Assert.Null(result);
    }

    [Fact]
    public void ExtractText_ReturnsNull_WhenPayloadIsNull()
    {
        var result = LiveEventTextExtractor.ExtractText("danmaku", null!);
        Assert.Null(result);
    }

    [Fact]
    public void ExtractText_ReturnsNull_WhenPayloadIsEmptyString()
    {
        var result = LiveEventTextExtractor.ExtractText("danmaku", "");
        Assert.Null(result);
    }

    [Fact]
    public void ExtractText_ReturnsNull_WhenPayloadIsWhitespace()
    {
        var result = LiveEventTextExtractor.ExtractText("danmaku", "   ");
        Assert.Null(result);
    }

    [Fact]
    public void ExtractText_ReturnsNull_WhenTextIsWhitespace()
    {
        var payload = """{"info":["18","   ","123456"]""";
        var result = LiveEventTextExtractor.ExtractText("danmaku", payload);
        Assert.Null(result);
    }

    [Fact]
    public void ExtractText_ReturnsText_WhenTextIsEmptyString()
    {
        // 空字符串不算空白，应该返回
        var payload = """{"info":["18","","123456"]}""";
        var result = LiveEventTextExtractor.ExtractText("danmaku", payload);
        Assert.Equal("", result);
    }

    [Fact]
    public void ExtractText_ReturnsText_WithEmojiAndSpecialChars()
    {
        var payload = """{"info":["18","🎉 Hello World! @#$%^&*()","123456"]}""";
        var result = LiveEventTextExtractor.ExtractText("danmaku", payload);
        Assert.Equal("🎉 Hello World! @#$%^&*()", result);
    }

    [Fact]
    public void ExtractText_ReturnsText_WithChineseCharacters()
    {
        var payload = """{"info":["18","你好世界","123456"]}""";
        var result = LiveEventTextExtractor.ExtractText("danmaku", payload);
        Assert.Equal("你好世界", result);
    }

    [Fact]
    public void ExtractText_ReturnsText_WithLongText()
    {
        var longText = new string('a', 1000);
        var payload = $$"""{"info":["18","{{longText}}","123456"]}""";
        var result = LiveEventTextExtractor.ExtractText("danmaku", payload);
        Assert.Equal(longText, result);
    }
}