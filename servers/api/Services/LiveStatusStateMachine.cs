using Z.Vtuber.Persistence.Entities;

namespace Z.Vtuber.Services;

public static class LiveStatusStateMachine
{
    public static (bool ShouldPersist, string? LiveSessionId) Decide(
        LiveStatusRecord? latest, string title, bool liveStatus)
    {
        if (latest is null)
        {
            return liveStatus
                ? (true, Guid.NewGuid().ToString())
                : (true, null);
        }

        var titleChanged = !string.Equals(latest.Title, title, StringComparison.Ordinal);
        var statusChanged = latest.LiveStatus != liveStatus;
        if (!titleChanged && !statusChanged)
        {
            return (false, latest.LiveSessionId);
        }

        if (latest.LiveStatus == false && liveStatus)
        {
            return (true, Guid.NewGuid().ToString());
        }

        return (true, latest.LiveSessionId);
    }
}
