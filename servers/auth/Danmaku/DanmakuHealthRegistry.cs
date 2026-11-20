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
    private sealed class State
    {
        public string Role = RoomRoles.Disconnected;
        public bool Connected;
        public bool OtherInstanceLease;
        public DateTime? LastHeartbeatAt;
        public DateTime? LastEventAt;
        public DateTime? LastFrameAt;
        public DateTime? LastErrorAt;
        public string? LastError;
    }

    private readonly ConcurrentDictionary<string, State> _states = new(StringComparer.Ordinal);

    public void MarkLeader(string roomId)
    {
        var state = Get(roomId);
        state.Role = RoomRoles.Leader;
        state.OtherInstanceLease = false;
    }

    public void MarkStandby(string roomId, bool otherInstanceLease)
    {
        var state = Get(roomId);
        state.Role = RoomRoles.Standby;
        state.Connected = false;
        state.OtherInstanceLease = otherInstanceLease;
    }

    public void MarkDisconnected(string roomId)
    {
        var state = Get(roomId);
        state.Role = RoomRoles.Disconnected;
        state.Connected = false;
        state.OtherInstanceLease = false;
    }

    public void MarkConnected(string roomId)
    {
        var state = Get(roomId);
        state.Connected = true;
        state.LastFrameAt = clock.GetUtcNow().UtcDateTime;
    }

    public void MarkHeartbeat(string roomId)
    {
        var state = Get(roomId);
        var now = clock.GetUtcNow().UtcDateTime;
        state.LastHeartbeatAt = now;
        state.LastFrameAt = now;
    }

    public void MarkEvent(string roomId)
    {
        var state = Get(roomId);
        var now = clock.GetUtcNow().UtcDateTime;
        state.LastEventAt = now;
        state.LastFrameAt = now;
    }

    public void MarkError(string roomId, string error)
    {
        var state = Get(roomId);
        state.LastError = error;
        state.LastErrorAt = clock.GetUtcNow().UtcDateTime;
    }

    public IReadOnlyList<DanmakuRoomSnapshot> GetSnapshots(IEnumerable<string> roomIds)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        return roomIds.Select(roomId =>
        {
            var state = _states.TryGetValue(roomId, out var s) ? s : new State();
            var available = state.Role switch
            {
                RoomRoles.Leader => state.Connected,
                RoomRoles.Standby => state.OtherInstanceLease,
                _ => false
            };
            return new DanmakuRoomSnapshot(
                roomId,
                state.Role,
                state.Connected,
                available,
                state.LastHeartbeatAt,
                state.LastEventAt);
        }).ToList();
    }

    public bool AnyAvailable(IEnumerable<string> roomIds, TimeSpan notReadyGrace)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        foreach (var snapshot in GetSnapshots(roomIds))
        {
            if (!snapshot.Available)
            {
                continue;
            }

            if (snapshot.Connected &&
                snapshot.LastHeartbeatAt is { } heartbeat &&
                now - heartbeat <= notReadyGrace)
            {
                return true;
            }

            if (snapshot.Role == RoomRoles.Standby)
            {
                return true;
            }
        }

        return false;
    }

    public bool IsReady(IEnumerable<string> roomIds, TimeSpan notReadyGrace) =>
        AnyAvailable(roomIds, notReadyGrace);

    private State Get(string roomId) => _states.GetOrAdd(roomId, _ => new State());
}
