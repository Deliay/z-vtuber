using Microsoft.Extensions.Logging.Abstractions;
using Z.Vtuber.Recording.Audio;
using Z.Vtuber.Recording.Configuration;
using Z.Vtuber.Recording.Recording;

namespace Z.Vtuber.Tests;

/// <summary>多片段合并 ffmpeg 参数构造（纯函数口径）与 mp3 编码降级。</summary>
public class AudioClipMergerTests
{
    private static AudioClipMerger Create(Mp3EncoderSupport? mp3Support = null) =>
        new(
            new RecordingOptions(),
            new FfmpegRecordingProcessFactory(NullLogger<FfmpegRecordingProcessFactory>.Instance),
            mp3Support ?? new Mp3EncoderSupport(),
            NullLogger<AudioClipMerger>.Instance);

    [Fact]
    public void BuildArguments_SeeksEachInputBeforeIt_AndTargetsMp3()
    {
        var items = new[]
        {
            new AudioMergeClip("/data/1/a.flv", 1000, 2500),
            new AudioMergeClip("/data/1/b.flv", 0, 1000),
        };

        var args = Create().BuildArguments(items);

        Assert.Equal(
            new[]
            {
                "-hide_banner", "-loglevel", "error",
                "-ss", "1", "-t", "1.5", "-i", "/data/1/a.flv",
                "-ss", "0", "-t", "1", "-i", "/data/1/b.flv",
                "-filter_complex", "[0:a][1:a]concat=n=2:v=0:a=1[out]",
                "-map", "[out]",
                "-vn", "-ac", "1", "-ar", "44100",
                "-b:a", "96k", "-f", "mp3", "pipe:1",
            },
            args);
    }

    [Fact]
    public void BuildArguments_SingleItem_StillConcats()
    {
        var args = Create().BuildArguments([new AudioMergeClip("/data/1/a.flv", 0, 1000)]);

        var filterIndex = args.ToList().IndexOf("-filter_complex");
        Assert.Equal("[0:a]concat=n=1:v=0:a=1[out]", args[filterIndex + 1]);
    }

    [Fact]
    public void BuildArguments_WithoutMp3Encoder_DegradesToWav()
    {
        var merger = Create(new Mp3EncoderSupport { Available = false });

        var args = merger.BuildArguments([new AudioMergeClip("/data/1/a.flv", 0, 1000)]);

        Assert.Equal("wav", args[Array.IndexOf(args.ToArray(), "-f") + 1]);
        Assert.DoesNotContain("-b:a", args);
        Assert.Equal("audio/wav", merger.ContentType);
    }

    [Fact]
    public void ContentType_WithMp3Encoder_IsMpeg()
    {
        Assert.Equal("audio/mpeg", Create().ContentType);
    }
}
