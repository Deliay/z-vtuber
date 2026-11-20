using Microsoft.Extensions.Logging.Abstractions;
using Z.Vtuber.Recording.Configuration;
using Z.Vtuber.Recording.Recording;
using Z.Vtuber.Recording.Video;

namespace Z.Vtuber.Tests;

/// <summary>录播视频切片参数纯函数与失败口径（不依赖真实 ffmpeg）。</summary>
public class VideoClipExtractorTests
{
    private static VideoClipExtractor CreateExtractor(IRecordingProcessFactory? factory = null) =>
        new(
            new RecordingOptions { StorageDir = "/data/recordings" },
            factory ?? new FakeRecordingProcessFactory(),
            NullLogger<VideoClipExtractor>.Instance);

    [Fact]
    public void BuildArguments_SeeksBeforeInputAndCopiesIntoFragmentedMp4()
    {
        var args = CreateExtractor().BuildArguments("/data/1/clip.flv", 1500, 4500);

        Assert.Equal(
            new[]
            {
                "-hide_banner", "-loglevel", "error",
                "-ss", "1.5", "-t", "3", "-i", "/data/1/clip.flv",
                "-map", "0:v:0", "-map", "0:a:0?",
                "-c", "copy",
                "-movflags", "frag_keyframe+empty_moov",
                "-avoid_negative_ts", "make_zero",
                "-f", "mp4", "pipe:1",
            },
            args);
    }

    [Fact]
    public void BuildArguments_FormatsMillisecondsAsSeconds()
    {
        var args = CreateExtractor().BuildArguments("/data/1/clip.flv", 0, 1234);

        Assert.Equal("0", args[Array.IndexOf(args.ToArray(), "-ss") + 1]);
        Assert.Equal("1.234", args[Array.IndexOf(args.ToArray(), "-t") + 1]);
    }

    [Fact]
    public async Task ExtractAsync_ZeroBytesAndNonZeroExit_ThrowsVideoClipFailed()
    {
        var factory = new FakeRecordingProcessFactory
        {
            ProcessFactory = _ =>
            {
                var process = new FakeRecordingProcess();
                process.Exit(1);
                return process;
            },
        };
        var extractor = CreateExtractor(factory);

        await Assert.ThrowsAsync<VideoClipFailedException>(() =>
            extractor.ExtractAsync("/data/1/clip.flv", 0, 1000, new MemoryStream(), CancellationToken.None));
    }

    [Fact]
    public async Task ExtractAsync_WritesBytesEvenWhenExitCodeNonZero_DoesNotThrow()
    {
        var factory = new FakeRecordingProcessFactory
        {
            ProcessFactory = _ =>
            {
                var process = new FakeRecordingProcess([1, 2, 3]);
                process.Exit(1);
                return process;
            },
        };
        var extractor = CreateExtractor(factory);
        using var output = new MemoryStream();

        await extractor.ExtractAsync("/data/1/clip.flv", 0, 1000, output, CancellationToken.None);

        Assert.Equal(new byte[] { 1, 2, 3 }, output.ToArray());
    }
}
