using System.Text;
using Z.Vtuber.RecordingTranscript.Api.Contracts;
using Z.Vtuber.RecordingTranscript.Api.Subtitles;

namespace Z.Vtuber.Tests;

/// <summary>
/// Plan 1.3：SRT/ASS 渲染 golden 断言——格式细节即契约
/// （SRT 小时两位起、超 99 小时进位；ASS 厘秒向下取整、转义、固定文件头；UTF-8 BOM；空片段）。
/// </summary>
public class SubtitleRendererTests
{
    private static TranscriptSegmentDto Seg(long start, long end, string text) => new(start, end, text);

    [Fact]
    public void FormatSrtTimestamp_UsesTwoDigitHoursAndCommaMillis()
    {
        Assert.Equal("00:00:00,000", SubtitleRenderer.FormatSrtTimestamp(0));
        Assert.Equal("00:00:01,234", SubtitleRenderer.FormatSrtTimestamp(1234));
        Assert.Equal("00:01:00,050", SubtitleRenderer.FormatSrtTimestamp(60_050));
        Assert.Equal("01:00:00,000", SubtitleRenderer.FormatSrtTimestamp(3_600_000));
        Assert.Equal("12:34:56,789", SubtitleRenderer.FormatSrtTimestamp(45_296_789));
    }

    [Fact]
    public void FormatSrtTimestamp_Over99Hours_NaturallyCarries()
    {
        // 小时两位「起」，超 99 小时自然进位为三位，不截断
        Assert.Equal("100:00:00,000", SubtitleRenderer.FormatSrtTimestamp(100L * 3_600_000));
    }

    [Fact]
    public void FormatAssTimestamp_UsesCentisecondFloor()
    {
        // 厘秒向下取整：1234ms → .23，1999ms → .99，1009ms → .00
        Assert.Equal("0:00:00.00", SubtitleRenderer.FormatAssTimestamp(0));
        Assert.Equal("0:00:01.23", SubtitleRenderer.FormatAssTimestamp(1234));
        Assert.Equal("0:00:01.99", SubtitleRenderer.FormatAssTimestamp(1999));
        Assert.Equal("0:00:01.00", SubtitleRenderer.FormatAssTimestamp(1009));
        Assert.Equal("0:01:01.23", SubtitleRenderer.FormatAssTimestamp(61_234));
        Assert.Equal("1:00:00.00", SubtitleRenderer.FormatAssTimestamp(3_600_000));
    }

    [Fact]
    public void RenderSrt_MatchesGolden()
    {
        var srt = SubtitleRenderer.RenderSrt(
        [
            Seg(1234, 5678, "第一句"),
            Seg(6000, 7000, "第二行\n换行保留"),
            Seg(100L * 3_600_000, 100L * 3_600_000 + 500, "超长时长"),
        ]);

        // 序号从 1 起；HH:MM:SS,mmm；条目间空行；文本换行原样保留
        const string golden =
            "1\n00:00:01,234 --> 00:00:05,678\n第一句\n\n" +
            "2\n00:00:06,000 --> 00:00:07,000\n第二行\n换行保留\n\n" +
            "3\n100:00:00,000 --> 100:00:00,500\n超长时长\n\n";
        Assert.Equal(golden, srt);
    }

    [Theory]
    [InlineData(2000, 1000)]
    [InlineData(500, 500)]
    public void RenderSrt_EndNotAfterStart_RendersOriginalValuesWithoutThrowing(long start, long end)
    {
        var srt = SubtitleRenderer.RenderSrt([Seg(start, end, "倒退片段")]);

        Assert.Equal(
            $"1\n{SubtitleRenderer.FormatSrtTimestamp(start)} --> {SubtitleRenderer.FormatSrtTimestamp(end)}\n倒退片段\n\n",
            srt);
    }

    [Fact]
    public void RenderSrt_NoSegments_ReturnsEmptyString()
    {
        Assert.Equal(string.Empty, SubtitleRenderer.RenderSrt([]));
    }

    [Fact]
    public void RenderAss_MatchesGolden()
    {
        var ass = SubtitleRenderer.RenderAss(
        [
            Seg(1234, 5678, "第一句"),
            Seg(60_567, 90_999, "含{括号}\n换行"),
        ]);

        const string golden =
            "[Script Info]\n" +
            "ScriptType: v4.00+\n" +
            "PlayResX: 1920\n" +
            "PlayResY: 1080\n" +
            "WrapStyle: 2\n" +
            "ScaledBorderAndShadow: yes\n" +
            "\n" +
            "[V4+ Styles]\n" +
            "Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding\n" +
            "Style: Default,Microsoft YaHei,64,&H00FFFFFF,&H000000FF,&H00000000,&H00000000,0,0,0,0,100,100,0,0,1,2,0,2,10,10,60,1\n" +
            "\n" +
            "[Events]\n" +
            "Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text\n" +
            "Dialogue: 0,0:00:01.23,0:00:05.67,Default,,0,0,0,,第一句\n" +
            "Dialogue: 0,0:01:00.56,0:01:30.99,Default,,0,0,0,,含\\{括号\\}\\N换行\n";
        Assert.Equal(golden, ass);
    }

    [Fact]
    public void RenderAss_NoSegments_ReturnsHeaderOnly()
    {
        const string headerOnly =
            "[Script Info]\n" +
            "ScriptType: v4.00+\n" +
            "PlayResX: 1920\n" +
            "PlayResY: 1080\n" +
            "WrapStyle: 2\n" +
            "ScaledBorderAndShadow: yes\n" +
            "\n" +
            "[V4+ Styles]\n" +
            "Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding\n" +
            "Style: Default,Microsoft YaHei,64,&H00FFFFFF,&H000000FF,&H00000000,&H00000000,0,0,0,0,100,100,0,0,1,2,0,2,10,10,60,1\n" +
            "\n" +
            "[Events]\n" +
            "Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text\n";

        Assert.Equal(headerOnly, SubtitleRenderer.RenderAss([]));
    }

    [Fact]
    public void RenderAss_EndNotAfterStart_RendersOriginalValuesWithoutThrowing()
    {
        var ass = SubtitleRenderer.RenderAss([Seg(2000, 1000, "倒退片段")]);

        Assert.Contains("Dialogue: 0,0:00:02.00,0:00:01.00,Default,,0,0,0,,倒退片段\n", ass);
    }

    [Fact]
    public void RenderedOutput_EncodedUtf8WithBom_HasBomPrefix()
    {
        // 编码口径即契约：控制器以 Encoding.UTF8.GetPreamble() + GetBytes(text) 产出 UTF-8 with BOM
        var text = SubtitleRenderer.RenderSrt([Seg(0, 1000, "字幕")]);
        var bytes = Encoding.UTF8.GetPreamble()
            .Concat(Encoding.UTF8.GetBytes(text))
            .ToArray();

        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes.Take(3).ToArray());
        Assert.Equal(text, Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3));
    }
}
