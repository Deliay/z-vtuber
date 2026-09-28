namespace Z.Vtuber.Training.Clients;

/// <summary>音频片段（字节 + Content-Type）。</summary>
public sealed record AudioClip(byte[] Bytes, string ContentType);

/// <summary>取条目音频片段字节：复用 recording 片段端点；失败抛专用异常供控制器映射错误码。</summary>
public interface IRecordingAudioClient
{
    /// <summary>
    /// 解析 <paramref name="audioUri"/>（<c>b/live/{roomId}/recordings/{file}</c>）并取片段；
    /// <paramref name="endMs"/> 为空时按 <c>Ai:AudioClipDefaultDurationMs</c> 补齐再请求。
    /// uri 空/不可解析抛 <see cref="ArgumentException"/>；取数失败抛 <see cref="Ai.AudioUnavailableException"/>；
    /// 超上限抛 <see cref="Ai.AudioTooLargeException"/>。
    /// </summary>
    Task<AudioClip> GetClipAsync(
        string audioUri, long startMs, long? endMs, CancellationToken ct = default);
}
