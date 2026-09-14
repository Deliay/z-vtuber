using Z.Vtuber.Recording.Live;

namespace Z.Vtuber.Tests;

public class StreamResolverTests
{
    [Fact]
    public void Pick_PrefersHttpStreamFlvAvc()
    {
        var candidates = new[]
        {
            new LiveStreamCandidate("hls", "http_hls", "ts", "avc"),
            new LiveStreamCandidate("flv", "http_stream", "flv", "avc"),
        };

        var picked = StreamResolver.Pick(candidates);

        Assert.Equal("flv", picked!.Url);
    }

    [Fact]
    public void Pick_ExcludesHevc()
    {
        var candidates = new[]
        {
            new LiveStreamCandidate("hevc", "http_stream", "flv", "hevc"),
            new LiveStreamCandidate("hls", "http_hls", "ts", "avc"),
        };

        var picked = StreamResolver.Pick(candidates);

        Assert.Equal("hls", picked!.Url);
    }

    [Fact]
    public void Pick_FallsBackToHls_WhenNoHttpStream()
    {
        var candidates = new[]
        {
            new LiveStreamCandidate("hls", "http_hls", "ts", "avc"),
        };

        var picked = StreamResolver.Pick(candidates);

        Assert.Equal("hls", picked!.Url);
    }

    [Fact]
    public void Pick_ReturnsNull_WhenEmpty()
    {
        Assert.Null(StreamResolver.Pick([]));
    }

    [Fact]
    public void Pick_ReturnsNull_WhenOnlyHevc()
    {
        var candidates = new[] { new LiveStreamCandidate("hevc", "http_stream", "flv", "hevc") };

        Assert.Null(StreamResolver.Pick(candidates));
    }
}
