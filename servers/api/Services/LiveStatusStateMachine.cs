using Z.Vtuber.Persistence.Entities;

namespace Z.Vtuber.Services;

public enum SessionTransition
{
    None,
    Started,
    Ended,
}

public static class LiveStatusStateMachine
{
    public static (bool ShouldPersist, string? LiveSessionId, SessionTransition Transition) Decide(
        LiveStatusRecord? latest, string title, bool liveStatus)
    {
        if (latest is null)
        {
            return liveStatus
                ? (true, Guid.NewGuid().ToString(), SessionTransition.Started)
                : (true, null, SessionTransition.None);
        }

        var titleChanged = !string.Equals(latest.Title, title, StringComparison.Ordinal);
        var statusChanged = latest.LiveStatus != liveStatus;
        if (!titleChanged && !statusChanged)
        {
            return (false, latest.LiveSessionId, SessionTransition.None);
        }

        if (latest.LiveStatus == false && liveStatus)
        {
            return (true, Guid.NewGuid().ToString(), SessionTransition.Started);
        }

        var transition = latest.LiveStatus && !liveStatus ? SessionTransition.Ended : SessionTransition.None;
        return (true, latest.LiveSessionId, transition);
    }
}
