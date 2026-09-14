using Z.Vtuber.Recording.Live;
using Z.Vtuber.Recording.Recording;

namespace Z.Vtuber.Tests;

public class FfmpegProcessBuilderTests
{
    private static LiveStreamCandidate HttpStream() =>
        new("http://cdn/stream.flv", "http_stream", "flv", "avc", 4000);

    private static LiveStreamCandidate HlsStream() =>
        new("http://cdn/index.m3u8", "http_hls", "ts", "avc");

    private static FfmpegCommand Build(
        LiveStreamCandidate stream,
        int? bitrate = 4000,
        bool av1Enabled = true) =>
        FfmpegProcessBuilder.Build(
            stream,
            "/data/raw.flv",
            av1Enabled ? "/data/av1.flv" : null,
            "SESSDATA=abc",
            "https://live.bilibili.com/",
            "UA/1.0",
            bitrate,
            4000,
            av1Enabled);

    [Fact]
    public void HttpStream_InjectsHeaders()
    {
        var command = Build(HttpStream());

        var index = command.Arguments.ToList().IndexOf("-headers");
        Assert.True(index >= 0);
        var headers = command.Arguments[index + 1];
        Assert.Contains("Cookie: SESSDATA=abc", headers);
        Assert.Contains("Referer: https://live.bilibili.com/", headers);
        Assert.Contains("User-Agent: UA/1.0", headers);
    }

    [Fact]
    public void Hls_DoesNotInjectHeaders_AndUsesUrlDirectly()
    {
        var command = Build(HlsStream());

        Assert.False(FfmpegProcessBuilder.RequiresHeaders(HlsStream()));
        Assert.DoesNotContain("-headers", command.Arguments);
        var index = command.Arguments.ToList().IndexOf("-i");
        Assert.Equal("http://cdn/index.m3u8", command.Arguments[index + 1]);
    }

    [Fact]
    public void BuildsThreeOutputs()
    {
        var command = Build(HttpStream());
        var args = command.Arguments.ToList();

        Assert.Contains("/data/raw.flv", args);
        Assert.Contains("/data/av1.flv", args);
        Assert.Contains(FfmpegProcessBuilder.AudioPipe, args);

        // RAW 原样封装
        var rawIndex = args.IndexOf("/data/raw.flv");
        Assert.Equal("flv", args[rawIndex - 1]);
        Assert.Equal("copy", args[rawIndex - 3]);

        // AV1 二压
        Assert.Contains("av1_nvenc", args);
        Assert.Contains("fullres", args);
        Assert.Contains("-spatial-aq", args);
        Assert.Contains("-temporal-aq", args);

        // 实时音频
        var pipeIndex = args.IndexOf(FfmpegProcessBuilder.AudioPipe);
        Assert.Equal("flv", args[pipeIndex - 1]);
        Assert.Equal("128k", args[pipeIndex - 3]);
        Assert.Equal("aac", args[pipeIndex - 5]);
    }

    [Fact]
    public void Av1TargetBitrate_IsNinetyPercent()
    {
        var command = Build(HttpStream(), bitrate: 5000);

        var index = command.Arguments.ToList().IndexOf("-b:v");
        Assert.Equal("4500k", command.Arguments[index + 1]);
    }

    [Fact]
    public void Av1TargetBitrate_FallsBackToDefault_WhenMetadataMissing()
    {
        var command = Build(HttpStream(), bitrate: null);

        var index = command.Arguments.ToList().IndexOf("-b:v");
        Assert.Equal("3600k", command.Arguments[index + 1]);
    }

    [Fact]
    public void Av1Disabled_SkipsSecondOutput()
    {
        var command = Build(HttpStream(), av1Enabled: false);

        Assert.DoesNotContain("av1_nvenc", command.Arguments);
        Assert.DoesNotContain("/data/av1.flv", command.Arguments);
        Assert.Contains("/data/raw.flv", command.Arguments);
        Assert.Contains(FfmpegProcessBuilder.AudioPipe, command.Arguments);
    }
}
