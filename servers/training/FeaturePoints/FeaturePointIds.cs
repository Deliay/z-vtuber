using Z.Vtuber.Common.Contracts;

namespace Z.Vtuber.Training.FeaturePoints;

/// <summary>功能点定义：slug + 中文名 + 能力要求。</summary>
public sealed record FeaturePointDefinition(
    string Slug,
    string Name,
    IReadOnlyList<string> InputCapabilities,
    IReadOnlyList<string> OutputCapabilities);

/// <summary>
/// 本期功能点常量（与 PRD §3.2 能力表一一对应）：
/// 数据清洗 / 输入语音识别情绪（输入 语音、文本 → 输出 文本）、
/// 输入文本识别情绪（输入 文本 → 输出 文本）。
/// </summary>
public static class FeaturePointIds
{
    /// <summary>数据清洗（重新识别）。</summary>
    public const string DatasetClean = "dataset-clean";

    /// <summary>输入语音识别情绪（批量情绪识别）。</summary>
    public const string EmotionFromAudio = "emotion-from-audio";

    /// <summary>输入文本识别情绪（合成情绪推断）。</summary>
    public const string EmotionFromText = "emotion-from-text";

    /// <summary>绘图生成（文本 + 图片 → 图片）。</summary>
    public const string ImageGenerate = "image-generate";

    private static readonly string[] AudioTextInput =
        [TrainingModelCapabilities.Audio, TrainingModelCapabilities.Text];

    private static readonly string[] TextInput = [TrainingModelCapabilities.Text];

    private static readonly string[] TextOutput = [TrainingModelCapabilities.Text];

    private static readonly string[] TextImageInput =
        [TrainingModelCapabilities.Text, TrainingModelCapabilities.Image];

    private static readonly string[] ImageOutput = [TrainingModelCapabilities.Image];

    public static readonly FeaturePointDefinition DatasetCleanDefinition =
        new(DatasetClean, "数据清洗", AudioTextInput, TextOutput);

    public static readonly FeaturePointDefinition EmotionFromAudioDefinition =
        new(EmotionFromAudio, "输入语音识别情绪", AudioTextInput, TextOutput);

    public static readonly FeaturePointDefinition EmotionFromTextDefinition =
        new(EmotionFromText, "输入文本识别情绪", TextInput, TextOutput);

    public static readonly FeaturePointDefinition ImageGenerateDefinition =
        new(ImageGenerate, "绘图生成", TextImageInput, ImageOutput);
}
