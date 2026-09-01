using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using StackExchange.Redis;
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
    private readonly IConnectionMultiplexer? _redis;
    private readonly ConcurrentDictionary<long, RoomState> _rooms = new();
    private readonly object _gate = new();

    public LiveEventBus(IConnectionMultiplexer? redis = null)
    {
        _redis = redis;
    }

    public LiveEventSubscription Subscribe(long roomId)
    {
        var channel = Channel.CreateUnbounded<LiveEvent>(new UnboundedChannelOptions { SingleReader = true });
        var id = Guid.NewGuid();

        RoomState state;
        bool startPump;
        lock (_gate)
        {
            state = _rooms.GetOrAdd(roomId, static _ => new RoomState());
            state.Subscribers[id] = channel;
            startPump = _redis is not null && state.Subscribers.Count == 1;
        }

        if (startPump)
        {
            state.Cts = new CancellationTokenSource();
            state.PumpTask = Task.Run(() => RunPumpAsync(roomId, state, state.Cts.Token));
        }

        return new LiveEventSubscription(id, channel.Reader);
    }

    public void Unsubscribe(long roomId, Guid id)
    {
        RoomState? state = null;
        bool stopPump;
        lock (_gate)
        {
            if (_rooms.TryGetValue(roomId, out state) && state.Subscribers.TryRemove(id, out var channel))
            {
                channel.Writer.TryComplete();
            }

            stopPump = state is { Subscribers.Count: 0 } && _rooms.TryRemove(roomId, out _);
        }

        if (stopPump && state is not null)
        {
            state.Cts?.Cancel();
            state.Cts?.Dispose();
        }
    }

    public bool TryPublish(long roomId, LiveEvent @event)
    {
        if (!_rooms.TryGetValue(roomId, out var state))
        {
            return false;
        }

        var published = false;
        foreach (var (_, channel) in state.Subscribers)
        {
            published |= channel.Writer.TryWrite(@event);
        }

        return published;
    }

    private async Task RunPumpAsync(long roomId, RoomState state, CancellationToken ct)
    {
        var subscriber = _redis!.GetSubscriber();
        var channel = new RedisChannel(LiveEventChannels.ChannelName(roomId), RedisChannel.PatternMode.Literal);
        var queue = await subscriber.SubscribeAsync(channel);
        try
        {
            await foreach (var msg in queue)
            {
                if (ct.IsCancellationRequested)
                {
                    break;
                }

                if (msg.Message.IsNull)
                {
                    continue;
                }

                LiveEvent? liveEvent;
                try
                {
                    liveEvent = JsonSerializer.Deserialize<LiveEvent>((string)msg.Message!);
                }
                catch (JsonException)
                {
                    continue;
                }

                if (liveEvent is null)
                {
                    continue;
                }

                var evt = liveEvent;
                if (_rooms.TryGetValue(roomId, out var current))
                {
                    foreach (var (_, subChannel) in current.Subscribers)
                    {
                        subChannel.Writer.TryWrite(evt);
                    }
                }
            }
        }
        finally
        {
            await subscriber.UnsubscribeAsync(channel);
        }
    }

    private sealed class RoomState
    {
        public ConcurrentDictionary<Guid, Channel<LiveEvent>> Subscribers { get; } = new();
        public CancellationTokenSource? Cts { get; set; }
        public Task? PumpTask { get; set; }
    }
}