using System.Collections.Concurrent;

namespace Z.Vtuber.Recording.Audio;

/// <summary>
/// 实时音频 Hub：每房间一个广播域；无订阅者时不缓存音频分片（仅缓存首包 FLV 头供中途接入）；
/// 每个订阅者有界缓冲（超限丢弃最旧数据），慢消费者只影响自身，不阻塞广播与其他订阅者。
/// </summary>
public sealed class AudioStreamHub
{
    private readonly int _bufferBytes;
    private readonly ConcurrentDictionary<long, RoomState> _rooms = new();

    public AudioStreamHub(int bufferBytes)
    {
        if (bufferBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(bufferBytes));
        }

        _bufferBytes = bufferBytes;
    }

    public bool IsStreaming(long roomId) =>
        _rooms.TryGetValue(roomId, out var state) && Volatile.Read(ref state.Streaming);

    /// <summary>标记房间进入录制（音频可被订阅）。</summary>
    public void MarkStreaming(long roomId)
    {
        var state = _rooms.GetOrAdd(roomId, _ => new RoomState());
        Volatile.Write(ref state.Streaming, true);
    }

    /// <summary>标记房间停止录制，并结束所有订阅者。</summary>
    public void MarkStopped(long roomId)
    {
        if (!_rooms.TryGetValue(roomId, out var state))
        {
            return;
        }

        Volatile.Write(ref state.Streaming, false);
        List<AudioSubscription> subscribers;
        lock (state.Gate)
        {
            subscribers = [.. state.Subscribers];
            state.Subscribers.Clear();
        }

        foreach (var subscriber in subscribers)
        {
            subscriber.Complete();
        }
    }

    /// <summary>
    /// 订阅房间音频。若房间未在录制，返回的订阅已完成（读到即结束），避免 IsStreaming 检查与订阅之间的 TOCTOU。
    /// 录制中且已缓存 FLV 头时先重放头部，保证中途接入可解码。
    /// </summary>
    public AudioSubscription Subscribe(long roomId)
    {
        var state = _rooms.GetOrAdd(roomId, _ => new RoomState());
        AudioSubscription? subscription = null;
        subscription = new AudioSubscription(_bufferBytes, () =>
        {
            if (subscription is not null)
            {
                Unsubscribe(roomId, subscription);
            }
        });

        lock (state.Gate)
        {
            if (!Volatile.Read(ref state.Streaming))
            {
                subscription.Complete();
                return subscription;
            }

            state.Subscribers.Add(subscription);
            if (state.Header is { Length: > 0 })
            {
                subscription.Enqueue(state.Header);
            }
        }

        return subscription;
    }

    public void Unsubscribe(long roomId, AudioSubscription? subscription)
    {
        if (subscription is null || !_rooms.TryGetValue(roomId, out var state))
        {
            return;
        }

        lock (state.Gate)
        {
            state.Subscribers.Remove(subscription);
        }

        subscription.Complete();
    }

    /// <summary>广播一段音频；无订阅者时仅缓存首包（FLV 头），其余分片直接丢弃。</summary>
    public void Publish(long roomId, byte[] data)
    {
        if (data.Length == 0 || !_rooms.TryGetValue(roomId, out var state))
        {
            return;
        }

        if (!Volatile.Read(ref state.Streaming))
        {
            return;
        }

        List<AudioSubscription> subscribers;
        lock (state.Gate)
        {
            // 缓存首包（含 FLV 头），供中途接入的订阅者重放
            state.Header ??= data;

            if (state.Subscribers.Count == 0)
            {
                return;
            }

            subscribers = [.. state.Subscribers];
        }

        foreach (var subscriber in subscribers)
        {
            subscriber.Enqueue(data);
        }
    }

    private sealed class RoomState
    {
        public readonly object Gate = new();
        public readonly List<AudioSubscription> Subscribers = [];
        public bool Streaming;
        public byte[]? Header;
    }
}

/// <summary>单个订阅者：以字节数为上限的有界队列（DropOldest 等价语义）。</summary>
public sealed class AudioSubscription : IDisposable
{
    private readonly int _maxBytes;
    private readonly Action _onDispose;
    private readonly object _gate = new();
    private readonly Queue<byte[]> _queue = new();
    private readonly SemaphoreSlim _signal = new(0);
    private int _queuedBytes;
    private bool _completed;
    private bool _disposed;

    internal AudioSubscription(int maxBytes, Action onDispose)
    {
        _maxBytes = maxBytes;
        _onDispose = onDispose;
    }

    public int QueuedBytes
    {
        get
        {
            lock (_gate)
            {
                return _queuedBytes;
            }
        }
    }

    internal void Enqueue(byte[] data)
    {
        if (data.Length == 0)
        {
            return;
        }

        lock (_gate)
        {
            if (_completed)
            {
                return;
            }

            // 单条超过上限：直接丢弃
            if (data.Length > _maxBytes)
            {
                return;
            }

            // 丢弃最旧数据直到能容纳
            while (_queuedBytes + data.Length > _maxBytes && _queue.Count > 0)
            {
                var dropped = _queue.Dequeue();
                _queuedBytes -= dropped.Length;
            }

            _queue.Enqueue(data);
            _queuedBytes += data.Length;
        }

        _signal.Release();
    }

    /// <summary>等待可读数据（或订阅结束）。返回 false 表示订阅已结束且队列已空。</summary>
    public async ValueTask<bool> WaitToReadAsync(CancellationToken ct)
    {
        while (true)
        {
            lock (_gate)
            {
                if (_queue.Count > 0)
                {
                    return true;
                }

                if (_completed)
                {
                    return false;
                }
            }

            try
            {
                await _signal.WaitAsync(ct);
            }
            catch (OperationCanceledException)
            {
                return false;
            }
        }
    }

    public bool TryRead(out byte[] data)
    {
        lock (_gate)
        {
            if (_queue.Count == 0)
            {
                data = [];
                return false;
            }

            data = _queue.Dequeue();
            _queuedBytes -= data.Length;
            return true;
        }
    }

    internal void Complete()
    {
        lock (_gate)
        {
            if (_completed)
            {
                return;
            }

            _completed = true;
        }

        _signal.Release();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Complete();
        _onDispose();
        // 不 Dispose _signal：Complete/Enqueue 可能在并发路径 Release，
        // Dispose 会导致 ObjectDisposedException 使音频泵静默死亡或误报 error。交由 GC 回收。
    }
}
