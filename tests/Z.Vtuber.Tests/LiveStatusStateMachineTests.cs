using Z.Vtuber.Common.Persistence.Entities;
using Z.Vtuber.Common.Services;

namespace Z.Vtuber.Tests;

public class LiveStatusStateMachineTests
{
    [Fact]
    public void FirstCrawl_Live_GeneratesSessionId()
    {
        var (shouldPersist, sessionId, transition) = LiveStatusStateMachine.Decide(null, "title", true);

        Assert.True(shouldPersist);
        Assert.False(string.IsNullOrWhiteSpace(sessionId));
        Assert.Equal(SessionTransition.Started, transition);
    }

    [Fact]
    public void FirstCrawl_NotLive_NoSessionId()
    {
        var (shouldPersist, sessionId, transition) = LiveStatusStateMachine.Decide(null, "title", false);

        Assert.True(shouldPersist);
        Assert.Null(sessionId);
        Assert.Equal(SessionTransition.None, transition);
    }

    [Fact]
    public void SameTitleAndStatus_Skips()
    {
        var latest = new LiveStatusRecord { Title = "title", LiveStatus = true, LiveSessionId = "s1" };

        var (shouldPersist, sessionId, transition) = LiveStatusStateMachine.Decide(latest, "title", true);

        Assert.False(shouldPersist);
        Assert.Equal("s1", sessionId);
        Assert.Equal(SessionTransition.None, transition);
    }

    [Fact]
    public void TitleChange_WhileLive_KeepsSessionId()
    {
        var latest = new LiveStatusRecord { Title = "old", LiveStatus = true, LiveSessionId = "s1" };

        var (shouldPersist, sessionId, transition) = LiveStatusStateMachine.Decide(latest, "new", true);

        Assert.True(shouldPersist);
        Assert.Equal("s1", sessionId);
        Assert.Equal(SessionTransition.None, transition);
    }

    [Fact]
    public void LiveToOffline_KeepsSessionId()
    {
        var latest = new LiveStatusRecord { Title = "title", LiveStatus = true, LiveSessionId = "s1" };

        var (shouldPersist, sessionId, transition) = LiveStatusStateMachine.Decide(latest, "title", false);

        Assert.True(shouldPersist);
        Assert.Equal("s1", sessionId);
        Assert.Equal(SessionTransition.Ended, transition);
    }

    [Fact]
    public void OfflineToLive_GeneratesNewSessionId()
    {
        var latest = new LiveStatusRecord { Title = "title", LiveStatus = false, LiveSessionId = "s1" };

        var (shouldPersist, sessionId, transition) = LiveStatusStateMachine.Decide(latest, "title", true);

        Assert.True(shouldPersist);
        Assert.False(string.IsNullOrWhiteSpace(sessionId));
        Assert.NotEqual("s1", sessionId);
        Assert.Equal(SessionTransition.Started, transition);
    }

    [Fact]
    public void TitleChange_WhileOffline_NoNewSessionId()
    {
        var latest = new LiveStatusRecord { Title = "old", LiveStatus = false, LiveSessionId = null };

        var (shouldPersist, sessionId, transition) = LiveStatusStateMachine.Decide(latest, "new", false);

        Assert.True(shouldPersist);
        Assert.Null(sessionId);
        Assert.Equal(SessionTransition.None, transition);
    }
}
