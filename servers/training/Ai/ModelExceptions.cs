namespace Z.Vtuber.Training.Ai;

/// <summary>未知 Provider 类型（派发失败）。</summary>
public sealed class ProviderTypeException(string message) : Exception(message);

/// <summary>模型类型不支持音频输入（Claude-like 无音频模态）→ 映射 400 <c>model does not support audio input</c>。</summary>
public sealed class ModelAudioNotSupportedException()
    : Exception("model does not support audio input");

/// <summary>Provider 类型不支持图片输出（Claude-like 等）→ 映射 400 <c>model does not support image output</c>。</summary>
public sealed class ModelImageNotSupportedException()
    : Exception("model does not support image output");

/// <summary>模型输出非预期 JSON → 映射 502 <c>invalid model output</c>。</summary>
public sealed class ModelOutputException(string message) : Exception(message);

/// <summary>Provider Endpoint 响应无法解析 → 映射 502 <c>invalid provider response</c>。</summary>
public sealed class InvalidProviderResponseException(string message) : Exception(message);

/// <summary>音频取数失败（不可达/超时/非 2xx/404）→ 映射 502 <c>audio unavailable</c>。</summary>
public sealed class AudioUnavailableException(string message) : Exception(message);

/// <summary>音频字节超上限 → 映射 400 <c>audio too large</c>。</summary>
public sealed class AudioTooLargeException(string message) : Exception(message);
