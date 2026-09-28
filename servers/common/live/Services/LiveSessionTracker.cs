using System.Collections.Concurrent;

namespace Z.Vtuber.Common.Services;

public interface ILiveSessionTracker
{
    string? GetCurrentSessionId(long roomId);
    void SetCurrentSessionId(long roomId, string? sessionId);
}

public sealed class LiveSessionTracker : ILiveSessionTracker
{
    private readonly ConcurrentDictionary<long, string?> _sessions = new();

    public string? GetCurrentSessionId(long roomId) =>
        _sessions.TryGetValue(roomId, out var sessionId) ? sessionId : null;

    public void SetCurrentSessionId(long roomId, string? sessionId) =>
        _sessions[roomId] = sessionId;
}
