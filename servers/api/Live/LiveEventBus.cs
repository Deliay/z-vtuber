using System.Collections.Concurrent;
using System.Threading.Channels;

using Z.Vtuber.Common.Live;
namespace Z.Vtuber.Live;

public sealed record LiveEventSubscription(Guid Id, ChannelReader<LiveEvent> Reader);

public interface ILiveEventBus
{
    LiveEventSubscription Subscribe(long roomId);
    void Unsubscribe(long roomId, Guid id);
    bool TryPublish(long roomId, LiveEvent @event);
}

public sealed class LiveEventBus : ILiveEventBus
{
    private readonly ConcurrentDictionary<long, ConcurrentDictionary<Guid, Channel<LiveEvent>>> _subscribers = new();

    public LiveEventSubscription Subscribe(long roomId)
    {
        var channel = Channel.CreateUnbounded<LiveEvent>(new UnboundedChannelOptions { SingleReader = true });
        var id = Guid.NewGuid();
        var room = _subscribers.GetOrAdd(roomId, static _ => new ConcurrentDictionary<Guid, Channel<LiveEvent>>());
        room[id] = channel;
        return new LiveEventSubscription(id, channel.Reader);
    }

    public void Unsubscribe(long roomId, Guid id)
    {
        if (!_subscribers.TryGetValue(roomId, out var room))
        {
            return;
        }

        if (room.TryRemove(id, out var channel))
        {
            channel.Writer.TryComplete();
        }

        if (room.IsEmpty)
        {
            _subscribers.TryRemove(roomId, out _);
        }
    }

    public bool TryPublish(long roomId, LiveEvent @event)
    {
        if (!_subscribers.TryGetValue(roomId, out var room))
        {
            return false;
        }

        var published = false;
        foreach (var (_, channel) in room)
        {
            published |= channel.Writer.TryWrite(@event);
        }

        return published;
    }
}
