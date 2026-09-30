using Z.Vtuber.Training.Contracts;

namespace Z.Vtuber.Training.Clients;

/// <summary>上游 TTS 音色参考（必带）：音频字节 + Content-Type。</summary>
public sealed record TtsSpeakerReference(byte[] Bytes, string ContentType);

/// <summary>上游 TTS 情绪参考：音频字节 + Content-Type + 命中条目的情绪标注（供 emo_vector/emo_text/emo_alpha）。</summary>
public sealed record TtsEmotionReference(byte[] Bytes, string ContentType, EmotionResult Emotion);

/// <summary>合成请求：文本 + 语种 + 音色参考（必带）+ 情绪参考（可选）。</summary>
public sealed record TtsSynthesizeRequest(
    string Text,
    string Lang,
    TtsSpeakerReference Speaker,
    TtsEmotionReference? Emotion);

/// <summary>
/// 上游 index-tts-api（IndexTTS-2.5）客户端：<c>POST /synthesize</c>（multipart/form-data）→ WAV 字节。
/// 超时/非 2xx → <see cref="TtsUnavailableException"/>（映射 502 <c>tts service unavailable</c>）。
/// </summary>
public interface ITtsClient
{
    Task<byte[]> SynthesizeAsync(TtsSynthesizeRequest request, CancellationToken ct = default);
}

/// <summary>上游 TTS 不可用（不可达/超时/非 2xx/空响应）→ 映射 502 <c>tts service unavailable</c>。</summary>
public sealed class TtsUnavailableException(string message, Exception? inner = null)
    : Exception(message, inner);
