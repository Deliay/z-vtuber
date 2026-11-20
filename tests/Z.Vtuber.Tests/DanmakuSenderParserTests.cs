using System.Text.Json;
using Z.Vtuber.Auth.Danmaku;

namespace Z.Vtuber.Tests;

public sealed class DanmakuSenderParserTests
{
    [Fact]
    public void ParsesTextUidNameMedalAndAvatar()
    {
        var sender = DanmakuSenderParser.Parse(AuthTestData.Payload("A1b2C3d4E5"));
        Assert.NotNull(sender);
        Assert.Equal("A1b2C3d4E5", sender!.Text);
        Assert.Equal(403496, sender.Uid);
        Assert.Equal("Zero_Ash", sender.Name);
        Assert.Equal("https://i2.hdslb.com/bfs/face/test.jpg", sender.AvatarUrl);
    }

    [Fact]
    public void FallsBackToOriginInfoAvatar()
    {
        var payload = JsonDocument.Parse(AuthTestData.DanmakuPayload(
            "A1b2C3d4E5", face: null, originFace: "https://i0.hdslb.com/origin.jpg")).RootElement.Clone();
        var sender = DanmakuSenderParser.Parse(payload);
        Assert.Equal("https://i0.hdslb.com/origin.jpg", sender!.AvatarUrl);
    }

    [Fact]
    public void MissingAvatar_ReturnsNullAvatar()
    {
        var payload = JsonDocument.Parse(AuthTestData.DanmakuPayload("A1b2C3d4E5", face: null)).RootElement.Clone();
        Assert.Null(DanmakuSenderParser.Parse(payload)!.AvatarUrl);
    }

    [Fact]
    public void TrimsText()
    {
        var payload = JsonDocument.Parse(AuthTestData.DanmakuPayload("  A1b2C3d4E5  ")).RootElement.Clone();
        Assert.Equal("A1b2C3d4E5", DanmakuSenderParser.Parse(payload)!.Text);
    }

    [Fact]
    public void UidZero_IsRejected()
    {
        var payload = JsonDocument.Parse(AuthTestData.DanmakuPayload("A1b2C3d4E5", uid: 0)).RootElement.Clone();
        Assert.Null(DanmakuSenderParser.Parse(payload));
    }

    [Fact]
    public void EmptyText_IsRejected()
    {
        var payload = JsonDocument.Parse(AuthTestData.DanmakuPayload("   ")).RootElement.Clone();
        Assert.Null(DanmakuSenderParser.Parse(payload));
    }

    [Fact]
    public void MalformedPayload_ReturnsNullWithoutThrowing()
    {
        using var document = JsonDocument.Parse("""{"info":[null]}""");
        Assert.Null(DanmakuSenderParser.Parse(document.RootElement));
        Assert.Null(DanmakuSenderParser.Parse(JsonDocument.Parse("\"text\"").RootElement));
    }
}
