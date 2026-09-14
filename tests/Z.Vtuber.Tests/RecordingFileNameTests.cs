using Z.Vtuber.Recording.Recording;

namespace Z.Vtuber.Tests;

public class RecordingFileNameTests
{
    private static readonly DateTime SampleDate = new(2026, 9, 13, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Render_Raw_BuildsExpectedTemplate()
    {
        var name = RecordingFileName.Render("RAW", 123, "某主播", SampleDate, "晚间歌回");

        Assert.Equal("[RAW][123][某主播] 2026-09-13-晚间歌回.flv", name);
    }

    [Fact]
    public void Render_Encoding_IsUppercased()
    {
        var name = RecordingFileName.Render("av1", 9, "anchor", SampleDate, "session");

        Assert.StartsWith("[AV1][9]", name);
    }

    [Fact]
    public void Render_RawAndAv1_DifferOnlyByEncode()
    {
        var raw = RecordingFileName.Render("RAW", 123, "某主播", SampleDate, "晚间歌回");
        var av1 = RecordingFileName.Render("AV1", 123, "某主播", SampleDate, "晚间歌回");

        Assert.Equal(raw.Replace("[RAW]", "[AV1]"), av1);
    }

    [Theory]
    [InlineData("a/b", "ab")]
    [InlineData("a\\b", "ab")]
    [InlineData("a:b*c?d\"e<f>g|h", "abcdefgh")]
    public void Sanitize_RemovesIllegalCharacters(string input, string expected)
    {
        var cleaned = RecordingFileName.Sanitize(input, 80, "fallback");

        Assert.Equal(expected, cleaned);
    }

    [Fact]
    public void Sanitize_RemovesControlCharacters()
    {
        var cleaned = RecordingFileName.Sanitize("晚\u0001间\u0007歌回", 80, "fallback");

        Assert.Equal("晚间歌回", cleaned);
    }

    [Fact]
    public void Sanitize_Empty_ReturnsFallback()
    {
        Assert.Equal("fallback", RecordingFileName.Sanitize("   ", 80, "fallback"));
    }

    [Fact]
    public void Sanitize_Overlong_TruncatesTitleWithoutLosingPrefix()
    {
        var title = new string('长', 200);

        var cleaned = RecordingFileName.Sanitize(title, 80, "fallback");

        Assert.Equal(80, cleaned.Length);
    }

    [Fact]
    public void Render_KeepsRoomIdAndAnchor_WhenTitleOverlong()
    {
        var name = RecordingFileName.Render("RAW", 42, "主播", SampleDate, new string('长', 200));

        Assert.StartsWith("[RAW][42][主播] 2026-09-13-", name);
    }

    [Fact]
    public void Render_NullAnchor_UsesFallback()
    {
        var name = RecordingFileName.Render("RAW", 1, null, SampleDate, "标题");

        Assert.Contains("[未知主播]", name);
    }
}
