using Z.Vtuber.Common.Persistence.Entities;

namespace Z.Vtuber.Training.Ai;

/// <summary>语音合成请求：参考语音（必带，含转写）+ 目标文本 + 语种 + 可选情绪指令。</summary>
public sealed record SpeechSynthesizeRequest(
    byte[] ReferenceAudio,
    string ReferenceContentType,
    string? ReferenceTranscript,
    string Text,
    string Lang,
    string? EmotionInstruction);

/// <summary>
/// 语音大模型客户端抽象：语音 + 文本输入 → 语音输出（WAV 字节）。
/// 不可达/超时/非 2xx → <see cref="SpeechModelUnavailableException"/>（映射 502 <c>model service unavailable</c>）；
/// 响应缺音频段 → <see cref="ModelOutputException"/>（映射 502 <c>invalid model output</c>）；
/// Provider 类型不支持音频输出 → <see cref="ModelAudioOutputNotSupportedException"/>（映射 400）。
/// 日志绝不打 apiKey、文本与音频内容。
/// </summary>
public interface ISpeechModelClient
{
    Task<byte[]> SynthesizeSpeechAsync(
        TrainingProviderRecord provider, TrainingModelRecord model,
        SpeechSynthesizeRequest request, CancellationToken ct = default);
}

/// <summary>
/// 按 <c>provider.Type</c> 派发（openai → OpenAI-like <c>/chat/completions</c>）；
/// claude 及其他类型抛 <see cref="ModelAudioOutputNotSupportedException"/>（无音频输出能力）。
/// </summary>
public interface ISpeechModelClientFactory
{
    ISpeechModelClient Create(string providerType);
}

/// <summary>语音大模型不可用（不可达/超时/非 2xx/空响应）→ 映射 502 <c>model service unavailable</c>。</summary>
public sealed class SpeechModelUnavailableException(string message, Exception? inner = null)
    : Exception(message, inner);

/// <summary>Provider 类型不支持音频输出（Claude-like 等）→ 映射 400 <c>model does not support audio output</c>。</summary>
public sealed class ModelAudioOutputNotSupportedException()
    : Exception("model does not support audio output");
