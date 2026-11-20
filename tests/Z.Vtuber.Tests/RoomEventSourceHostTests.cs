using Z.Vtuber.Auth.Danmaku;

namespace Z.Vtuber.Tests;

public sealed class RoomEventSourceHostTests
{
    [Fact]
    public void ResolveRooms_UsesConfiguredOrderAndDeduplicates()
    {
        var options = AuthTestOptions.Danmaku();
        options.RoomIds = ["30066", "12345", "30066"];

        var rooms = RoomEventSourceHost.ResolveRooms(options);

        Assert.Equal(new[] { "30066", "12345" }, rooms);
    }

    [Fact]
    public void ResolveRooms_DefaultIsSinglePrimaryRoom()
    {
        var rooms = RoomEventSourceHost.ResolveRooms(AuthTestOptions.Danmaku());
        Assert.Equal(new[] { "30066" }, rooms);
    }
}
