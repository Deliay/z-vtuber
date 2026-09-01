using Z.Vtuber.Crawler.Live;
using Z.Vtuber.Common.Live;

namespace Z.Vtuber.Tests;

public class DanmakuEventNormalizerTests
{
    [Fact]
    public void KnownCommand_NormalizesToFriendlyType()
    {
        var raw = """{"cmd":"GUARD_BUY","data":{"uid":123}}""";
        var occurredAt = DateTime.UtcNow;

        var result = DanmakuEventNormalizer.Normalize(42, "session-1", raw, occurredAt);

        Assert.NotNull(result);
        Assert.Equal(42L, result.RoomId);
        Assert.Equal("guard_buy", result.Type);
        Assert.Equal("session-1", result.LiveSessionId);
        Assert.Equal(occurredAt, result.OccurredAt);
        Assert.Equal(raw, result.Payload);
    }

    [Fact]
    public void SuperChat_NormalizesToSuperChatType()
    {
        var raw = """{"cmd":"SUPER_CHAT_MESSAGE","data":{}}""";

        var result = DanmakuEventNormalizer.Normalize(1, null, raw, DateTime.UtcNow);

        Assert.NotNull(result);
        Assert.Equal("super_chat", result.Type);
    }

    [Fact]
    public void UnknownCommand_ReturnsNull()
    {
        var raw = """{"cmd":"SOME_UNKNOWN_CMD","data":{}}""";

        var result = DanmakuEventNormalizer.Normalize(1, null, raw, DateTime.UtcNow);

        Assert.Null(result);
    }

    [Fact]
    public void MissingCommand_ReturnsNull()
    {
        var raw = """{"data":{}}""";

        var result = DanmakuEventNormalizer.Normalize(1, null, raw, DateTime.UtcNow);

        Assert.Null(result);
    }

    [Fact]
    public void MalformedJson_ReturnsNull()
    {
        var result = DanmakuEventNormalizer.Normalize(1, null, "not json", DateTime.UtcNow);

        Assert.Null(result);
    }
}
