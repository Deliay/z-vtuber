using Z.Vtuber.Common.Persistence.Entities;
using Z.Vtuber.Crawler.Services;

namespace Z.Vtuber.Tests;

public class LiveGuardTop3BackfillPlannerTests
{
    [Fact]
    public void SelectTop10_MedalLevelPrimarySort()
    {
        var entries = new List<LiveGuardEntry>
        {
            new() { Uid = 1, MedalLevel = 5, GuardLevel = 3, UserName = "low" },
            new() { Uid = 2, MedalLevel = 10, GuardLevel = 1, UserName = "high" },
            new() { Uid = 3, MedalLevel = 5, GuardLevel = 5, UserName = "mid" },
        };

        var result = LiveGuardTop3BackfillPlanner.SelectTop10(entries);

        Assert.Equal(3, result.Count);
        Assert.Equal(2L, result[0].Uid);
        Assert.Equal(3L, result[1].Uid);
        Assert.Equal(1L, result[2].Uid);
    }

    [Fact]
    public void SelectTop10_GuardLevelSecondarySort()
    {
        var entries = new List<LiveGuardEntry>
        {
            new() { Uid = 1, MedalLevel = 5, GuardLevel = 1, UserName = "low" },
            new() { Uid = 2, MedalLevel = 5, GuardLevel = 5, UserName = "high" },
            new() { Uid = 3, MedalLevel = 5, GuardLevel = 3, UserName = "mid" },
        };

        var result = LiveGuardTop3BackfillPlanner.SelectTop10(entries);

        Assert.Equal(3, result.Count);
        Assert.Equal(2L, result[0].Uid);
        Assert.Equal(3L, result[1].Uid);
        Assert.Equal(1L, result[2].Uid);
    }

    [Fact]
    public void SelectTop10_UidTertiarySort()
    {
        var entries = new List<LiveGuardEntry>
        {
            new() { Uid = 3, MedalLevel = 5, GuardLevel = 5, UserName = "c" },
            new() { Uid = 1, MedalLevel = 5, GuardLevel = 5, UserName = "a" },
            new() { Uid = 2, MedalLevel = 5, GuardLevel = 5, UserName = "b" },
        };

        var result = LiveGuardTop3BackfillPlanner.SelectTop10(entries);

        Assert.Equal(3, result.Count);
        Assert.Equal(1L, result[0].Uid);
        Assert.Equal(2L, result[1].Uid);
        Assert.Equal(3L, result[2].Uid);
    }

    [Fact]
    public void SelectTop10_LessThan10_ReturnsAll()
    {
        var entries = new List<LiveGuardEntry>
        {
            new() { Uid = 1, MedalLevel = 5, GuardLevel = 3, UserName = "user1" },
            new() { Uid = 2, MedalLevel = 10, GuardLevel = 1, UserName = "user2" },
        };

        var result = LiveGuardTop3BackfillPlanner.SelectTop10(entries);

        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void SelectTop10_Exactly10_Returns10()
    {
        var entries = Enumerable.Range(1, 10)
            .Select(i => new LiveGuardEntry { Uid = i, MedalLevel = i, GuardLevel = i, UserName = $"user{i}" })
            .ToList();

        var result = LiveGuardTop3BackfillPlanner.SelectTop10(entries);

        Assert.Equal(10, result.Count);
    }

    [Fact]
    public void SelectTop10_MoreThan10_Truncates()
    {
        var entries = Enumerable.Range(1, 20)
            .Select(i => new LiveGuardEntry { Uid = i, MedalLevel = i, GuardLevel = i, UserName = $"user{i}" })
            .ToList();

        var result = LiveGuardTop3BackfillPlanner.SelectTop10(entries);

        Assert.Equal(10, result.Count);
        Assert.Equal(20L, result[0].Uid);
        Assert.Equal(11L, result[9].Uid);
    }

    [Fact]
    public void ComputeMissing_AllMissing()
    {
        var snapshotGuards = new List<LiveGuardEntry>();
        var top10 = new List<LiveGuardEntry>
        {
            new() { Uid = 1, MedalLevel = 5, GuardLevel = 3, UserName = "user1" },
            new() { Uid = 2, MedalLevel = 10, GuardLevel = 1, UserName = "user2" },
        };

        var result = LiveGuardTop3BackfillPlanner.ComputeMissing(snapshotGuards, top10);

        Assert.Equal(2, result.Count);
        Assert.Equal(1L, result[0].Uid);
        Assert.Equal(2L, result[1].Uid);
    }

    [Fact]
    public void ComputeMissing_PartialMissing()
    {
        var snapshotGuards = new List<LiveGuardEntry>
        {
            new() { Uid = 1, MedalLevel = 5, GuardLevel = 3, UserName = "existing" },
        };
        var top10 = new List<LiveGuardEntry>
        {
            new() { Uid = 2, MedalLevel = 10, GuardLevel = 1, UserName = "user2" },
            new() { Uid = 1, MedalLevel = 5, GuardLevel = 3, UserName = "user1" },
            new() { Uid = 3, MedalLevel = 8, GuardLevel = 2, UserName = "user3" },
        };

        var result = LiveGuardTop3BackfillPlanner.ComputeMissing(snapshotGuards, top10);

        Assert.Equal(2, result.Count);
        Assert.Equal(2L, result[0].Uid);
        Assert.Equal(3L, result[1].Uid);
    }

    [Fact]
    public void ComputeMissing_AllExisting_ReturnsEmpty()
    {
        var snapshotGuards = new List<LiveGuardEntry>
        {
            new() { Uid = 1, MedalLevel = 5, GuardLevel = 3, UserName = "user1" },
            new() { Uid = 2, MedalLevel = 10, GuardLevel = 1, UserName = "user2" },
        };
        var top10 = new List<LiveGuardEntry>
        {
            new() { Uid = 1, MedalLevel = 5, GuardLevel = 3, UserName = "user1" },
            new() { Uid = 2, MedalLevel = 10, GuardLevel = 1, UserName = "user2" },
        };

        var result = LiveGuardTop3BackfillPlanner.ComputeMissing(snapshotGuards, top10);

        Assert.Empty(result);
    }
}