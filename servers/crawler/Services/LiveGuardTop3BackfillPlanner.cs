using Z.Vtuber.Common.Persistence.Entities;

namespace Z.Vtuber.Crawler.Services;

public static class LiveGuardTop3BackfillPlanner
{
    public static List<LiveGuardEntry> SelectTop10(IReadOnlyList<LiveGuardEntry> entries)
    {
        return entries
            .OrderByDescending(e => e.MedalLevel)
            .ThenByDescending(e => e.GuardLevel)
            .ThenBy(e => e.Uid)
            .Take(10)
            .ToList();
    }

    public static List<LiveGuardEntry> ComputeMissing(IReadOnlyList<LiveGuardEntry> snapshotGuards, List<LiveGuardEntry> top10)
    {
        var existingUids = new HashSet<long>(snapshotGuards.Select(e => e.Uid));
        return top10.Where(e => !existingUids.Contains(e.Uid)).ToList();
    }
}