using Z.Vtuber.Services;

namespace Z.Vtuber.Tests;

public class SpaceTimelinePagingTests
{
    [Fact]
    public void EmptyPage_Stops()
    {
        Assert.False(SpaceTimelinePaging.ShouldContinuePaging(0, 0, true, "next"));
    }

    [Fact]
    public void WholePageAlreadyExists_Stops()
    {
        Assert.False(SpaceTimelinePaging.ShouldContinuePaging(5, 5, true, "next"));
    }

    [Fact]
    public void NoMorePages_Stops()
    {
        Assert.False(SpaceTimelinePaging.ShouldContinuePaging(5, 2, false, "next"));
    }

    [Fact]
    public void Gap_WithMorePages_Continues()
    {
        Assert.True(SpaceTimelinePaging.ShouldContinuePaging(5, 2, true, "next"));
    }

    [Fact]
    public void Gap_WithEmptyNextOffset_Stops()
    {
        Assert.False(SpaceTimelinePaging.ShouldContinuePaging(5, 2, true, ""));
    }
}
