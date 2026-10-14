using Z.Vtuber.Training.Ai;
using Z.Vtuber.Training.Contracts;
using Z.Vtuber.Training.Synthesis;

namespace Z.Vtuber.Tests;

/// <summary>
/// 语音合成 user 指令组装（纯函数）：角色指令 + 参考转写（可空）+ 语种提示（非默认才附）+ 情绪指令（可空）；
/// 目标文本不进 user 指令（由 assistant 消息承载）。情绪指令描述优先、空描述按八维最强维度映射。
/// </summary>
public class SpeechPromptBuilderTests
{
    private static SpeechSynthesizeRequest Request(
        string? transcript = "参考内容", string lang = "ZH", string? emotion = null) =>
        new([1], "audio/mpeg", transcript, "目标文本", lang, emotion);

    [Fact]
    public void Build_IncludesRole_AndTranscript_ExcludesTargetText()
    {
        var prompt = SpeechPromptBuilder.Build(Request());

        Assert.Contains("参考音频中的音色", prompt);
        Assert.Contains("参考音频的内容是：「参考内容」", prompt);
        Assert.DoesNotContain("目标文本", prompt);
        Assert.DoesNotContain("待朗读的文本", prompt);
    }

    [Fact]
    public void Build_WithoutTranscript_OmitsTranscriptLine()
    {
        var prompt = SpeechPromptBuilder.Build(Request(transcript: null));

        Assert.DoesNotContain("参考音频的内容是", prompt);
    }

    [Fact]
    public void Build_BlankTranscript_OmitsTranscriptLine()
    {
        var prompt = SpeechPromptBuilder.Build(Request(transcript: "   "));

        Assert.DoesNotContain("参考音频的内容是", prompt);
    }

    [Theory]
    [InlineData("ZH", "请使用中文朗读。", false)]
    [InlineData("EN", "请使用英文朗读。", true)]
    [InlineData("JA", "请使用日文朗读。", true)]
    public void Build_AddsLanguageHintOnlyWhenNonDefault(string lang, string hint, bool expected)
    {
        var prompt = SpeechPromptBuilder.Build(Request(lang: lang));

        Assert.Equal(expected, prompt.Contains(hint));
    }

    [Fact]
    public void Build_WithEmotionInstruction_IncludesIt()
    {
        var prompt = SpeechPromptBuilder.Build(Request(emotion: "请以「生气」的情绪朗读。"));

        Assert.Contains("请以「生气」的情绪朗读。", prompt);
    }

    [Fact]
    public void BuildEmotionInstruction_Null_ReturnsNull()
    {
        Assert.Null(SpeechPromptBuilder.BuildEmotionInstruction(null));
    }

    [Fact]
    public void BuildEmotionInstruction_PrefersDescription()
    {
        var emotion = new EmotionResult(5, "平静", 0, 0.9, 0, 0, 0, 0, 0, 0);

        Assert.Equal("请以「平静」的情绪朗读。", SpeechPromptBuilder.BuildEmotionInstruction(emotion));
    }

    [Fact]
    public void BuildEmotionInstruction_EmptyDescription_UsesStrongestDimension()
    {
        var emotion = new EmotionResult(5, "  ", 0, 0.9, 0, 0, 0, 0, 0, 0);

        Assert.Equal("请以「生气」的情绪朗读。", SpeechPromptBuilder.BuildEmotionInstruction(emotion));
    }

    [Fact]
    public void BuildEmotionInstruction_TieBreaksByDimensionOrder()
    {
        // happy 与 angry 同分（0.9）：按 Dimensions 顺序取 happy（开心）。
        var emotion = new EmotionResult(5, "", 0.9, 0.9, 0, 0, 0, 0, 0, 0);

        Assert.Equal("请以「开心」的情绪朗读。", SpeechPromptBuilder.BuildEmotionInstruction(emotion));
    }
}
