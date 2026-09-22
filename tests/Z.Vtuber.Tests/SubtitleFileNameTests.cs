using Z.Vtuber.RecordingTranscript.Api.Subtitles;

namespace Z.Vtuber.Tests;

/// <summary>
/// Plan 1.4 文件名单测：sanitize 仅保留中英文/数字/-/_（其余折叠 _、不残留 CR/LF）、
/// 任一段为空兜底 transcript-{taskId 前 8 位}。
/// </summary>
public class SubtitleFileNameTests
{
    private const string TaskId = "66f0a1b2c3d4e5f6a7b8c9d0";

    [Fact]
    public void Build_KeepsLettersDigitsDashUnderscoreAndChinese()
    {
        Assert.Equal(
            "Title_v2-标题A1-qwen-fun-asr.srt",
            SubtitleFileName.Build("Title_v2-标题A1", "qwen-fun-asr", TaskId, "srt"));
    }

    [Fact]
    public void Build_JoinsSanitizedTitleAndModelName()
    {
        Assert.Equal(
            "深夜杂谈-qwen-fun-asr.ass",
            SubtitleFileName.Build("深夜杂谈", "qwen-fun-asr", TaskId, "ass"));
    }

    [Fact]
    public void Build_FoldsDisallowedCharactersIntoSingleUnderscore()
    {
        // 【A/B】:C → 非法字符折叠、连续折叠为单个 _、两端 _ 去除
        Assert.Equal(
            "A_B_C-model.srt",
            SubtitleFileName.Build("【A/B】:C", "model", TaskId, "srt"));
    }

    [Fact]
    public void Build_SanitizedResult_ContainsNoCarriageReturnOrLineFeed()
    {
        var name = SubtitleFileName.Build("a\r\nb\nc\rd", "m\r\ne", TaskId, "srt");

        Assert.DoesNotContain('\r', name);
        Assert.DoesNotContain('\n', name);
        Assert.Equal("a_b_c_d-m_e.srt", name);
    }

    [Fact]
    public void Build_EmptyTitle_FallsBackToTranscriptTaskIdPrefix()
    {
        Assert.Equal(
            "transcript-66f0a1b2.srt",
            SubtitleFileName.Build("!!!", "qwen-fun-asr", TaskId, "srt"));
    }

    [Fact]
    public void Build_NullModelName_FallsBackToTranscriptTaskIdPrefix()
    {
        Assert.Equal(
            "transcript-66f0a1b2.ass",
            SubtitleFileName.Build("深夜杂谈", null, TaskId, "ass"));
    }

    [Fact]
    public void Build_WhitespaceOnlyTitle_FallsBackToTranscriptTaskIdPrefix()
    {
        Assert.Equal(
            "transcript-66f0a1b2.srt",
            SubtitleFileName.Build("   ", "  ", TaskId, "srt"));
    }

    [Fact]
    public void Build_ShortTaskId_UsesWholeTaskIdForFallback()
    {
        Assert.Equal(
            "transcript-abc.srt",
            SubtitleFileName.Build(null, null, "abc", "srt"));
    }

    [Fact]
    public void Build_ExtensionIsAppendedAsIs()
    {
        Assert.Equal(
            "标题-model.ssa",
            SubtitleFileName.Build("标题", "model", TaskId, "ssa"));
    }
}
