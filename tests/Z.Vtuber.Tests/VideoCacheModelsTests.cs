using Z.Vtuber.Crawler.Video;

namespace Z.Vtuber.Tests;

public sealed class VideoCacheModelsTests
{
    [Theory]
    [InlineData("BV1xx411c7mD", 1, VideoStreamKind.Video, "videos/BV1xx411c7mD/p01/video")]
    [InlineData("BV1xx411c7mD", 12, VideoStreamKind.Audio, "videos/BV1xx411c7mD/p12/audio")]
    public void VideoCacheKey_For_IncludesBvidPageAndKind(string bvid, int page, VideoStreamKind kind, string expected)
        => Assert.Equal(expected, VideoCacheKey.For(bvid, page, kind).ObjectKey);

    [Fact]
    public void IfRangeMatches_EmptyValue_AppliesRange()
        => Assert.True(VideoCacheRange.IfRangeMatches(null, "\"etag\"", DateTimeOffset.UtcNow));

    [Fact]
    public void IfRangeMatches_ETag_ComparesIgnoringQuotesAndWeakPrefix()
    {
        var lastModified = DateTimeOffset.UtcNow;

        Assert.True(VideoCacheRange.IfRangeMatches("\"abc\"", "\"abc\"", lastModified));
        Assert.True(VideoCacheRange.IfRangeMatches("W/\"abc\"", "abc", lastModified));
        Assert.False(VideoCacheRange.IfRangeMatches("\"abc\"", "\"def\"", lastModified));
    }

    [Fact]
    public void IfRangeMatches_Date_MatchesWhenNotModifiedSince()
    {
        var lastModified = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        Assert.True(VideoCacheRange.IfRangeMatches("Thu, 01 Jan 2026 00:00:00 GMT", "\"e\"", lastModified));
        Assert.False(VideoCacheRange.IfRangeMatches("Tue, 01 Jan 2019 00:00:00 GMT", "\"e\"", lastModified));
    }

    [Fact]
    public void IfRangeMatches_Unparsable_ReturnsFalse()
        => Assert.False(VideoCacheRange.IfRangeMatches("not-a-validator", null, null));

    [Theory]
    [InlineData("bytes=0-1023", true, 0)]
    [InlineData("bytes=100-", true, 100)]
    [InlineData("bytes=0-1,3-4", false, 0)]
    [InlineData("bytes=-500", false, 0)]
    [InlineData("items=0-1", false, 0)]
    [InlineData(null, false, 0)]
    public void TryParseStart_ParsesOnlySingleOpenEndedRange(string? range, bool expected, long expectedStart)
    {
        var parsed = VideoCacheRange.TryParseStart(range, out var start);

        Assert.Equal(expected, parsed);
        if (expected)
        {
            Assert.Equal(expectedStart, start);
        }
    }

    [Fact]
    public void NotSatisfiable_And_ContentRange_FormatCorrectly()
    {
        Assert.Equal("bytes */20480", VideoCacheRange.NotSatisfiable(20480));
        Assert.Equal("bytes 0-1023/20480", VideoCacheRange.ContentRange(0, 1024, 20480));
    }
}
