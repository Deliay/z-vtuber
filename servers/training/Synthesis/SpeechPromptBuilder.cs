using Z.Vtuber.Training.Ai;
using Z.Vtuber.Training.Contracts;

namespace Z.Vtuber.Training.Synthesis;

/// <summary>
/// 语音合成 user 指令组装（纯函数）：角色指令 → 参考转写（可空）→ 语种提示（非默认才附）→
/// 情绪指令（可空）。目标文本<b>不</b>进入 user 指令：上游（MiMo voiceclone）要求目标文本只放
/// assistant 消息，user 仅承载风格/情绪指令。情绪指令由 <see cref="EmotionResult"/> 生成（描述优先；
/// 描述为空时按八维最强维度映射情绪名，同分按 <see cref="EmotionVector.Dimensions"/> 顺序取先）。
/// </summary>
public static class SpeechPromptBuilder
{
    /// <summary>八维维度名 → 中文情绪名（描述为空时的兜底）。</summary>
    private static readonly Dictionary<string, string> DimensionLabels = new(StringComparer.Ordinal)
    {
        ["happy"] = "开心",
        ["angry"] = "生气",
        ["sad"] = "悲伤",
        ["afraid"] = "害怕",
        ["disgusted"] = "厌恶",
        ["melancholic"] = "忧郁",
        ["surprised"] = "惊讶",
        ["calm"] = "平静",
    };

    private static readonly Dictionary<string, string> LanguageNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ZH"] = "中文",
        ["EN"] = "英文",
        ["JA"] = "日文",
        ["ES"] = "西班牙文",
        ["AR"] = "阿拉伯文",
    };

    /// <summary>由情绪标注生成自然语言指令；null → null（中性，不注入）。</summary>
    public static string? BuildEmotionInstruction(EmotionResult? emotion)
    {
        if (emotion is null)
        {
            return null;
        }

        var label = string.IsNullOrWhiteSpace(emotion.EmotionDescription)
            ? StrongestDimensionLabel(emotion)
            : emotion.EmotionDescription.Trim();

        return $"请以「{label}」的情绪朗读。";
    }

    /// <summary>组装 user 指令文本（各行以换行拼接）；<b>不含</b>目标文本（由 assistant 消息承载）。</summary>
    public static string Build(SpeechSynthesizeRequest request, string defaultLang = "ZH")
    {
        var lines = new List<string>
        {
            "请使用参考音频中的音色朗读。",
        };

        if (!string.IsNullOrWhiteSpace(request.ReferenceTranscript))
        {
            lines.Add($"参考音频的内容是：「{request.ReferenceTranscript}」。");
        }

        if (!string.IsNullOrWhiteSpace(request.Lang)
            && !string.Equals(request.Lang, defaultLang, StringComparison.OrdinalIgnoreCase)
            && LanguageNames.TryGetValue(request.Lang, out var language))
        {
            lines.Add($"请使用{language}朗读。");
        }

        if (!string.IsNullOrWhiteSpace(request.EmotionInstruction))
        {
            lines.Add(request.EmotionInstruction);
        }

        return string.Join("\n", lines);
    }

    /// <summary>八维最强维度映射情绪名；全 0/负值仍取八维顺序首个（happy），同分按 <see cref="EmotionVector.Dimensions"/> 顺序取先。</summary>
    private static string StrongestDimensionLabel(EmotionResult emotion)
    {
        var vector = EmotionVector.FromEmotion(emotion);
        var bestIndex = 0;
        for (var i = 1; i < vector.Length; i++)
        {
            if (vector[i] > vector[bestIndex])
            {
                bestIndex = i;
            }
        }

        var dimension = EmotionVector.Dimensions[bestIndex];
        return DimensionLabels.TryGetValue(dimension, out var label) ? label : dimension;
    }
}
