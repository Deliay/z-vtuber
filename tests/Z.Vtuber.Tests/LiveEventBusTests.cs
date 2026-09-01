using Z.Vtuber.Common.Live;
using Z.Vtuber.Live;

namespace Z.Vtuber.Tests;

public class LiveEventBusTests
{
    private static LiveEvent Event(long roomId) => new(roomId, "danmaku", null, DateTime.UtcNow, "{\"cmd\":\"DANMU_MSG\"}");

    [Fact]
    public void Publish_WithoutSubscriber_ReturnsFalse()
    {
        var bus = new LiveEventBus();

        Assert.False(bus.TryPublish(1, Event(1)));
    }

    [Fact]
    public void Publish_WithSubscriber_ReturnsTrue_AndDelivers()
    {
        var bus = new LiveEventBus();
        var subscription = bus.Subscribe(1);

        Assert.True(bus.TryPublish(1, Event(1)));

        Assert.True(subscription.Reader.TryRead(out var evt));
        Assert.Equal(1L, evt.RoomId);
    }

    [Fact]
    public void LastUnsubscribe_RemovesChannel()
    {
        var bus = new LiveEventBus();
        var subscription = bus.Subscribe(1);

        bus.Unsubscribe(1, subscription.Id);

        Assert.False(bus.TryPublish(1, Event(1)));
    }

    [Fact]
    public void MultipleSubscribers_OneUnsubscribe_OthersStillReceive()
    {
        var bus = new LiveEventBus();
        var s1 = bus.Subscribe(1);
        var s2 = bus.Subscribe(1);

        bus.Unsubscribe(1, s1.Id);

        Assert.True(bus.TryPublish(1, Event(1)));
        Assert.True(s2.Reader.TryRead(out _));
    }

    [Fact]
    public void MultipleSubscribers_BroadcastToAll()
    {
        var bus = new LiveEventBus();
        var s1 = bus.Subscribe(1);
        var s2 = bus.Subscribe(1);

        bus.TryPublish(1, Event(1));

        Assert.True(s1.Reader.TryRead(out _));
        Assert.True(s2.Reader.TryRead(out _));
    }

    [Fact]
    public void LastUnsubscribe_CompletesChannel()
    {
        var bus = new LiveEventBus();
        var subscription = bus.Subscribe(1);
        bus.Unsubscribe(1, subscription.Id);

        Assert.True(subscription.Reader.Completion.IsCompleted);
    }

    [Fact]
    public void SubscriberCount_DoesNotLeak()
    {
        var bus = new LiveEventBus();
        var ids = new List<Guid>();
        for (var i = 0; i < 10; i++)
        {
            ids.Add(bus.Subscribe(1).Id);
        }

        foreach (var id in ids)
        {
            bus.Unsubscribe(1, id);
        }

        Assert.False(bus.TryPublish(1, Event(1)));
    }
}
