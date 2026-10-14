using System.Net.Http;
using System.Text.Json;
using Z.Vtuber.Common.Persistence.Entities;
using Z.Vtuber.Training.Ai;
using Z.Vtuber.Training.Clients;

namespace Z.Vtuber.Tests;

/// <summary>训练音频生成相关测试替身。</summary>
internal static class TrainingSynthesizeFakes
{
    public static JsonElement Json(string raw)
    {
        using var document = JsonDocument.Parse(raw);
        return document.RootElement.Clone();
    }
}

/// <summary>固定返回同一 <see cref="HttpClient"/> 的 <see cref="IHttpClientFactory"/> 替身（契约测试用）。</summary>
internal sealed class StubHttpClientFactory(HttpClient client) : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => client;
}

/// <summary>音频取数替身：按 URI 映射返回片段；<see cref="FailingUris"/> 中的 URI 抛不可用。</summary>
internal sealed class StubAudioClient : IRecordingAudioClient
{
    public Dictionary<string, AudioClip> Clips { get; } = [];

    public HashSet<string> FailingUris { get; } = [];

    public List<string> RequestedUris { get; } = [];

    /// <summary>合并请求记录（按调用顺序）。</summary>
    public List<IReadOnlyList<AudioClipRequest>> MergeRequests { get; } = [];

    /// <summary>合并结果（默认固定两字节）；为 null 时返回默认。</summary>
    public AudioClip? MergeResult { get; set; } = new([9, 9], "audio/mpeg");

    /// <summary>合并抛出的异常（模拟合并不可用）。</summary>
    public Exception? MergeThrow { get; set; }

    public Task<AudioClip> GetClipAsync(
        string audioUri, long startMs, long? endMs, CancellationToken ct = default)
    {
        RequestedUris.Add(audioUri);
        if (FailingUris.Contains(audioUri) || !Clips.TryGetValue(audioUri, out var clip))
        {
            return Task.FromException<AudioClip>(new AudioUnavailableException("audio unavailable"));
        }

        return Task.FromResult(clip);
    }

    public Task<AudioClip> MergeClipsAsync(
        IReadOnlyList<AudioClipRequest> items, CancellationToken ct = default)
    {
        MergeRequests.Add(items);
        if (MergeThrow is not null)
        {
            return Task.FromException<AudioClip>(MergeThrow);
        }

        return Task.FromResult(MergeResult ?? new AudioClip([9, 9], "audio/mpeg"));
    }
}

/// <summary>语音大模型替身：记录请求并返回固定 WAV 字节；可抛异常。</summary>
internal sealed class StubSpeechClient : ISpeechModelClient
{
    public byte[] Bytes { get; set; } = [1, 2, 3, 4];

    public Exception? Throw { get; set; }

    public List<SpeechSynthesizeRequest> Requests { get; } = [];

    public Task<byte[]> SynthesizeSpeechAsync(
        TrainingProviderRecord provider, TrainingModelRecord model,
        SpeechSynthesizeRequest request, CancellationToken ct = default)
    {
        Requests.Add(request);
        return Throw is not null
            ? Task.FromException<byte[]>(Throw)
            : Task.FromResult(Bytes);
    }
}
