using Z.Vtuber.Crawler.Video;

namespace Z.Vtuber.Tests;

public sealed class VideoDownloadFileNameTests
{
    [Fact]
    public void Build_FormatsTitlePageKindAndExtension()
    {
        var name = VideoDownloadFileName.Build("某视频标题", 1, "video", "m4v");

        Assert.Equal("某视频标题.p01.video.m4v", name);
    }

    [Fact]
    public void Build_PadsPageToTwoDigits()
    {
        var name = VideoDownloadFileName.Build("t", 12, "audio", "m4a");

        Assert.Equal("t.p12.audio.m4a", name);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("///")]
    public void Build_EmptyOrOnlyIllegalTitle_FallsBack(string? title)
    {
        var name = VideoDownloadFileName.Build(title, 1, "video", "flv");

        Assert.Equal($"{VideoDownloadFileName.DefaultTitle}.p01.video.flv", name);
    }

    [Fact]
    public void Sanitize_RemovesIllegalCharsControlsAndCollapsesWhitespace()
    {
        var sanitized = VideoDownloadFileName.Sanitize("a:b\tc  /d|e", 80, "fallback");

        Assert.Equal("abc de", sanitized);
    }

    [Fact]
    public void Sanitize_TruncatesToMaxLength()
    {
        var sanitized = VideoDownloadFileName.Sanitize(new string('x', 100), 10, "fallback");

        Assert.Equal(10, sanitized.Length);
    }

    [Fact]
    public void FormatContentDisposition_EncodesUtf8FileName()
    {
        var header = VideoDownloadFileName.FormatContentDisposition("中文标题.p01.video.m4v");

        Assert.StartsWith("attachment; filename*=UTF-8''", header);
        Assert.Equal(
            "attachment; filename*=UTF-8''%E4%B8%AD%E6%96%87%E6%A0%87%E9%A2%98.p01.video.m4v",
            header);
    }
}
