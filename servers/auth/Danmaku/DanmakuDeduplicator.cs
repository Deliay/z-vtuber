using System.Collections.Concurrent;

namespace Z.Vtuber.Auth.Danmaku;

/// <summary>
/// 实时流 + 补投事件去重：有界集合（默认 10k / 10 分钟），键为 (roomId, occurredAt, uid, text)。
/// </summary>
public sealed class DanmakuDeduplicator
{
    private readonly int _capacity;
    private readonly TimeSpan _window;
    private readonly TimeProvider _clock;
    private readonly object _gate = new();
    private readonly Dictionary<string, DateTime> _seen = new(StringComparer.Ordinal);
    private readonly Queue<(string Key, DateTime At)> _order = new();

    public DanmakuDeduplicator(int capacity = 10_000, TimeSpan? window = null, TimeProvider? clock = null)
    {
        _capacity = capacity;
        _window = window ?? TimeSpan.FromMinutes(10);
        _clock = clock ?? TimeProvider.System;
    }

    public bool TryAdd(string roomId, DateTime occurredAt, long uid, string text) =>
        TryAdd($"{roomId}|{occurredAt.Ticks}|{uid}|{text}");

    public void Remove(string roomId, DateTime occurredAt, long uid, string text) =>
        Remove($"{roomId}|{occurredAt.Ticks}|{uid}|{text}");

    public void Remove(string key)
    {
        lock (_gate)
        {
            _seen.Remove(key);
        }
    }

    public bool TryAdd(string key)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        lock (_gate)
        {
            Trim(now);
            if (_seen.TryGetValue(key, out var at) && now - at <= _window)
            {
                return false;
            }

            _seen[key] = now;
            _order.Enqueue((key, now));
            while (_seen.Count > _capacity && _order.Count > 0)
            {
                var (oldKey, oldAt) = _order.Dequeue();
                if (_seen.TryGetValue(oldKey, out var current) && current == oldAt)
                {
                    _seen.Remove(oldKey);
                }
            }

            return true;
        }
    }

    private void Trim(DateTime now)
    {
        while (_order.Count > 0)
        {
            var (key, at) = _order.Peek();
            if (now - at <= _window && _seen.Count <= _capacity)
            {
                break;
            }

            _order.Dequeue();
            if (_seen.TryGetValue(key, out var current) && current == at)
            {
                _seen.Remove(key);
            }
        }
    }
}
