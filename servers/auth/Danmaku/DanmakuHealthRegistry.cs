using System.Collections.Concurrent;

namespace Z.Vtuber.Auth.Danmaku;

public static class RoomRoles
{
    public const string Leader = "leader";
    public const string Standby = "standby";
    public const string Disconnected = "disconnected";
}

public sealed record DanmakuRoomSnapshot(
    string RoomId,
    string Role,
    bool Connected,
    bool Available,
    DateTime? LastHeartbeatAt,
    DateTime? LastEventAt);

/// <summary>每房间事件源健康状态注册表（供 API、readiness 与页面使用）。</summary>
public sealed class DanmakuHealthRegistry(TimeProvider clock)
{
    public static readonly TimeSpan DefaultGrace = TimeSpan.FromSeconds(30);

    private sealed class State
    {
        public readonly object Gate = new();
        public string Role = RoomRoles.Disconnected;
        public bool Connected;
        public bool OtherInstanceLease;
        public DateTime? LastHeartbeatAt;
        public DateTime? LastEventAt;
        public DateTime? LastFrameAt;
        public DateTime? LastErrorAt;
        public string? LastError;

        public DanmakuRoomSnapshot Snapshot(string roomId, DateTime now, TimeSpan grace)
        {
            lock (Gate)
            {
                var freshFrame = LastFrameAt is { } frame && now - frame <= grace;
                // 已连接、宽限期内仍有帧、或 follower 观察到其他实例持租约，均视为系统级可用。
                var available = Connected || freshFrame || (Role == RoomRoles.Standby && OtherInstanceLease);
                return new DanmakuRoomSnapshot(
                    roomId,
                    Role,
                    Connected,
                    available,
                    LastHeartbeatAt,
                    LastEventAt);
            }
        }
    }

    private readonly ConcurrentDictionary<string, State> _states = new(StringComparer.Ordinal);

    public void MarkLeader(string roomId)
    {
        var state = Get(roomId);
        lock (state.Gate)
        {
            state.Role = RoomRoles.Leader;
            state.OtherInstanceLease = false;
        }
    }

    public void MarkStandby(string roomId, bool otherInstanceLease)
    {
        var state = Get(roomId);
        lock (state.Gate)
        {
            state.Role = RoomRoles.Standby;
            state.Connected = false;
            state.OtherInstanceLease = otherInstanceLease;
        }
    }

    public void MarkDisconnected(string roomId)
    {
        var state = Get(roomId);
        lock (state.Gate)
        {
            state.Role = RoomRoles.Disconnected;
            state.Connected = false;
            state.OtherInstanceLease = false;
            // 保留 LastFrameAt：断线抖动时仍可在宽限期内继续服务创建 token 的请求。
        }
    }

    public void MarkConnected(string roomId)
    {
        var state = Get(roomId);
        lock (state.Gate)
        {
            state.Connected = true;
            state.LastFrameAt = clock.GetUtcNow().UtcDateTime;
        }
    }

    public void MarkHeartbeat(string roomId)
    {
        var state = Get(roomId);
        var now = clock.GetUtcNow().UtcDateTime;
        lock (state.Gate)
        {
            state.LastHeartbeatAt = now;
            state.LastFrameAt = now;
        }
    }

    public void MarkEvent(string roomId)
    {
        var state = Get(roomId);
        var now = clock.GetUtcNow().UtcDateTime;
        lock (state.Gate)
        {
            state.LastEventAt = now;
            state.LastFrameAt = now;
        }
    }

    public void MarkError(string roomId, string error)
    {
        var state = Get(roomId);
        var now = clock.GetUtcNow().UtcDateTime;
        lock (state.Gate)
        {
            state.LastError = error;
            state.LastErrorAt = now;
        }
    }

    public IReadOnlyList<DanmakuRoomSnapshot> GetSnapshots(IEnumerable<string> roomIds, TimeSpan? grace = null)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var effectiveGrace = grace ?? DefaultGrace;
        return roomIds
            .Select(roomId => _states.TryGetValue(roomId, out var state)
                ? state.Snapshot(roomId, now, effectiveGrace)
                : new DanmakuRoomSnapshot(roomId, RoomRoles.Disconnected, false, false, null, null))
            .ToList();
    }

    public bool AnyAvailable(IEnumerable<string> roomIds, TimeSpan notReadyGrace) =>
        GetSnapshots(roomIds, notReadyGrace).Any(x => x.Available);

    public bool IsReady(IEnumerable<string> roomIds, TimeSpan notReadyGrace) =>
        AnyAvailable(roomIds, notReadyGrace);

    private State Get(string roomId) => _states.GetOrAdd(roomId, _ => new State());
}
