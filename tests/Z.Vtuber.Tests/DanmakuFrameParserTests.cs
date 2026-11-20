using Z.Vtuber.Auth.Danmaku;

namespace Z.Vtuber.Tests;

public sealed class DanmakuFrameParserTests
{
    [Fact]
    public void ParsesMultipleFrames()
    {
        const string raw = "event: heartbeat\ndata: {}\n\nevent: danmaku\ndata: {\"a\":1}\n\n";
        var frames = DanmakuFrameParser.Parse(raw);
        Assert.Equal(2, frames.Count);
        Assert.Equal("heartbeat", frames[0].Event);
        Assert.Equal("{}", frames[0].Data);
        Assert.Equal("danmaku", frames[1].Event);
    }

    [Fact]
    public void JoinsMultilineDataWithNewline()
    {
        var frames = DanmakuFrameParser.Parse("data: line1\ndata: line2\n\n");
        Assert.Single(frames);
        Assert.Equal("line1\nline2", frames[0].Data);
    }

    [Fact]
    public void IgnoresCommentsAndIds()
    {
        var frames = DanmakuFrameParser.Parse(": keep-alive\nid: 3\nevent: danmaku\ndata: {}\n\n");
        Assert.Single(frames);
        Assert.Equal("danmaku", frames[0].Event);
    }

    [Fact]
    public void HandlesCrlf()
    {
        var frames = DanmakuFrameParser.Parse("event: heartbeat\r\ndata: {}\r\n\r\n");
        Assert.Single(frames);
        Assert.Equal("heartbeat", frames[0].Event);
    }

    [Fact]
    public void MissingEventDefaultsToMessage()
    {
        var frames = DanmakuFrameParser.Parse("data: hello\n\n");
        Assert.Equal("message", frames[0].Event);
        Assert.Equal("hello", frames[0].Data);
    }

    [Fact]
    public void EmptyInput_ReturnsNoFrames()
    {
        Assert.Empty(DanmakuFrameParser.Parse(""));
        Assert.Empty(DanmakuFrameParser.Parse("\n\n"));
    }

    [Fact]
    public void BlankDataLine_IsPreserved()
    {
        var frames = DanmakuFrameParser.Parse("event: heartbeat\ndata:\n\n");
        Assert.Single(frames);
        Assert.Equal(string.Empty, frames[0].Data);
    }
}
