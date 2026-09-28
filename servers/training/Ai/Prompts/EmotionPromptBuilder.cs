namespace Z.Vtuber.Training.Ai.Prompts;

/// <summary>
/// 情绪识别 prompt 组装：依据给定台词判断说话人情绪，硬约束「只输出 JSON、十字段固定名、
/// 数值 0~1、描述 ≤ 200 字」；情绪识别只用文本，<see cref="ChatPrompt.Audio"/> 恒为 null。
/// </summary>
public static class EmotionPromptBuilder
{
    public const string SystemPrompt =
        "你是一个情绪分析助手。请依据给定的台词判断说话人的情绪。"
        + "你只输出 JSON，字段名固定为 emotionControlWeight、emotionDescription、happy、angry、sad、"
        + "afraid、disgusted、melancholic、surprised、calm 十项；其中 happy/angry/sad/afraid/disgusted/"
        + "melancholic/surprised/calm 为情绪强度数值（0~1），emotionControlWeight 为情绪控制权重数值，"
        + "emotionDescription 为不超过 200 字的中文描述。不要输出任何解释、Markdown 或代码块围栏。";

    public static ChatPrompt Build(string text) =>
        new(SystemPrompt, text ?? string.Empty, null);
}
