using System.Text;

namespace Z.Vtuber.Training.Ai.Prompts;

/// <summary>
/// 重新识别（语音转写校对）prompt 组装：硬约束「只输出 JSON、格式 <c>{"text":"..."}</c>」，
/// 可选把条目已有文本作为提示/上下文，音频经 <see cref="ChatPrompt.Audio"/> 传入。
/// </summary>
public static class RecognizePromptBuilder
{
    public const string SystemPrompt =
        "你是一个语音转写校对助手。请根据用户提供的音频（以及可选的已有文本提示）输出准确、通顺的转写文本。"
        + "你只输出 JSON，格式固定为 {\"text\":\"识别出的文本\"}，不要输出任何解释、Markdown 或代码块围栏。";

    public static ChatPrompt Build(string? hintText, ChatAudio? audio)
    {
        var user = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(hintText))
        {
            user.AppendLine("已有文本（仅供参考，可能包含错别字或缺失，请结合音频校对修正）：");
            user.AppendLine(hintText);
            user.AppendLine();
        }

        user.Append("请根据音频进行语音转写校对，只输出 JSON：{\"text\":\"识别出的文本\"}，不要输出任何解释或代码块围栏。");

        return new ChatPrompt(SystemPrompt, user.ToString(), audio);
    }
}
