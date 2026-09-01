using Z.Vtuber.Crawler.Live;
using Z.Vtuber.Common.Live;

namespace Z.Vtuber.Tests;

public class LiveEventTextExtractorTests
{
    [Fact]
    public void Extract_ReturnsNull_WhenTypeIsDanmakuAndPayloadIsEmpty()
    {
        var result = LiveEventTextExtractor.Extract("danmaku", "{}");
        Assert.Null(result);
    }

    [Fact]
    public void Extract_ReturnsTextAndSenders_WhenTypeIsDanmakuAndInfoExists()
    {
        // 真实弹幕 payload 格式：{"info":["...", "弹幕内容", [uid, uname, ...], [medalLevel, medalName, ...]]}
        var payload = """{"info":["18","这是弹幕内容",[403496,"Zero_Ash",123],[25,"小粉螈","枝堇Sumire"]]}""";
        var result = LiveEventTextExtractor.Extract("danmaku", payload);
        Assert.NotNull(result);
        Assert.Equal("这是弹幕内容", result.Text);
        Assert.Equal("Zero_Ash", result.SenderName);
        Assert.Equal(403496, result.SenderUid);
        Assert.Equal("小粉螈", result.SenderMedal);
    }

    [Fact]
    public void Extract_ReturnsNull_WhenTypeIsDanmakuAndInfoIsNotArray()
    {
        var payload = """{"info":"not array"}""";
        var result = LiveEventTextExtractor.Extract("danmaku", payload);
        Assert.Null(result);
    }

    [Fact]
    public void Extract_ReturnsNull_WhenTypeIsDanmakuAndInfoLengthLessThan2()
    {
        var payload = """{"info":["18"]}""";
        var result = LiveEventTextExtractor.Extract("danmaku", payload);
        Assert.Null(result);
    }

    [Fact]
    public void Extract_ReturnsNull_WhenTypeIsDanmakuAndSecondElementIsNotString()
    {
        var payload = """{"info":["18",123]}""";
        var result = LiveEventTextExtractor.Extract("danmaku", payload);
        Assert.Null(result);
    }

    [Fact]
    public void Extract_ReturnsNull_WhenTypeIsSuperChatAndPayloadIsEmpty()
    {
        var result = LiveEventTextExtractor.Extract("super_chat", "{}");
        Assert.Null(result);
    }

    [Fact]
    public void Extract_ReturnsTextAndSenders_WhenTypeIsSuperChatAndMessageExists()
    {
        // 真实 SC payload 格式：{"data":{"message":"SC消息内容", "uid":xxx, "user_info":{"uname":"xxx"}, "medal_info":{"medal_name":"xxx"}}}
        var payload = """{"data":{"message":"这是SC消息","uid":663620883,"user_info":{"uname":"lxl-lxl"},"medal_info":{"medal_name":"小粉螈"}}}""";
        var result = LiveEventTextExtractor.Extract("super_chat", payload);
        Assert.NotNull(result);
        Assert.Equal("这是SC消息", result.Text);
        Assert.Equal("lxl-lxl", result.SenderName);
        Assert.Equal(663620883, result.SenderUid);
        Assert.Equal("小粉螈", result.SenderMedal);
    }

    [Fact]
    public void Extract_ReturnsNull_WhenTypeIsSuperChatAndDataIsNotObject()
    {
        var payload = """{"data":"not object"}""";
        var result = LiveEventTextExtractor.Extract("super_chat", payload);
        Assert.Null(result);
    }

    [Fact]
    public void Extract_ReturnsNull_WhenTypeIsSuperChatAndMessageIsMissing()
    {
        var payload = """{"data":{"price":30}}""";
        var result = LiveEventTextExtractor.Extract("super_chat", payload);
        Assert.Null(result);
    }

    [Fact]
    public void Extract_ReturnsNull_WhenTypeIsSuperChatAndMessageIsNotString()
    {
        var payload = """{"data":{"message":123}}""";
        var result = LiveEventTextExtractor.Extract("super_chat", payload);
        Assert.Null(result);
    }

    [Fact]
    public void Extract_ReturnsNull_WhenTypeIsOtherType()
    {
        var payload = """{"data":{"message":"test"}}""";
        var result = LiveEventTextExtractor.Extract("gift", payload);
        Assert.Null(result);
    }

    [Fact]
    public void Extract_ReturnsNull_WhenPayloadIsInvalidJson()
    {
        var result = LiveEventTextExtractor.Extract("danmaku", "invalid json");
        Assert.Null(result);
    }

    [Fact]
    public void Extract_ReturnsNull_WhenPayloadIsNull()
    {
        var result = LiveEventTextExtractor.Extract("danmaku", null!);
        Assert.Null(result);
    }

    [Fact]
    public void Extract_ReturnsNull_WhenPayloadIsEmptyString()
    {
        var result = LiveEventTextExtractor.Extract("danmaku", "");
        Assert.Null(result);
    }

    [Fact]
    public void Extract_ReturnsNull_WhenPayloadIsWhitespace()
    {
        var result = LiveEventTextExtractor.Extract("danmaku", "   ");
        Assert.Null(result);
    }

    [Fact]
    public void Extract_ReturnsNull_WhenTextIsWhitespace()
    {
        // 纯空白弹幕不应写入索引，返回 null
        var payload = """{"info":["18","   ",[403496,"Zero_Ash"]]}""";
        var result = LiveEventTextExtractor.Extract("danmaku", payload);
        Assert.Null(result);
    }

    [Fact]
    public void Extract_ReturnsNull_WhenTextIsEmptyString()
    {
        // 空字符串弹幕不应写入索引，返回 null
        var payload = """{"info":["18","",[403496,"Zero_Ash"]]}""";
        var result = LiveEventTextExtractor.Extract("danmaku", payload);
        Assert.Null(result);
    }

    [Fact]
    public void Extract_ReturnsText_WithEmojiAndSpecialChars()
    {
        var payload = """{"info":["18","🎉 Hello World! @#$%^&*()",[403496,"Zero_Ash"]]}""";
        var result = LiveEventTextExtractor.Extract("danmaku", payload);
        Assert.NotNull(result);
        Assert.Equal("🎉 Hello World! @#$%^&*()", result.Text);
    }

    [Fact]
    public void Extract_ReturnsText_WithChineseCharacters()
    {
        var payload = """{"info":["18","你好世界",[403496,"Zero_Ash"]]}""";
        var result = LiveEventTextExtractor.Extract("danmaku", payload);
        Assert.NotNull(result);
        Assert.Equal("你好世界", result.Text);
    }

    [Fact]
    public void Extract_ReturnsText_WithLongText()
    {
        var longText = new string('a', 1000);
        var payload = $$"""{"info":["18","{{longText}}",[403496,"Zero_Ash"]]}""";
        var result = LiveEventTextExtractor.Extract("danmaku", payload);
        Assert.NotNull(result);
        Assert.Equal(longText, result.Text);
    }

    [Fact]
    public void Extract_ReturnsEmptyMedal_WhenDanmakuInfo3IsEmptyArray()
    {
        // info[3]=[] 表示未佩戴粉丝牌
        var payload = """{"info":["18","弹幕内容",[403496,"Zero_Ash"],[]]}""";
        var result = LiveEventTextExtractor.Extract("danmaku", payload);
        Assert.NotNull(result);
        Assert.Equal("", result.SenderMedal);
    }

    [Fact]
    public void Extract_ReturnsEmptySenderFields_WhenDanmakuSenderInfoMissing()
    {
        // info[2] 缺失时发送者字段降级为空值
        var payload = """{"info":["18","弹幕内容"]}""";
        var result = LiveEventTextExtractor.Extract("danmaku", payload);
        Assert.NotNull(result);
        Assert.Equal("弹幕内容", result.Text);
        Assert.Equal("", result.SenderName);
        Assert.Equal(0, result.SenderUid);
        Assert.Equal("", result.SenderMedal);
    }

    [Fact]
    public void Extract_ReturnsDefaultUid_WhenDanmakuUidIsNotNumber()
    {
        // info[2][0] 非数字时 SenderUid 降级为 0
        var payload = """{"info":["18","弹幕内容",["not_number","Zero_Ash"]]}""";
        var result = LiveEventTextExtractor.Extract("danmaku", payload);
        Assert.NotNull(result);
        Assert.Equal(0, result.SenderUid);
    }

    [Fact]
    public void Extract_ReturnsEmptySenderFields_WhenSuperChatSenderInfoMissing()
    {
        // SC 中 user_info/medal_info 缺失时发送者字段降级为空值
        var payload = """{"data":{"message":"SC消息","uid":663620883}}""";
        var result = LiveEventTextExtractor.Extract("super_chat", payload);
        Assert.NotNull(result);
        Assert.Equal("SC消息", result.Text);
        Assert.Equal(663620883, result.SenderUid);
        Assert.Equal("", result.SenderName);
        Assert.Equal("", result.SenderMedal);
    }

    [Fact]
    public void Extract_ReturnsDefaultUid_WhenSuperChatUidIsNotNumber()
    {
        // data.uid 非数字时 SenderUid 降级为 0
        var payload = """{"data":{"message":"SC消息","uid":"not_number"}}""";
        var result = LiveEventTextExtractor.Extract("super_chat", payload);
        Assert.NotNull(result);
        Assert.Equal(0, result.SenderUid);
    }
}
