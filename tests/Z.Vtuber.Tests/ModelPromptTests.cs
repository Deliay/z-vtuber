using Z.Vtuber.Training.Ai;
using Z.Vtuber.Training.Ai.Prompts;

namespace Z.Vtuber.Tests;

/// <summary>
/// Plan 1.6：prompt 组装硬约束与输入注入断言。
/// </summary>
public class ModelPromptTests
{
    [Fact]
    public void RecognizePrompt_ContainsJsonOnlyConstraintAndAudio()
    {
        var audio = new ChatAudio("QUJD", "audio/mpeg");

        var prompt = RecognizePromptBuilder.Build(null, audio);

        Assert.Contains("只输出 JSON", prompt.System);
        Assert.Contains("{\"text\"", prompt.System);
        Assert.Contains("{\"text\"", prompt.User);
        Assert.Same(audio, prompt.Audio);
    }

    [Fact]
    public void RecognizePrompt_IncludesExistingTextAsHint()
    {
        var prompt = RecognizePromptBuilder.Build("这是已有文本", audio: null);

        Assert.Contains("这是已有文本", prompt.User);
        Assert.Null(prompt.Audio);
    }

    [Fact]
    public void EmotionPrompt_ContainsFixedTenFieldsAndRanges()
    {
        const string itemText = "我今天真的太难过了，什么都做不好。";

        var prompt = EmotionPromptBuilder.Build(itemText);

        foreach (var field in new[]
        {
            "emotionControlWeight", "emotionDescription", "happy", "angry", "sad",
            "afraid", "disgusted", "melancholic", "surprised", "calm",
        })
        {
            Assert.Contains(field, prompt.System);
        }

        Assert.Contains("0~1", prompt.System);
        Assert.Contains("200", prompt.System);
        Assert.Contains("只输出 JSON", prompt.System);
        Assert.Equal(itemText, prompt.User);
        Assert.Null(prompt.Audio);
    }
}
