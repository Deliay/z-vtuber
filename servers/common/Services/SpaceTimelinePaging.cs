namespace Z.Vtuber.Common.Services;

public static class SpaceTimelinePaging
{
    public static bool ShouldContinuePaging(int pageCount, int existingCount, bool hasMore, string nextOffset)
    {
        if (pageCount <= 0)
        {
            return false;
        }

        if (existingCount >= pageCount)
        {
            return false;
        }

        if (!hasMore)
        {
            return false;
        }

        return !string.IsNullOrEmpty(nextOffset);
    }
}
