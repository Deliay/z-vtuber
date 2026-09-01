using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
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
    private static readonly TimeSpan ReSubscribeDelay = TimeSpan.FromSeconds(2);

    private readonly IConnectionMultiplexer? _redis;
    private readonly ILogger<LiveEventBus> _logger;
    private readonly ConcurrentDictionary<long, RoomState> _rooms = new();
    private readonly object _gate = new();

    public LiveEventBus(IConnectionMultiplexer? redis = null, ILogger<LiveEventBus>? logger = null)
    {
        _redis = redis;
        _logger = logger ?? NullLogger<LiveEventBus>.Instance;
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
            // 取消 + 拆除 Redis 订阅（完成 ChannelMessageQueue），使泵的 await foreach 退出，
            // 最终由泵的 finally 兜底 UNSUBSCRIBE，避免后台任务与订阅泄漏
            state.Cts?.Cancel();
            if (state.Queue is not null)
            {
                _ = UnsubscribeQueueAsync(state.Queue);
            }
        }
    }

    private async Task UnsubscribeQueueAsync(ChannelMessageQueue queue)
    {
        try
        {
            await queue.UnsubscribeAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to unsubscribe live event Redis channel queue.");
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
        try
        {
            while (!ct.IsCancellationRequested)
            {
                ChannelMessageQueue? queue = null;
                try
                {
                    queue = await subscriber.SubscribeAsync(channel);
                    state.Queue = queue;

                    // 退订与订阅竞态：若订阅完成前已请求取消，立即拆除订阅退出，避免悬挂订阅
                    if (ct.IsCancellationRequested)
                    {
                        await queue.UnsubscribeAsync();
                        break;
                    }

                    await foreach (var msg in queue)
                    {
                        ct.ThrowIfCancellationRequested();

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

                        Distribute(roomId, state, liveEvent);
                    }

                    // 队列被释放（最后一个订阅者退订）→ 正常退出泵
                    break;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    // 泵故障：记录日志并重建订阅（仍有订阅者且未取消时）
                    _logger.LogWarning(ex, "LiveEventBus pump for room {RoomId} faulted; re-subscribing.", roomId);
                    try
                    {
                        await subscriber.UnsubscribeAsync(channel);
                    }
                    catch
                    {
                        // 忽略退订失败，下一次重建仍会重试
                    }

                    try
                    {
                        await Task.Delay(ReSubscribeDelay, ct);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }
            }
        }
        finally
        {
            try
            {
                await subscriber.UnsubscribeAsync(channel);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to unsubscribe LiveEventBus channel {Channel}.", channel);
            }

            state.PumpCompleted.TrySetResult();
        }
    }

    private void Distribute(long roomId, RoomState state, LiveEvent evt)
    {
        // 快速重订阅竞态防护：若房间已重建（新 RoomState），旧泵不得向新订阅者投递，避免重复投递
        if (!_rooms.TryGetValue(roomId, out var current) || !ReferenceEquals(current, state))
        {
            return;
        }

        foreach (var (_, subChannel) in current.Subscribers)
        {
            subChannel.Writer.TryWrite(evt);
        }
    }

    private sealed class RoomState
    {
        public ConcurrentDictionary<Guid, Channel<LiveEvent>> Subscribers { get; } = new();
        public CancellationTokenSource? Cts { get; set; }
        public Task? PumpTask { get; set; }
        public ChannelMessageQueue? Queue { get; set; }
        public TaskCompletionSource PumpCompleted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
