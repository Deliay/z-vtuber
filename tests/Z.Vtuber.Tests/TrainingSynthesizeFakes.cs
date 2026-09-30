using System.Net.Http;
using System.Text.Json;
using MongoDB.Bson;
using Z.Vtuber.Common.Persistence.Entities;
using Z.Vtuber.Training.Ai;
using Z.Vtuber.Training.Clients;
using Z.Vtuber.Training.Vectors;

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

/// <summary>记录请求并按 Responder 应答的 Milvus REST 替身。</summary>
internal sealed class StubMilvusRestClient : IMilvusRestClient
{
    public List<(string Path, JsonElement Payload)> Requests { get; } = [];

    public Func<string, JsonElement, MilvusResponse>? Responder { get; set; }

    public Task<MilvusResponse> PostAsync(string path, object payload, CancellationToken ct = default)
    {
        var json = JsonSerializer.SerializeToElement(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Requests.Add((path, json));
        return Task.FromResult(Responder?.Invoke(path, json) ?? new MilvusResponse(0, string.Empty, null));
    }

    public int CountOf(string path) => Requests.Count(r => r.Path == path);
}

/// <summary>文本 embedding 替身（默认返回固定向量；可抛异常模拟不可用）。</summary>
internal sealed class FakeEmbeddingClient : IEmbeddingClient
{
    public Func<string, float[]>? Provider { get; set; }

    public Exception? Throw { get; set; }

    public int Calls { get; private set; }

    public string? LastText { get; private set; }

    public Task<float[]> GetEmbeddingAsync(string text, CancellationToken ct = default)
    {
        Calls++;
        LastText = text;
        if (Throw is not null)
        {
            return Task.FromException<float[]>(Throw);
        }

        return Task.FromResult(Provider?.Invoke(text) ?? [0.1f, 0.2f, 0.3f]);
    }
}

/// <summary>内存向量库替身：按 datasetId 过滤、COSINE/L2 排序 + itemId 稳定 tie-break。</summary>
internal sealed class InMemoryVectorStore : IVectorStore
{
    private sealed record Entry(string DatasetId, string ItemId, string AudioKey, float[] Vector);

    private readonly List<Entry> _text = [];
    private readonly List<Entry> _emotion = [];

    public Exception? SearchTextThrow { get; set; }

    public Exception? SearchEmotionThrow { get; set; }

    public Exception? UpsertThrow { get; set; }

    public Exception? DeleteThrow { get; set; }

    public int UpsertCalls { get; private set; }

    public int DeleteCalls { get; private set; }

    public bool EnsureCalled { get; private set; }

    public Task EnsureCollectionsAsync(CancellationToken ct = default)
    {
        EnsureCalled = true;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<VectorMatch>> SearchTextAsync(
        ObjectId datasetId, float[] query, int limit, CancellationToken ct = default)
    {
        if (SearchTextThrow is not null)
        {
            return Task.FromException<IReadOnlyList<VectorMatch>>(SearchTextThrow);
        }

        return Task.FromResult(Rank(_text, datasetId, query, limit, ascending: false));
    }

    public Task<IReadOnlyList<VectorMatch>> SearchEmotionAsync(
        ObjectId datasetId, float[] query, int limit, CancellationToken ct = default)
    {
        if (SearchEmotionThrow is not null)
        {
            return Task.FromException<IReadOnlyList<VectorMatch>>(SearchEmotionThrow);
        }

        return Task.FromResult(Rank(_emotion, datasetId, query, limit, ascending: true));
    }

    public Task UpsertItemVectorsAsync(
        ObjectId datasetId, ObjectId itemId, string audioKey,
        float[]? textEmbedding, float[]? emotionVector, CancellationToken ct = default)
    {
        if (UpsertThrow is not null)
        {
            return Task.FromException(UpsertThrow);
        }

        UpsertCalls++;
        _text.RemoveAll(e => e.DatasetId == datasetId.ToString() && e.ItemId == itemId.ToString());
        _emotion.RemoveAll(e => e.DatasetId == datasetId.ToString() && e.ItemId == itemId.ToString());
        if (textEmbedding is not null)
        {
            _text.Add(new Entry(datasetId.ToString(), itemId.ToString(), audioKey, textEmbedding));
        }

        if (emotionVector is not null)
        {
            _emotion.Add(new Entry(datasetId.ToString(), itemId.ToString(), audioKey, emotionVector));
        }

        return Task.CompletedTask;
    }

    public Task DeleteDatasetVectorsAsync(ObjectId datasetId, CancellationToken ct = default)
    {
        if (DeleteThrow is not null)
        {
            return Task.FromException(DeleteThrow);
        }

        DeleteCalls++;
        _text.RemoveAll(e => e.DatasetId == datasetId.ToString());
        _emotion.RemoveAll(e => e.DatasetId == datasetId.ToString());
        return Task.CompletedTask;
    }

    public Task DeleteItemVectorsAsync(ObjectId datasetId, ObjectId itemId, CancellationToken ct = default)
    {
        if (DeleteThrow is not null)
        {
            return Task.FromException(DeleteThrow);
        }

        DeleteCalls++;
        _text.RemoveAll(e => e.DatasetId == datasetId.ToString() && e.ItemId == itemId.ToString());
        _emotion.RemoveAll(e => e.DatasetId == datasetId.ToString() && e.ItemId == itemId.ToString());
        return Task.CompletedTask;
    }

    public void SeedText(ObjectId datasetId, ObjectId itemId, string audioKey, float[] vector) =>
        _text.Add(new Entry(datasetId.ToString(), itemId.ToString(), audioKey, vector));

    public void SeedEmotion(ObjectId datasetId, ObjectId itemId, string audioKey, float[] vector) =>
        _emotion.Add(new Entry(datasetId.ToString(), itemId.ToString(), audioKey, vector));

    private static IReadOnlyList<VectorMatch> Rank(
        List<Entry> entries, ObjectId datasetId, float[] query, int limit, bool ascending)
    {
        var scored = entries
            .Where(e => e.DatasetId == datasetId.ToString())
            .Select(e => new VectorMatch(
                ObjectId.Parse(e.ItemId), e.AudioKey, Score(e.Vector, query, ascending)))
            .ToList();

        var ordered = ascending
            ? scored.OrderBy(m => m.Score).ThenBy(m => m.ItemId)
            : scored.OrderByDescending(m => m.Score).ThenBy(m => m.ItemId);
        return ordered.Take(Math.Max(1, limit)).ToList();
    }

    private static float Score(float[] a, float[] b, bool l2)
    {
        if (a.Length != b.Length || a.Length == 0)
        {
            return l2 ? float.MaxValue : float.MinValue;
        }

        if (l2)
        {
            double sum = 0;
            for (var i = 0; i < a.Length; i++)
            {
                var d = a[i] - b[i];
                sum += d * d;
            }

            return (float)sum;
        }

        double dot = 0, na = 0, nb = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            na += a[i] * a[i];
            nb += b[i] * b[i];
        }

        var denom = Math.Sqrt(na) * Math.Sqrt(nb);
        return denom == 0 ? 0 : (float)(dot / denom);
    }
}

/// <summary>音频取数替身：按 URI 映射返回片段；<see cref="FailingUris"/> 中的 URI 抛不可用。</summary>
internal sealed class StubAudioClient : IRecordingAudioClient
{
    public Dictionary<string, AudioClip> Clips { get; } = [];

    public HashSet<string> FailingUris { get; } = [];

    public List<string> RequestedUris { get; } = [];

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
}

/// <summary>TTS 替身：记录请求并返回固定 WAV 字节；可抛异常。</summary>
internal sealed class StubTtsClient : ITtsClient
{
    public byte[] Bytes { get; set; } = [1, 2, 3, 4];

    public Exception? Throw { get; set; }

    public List<TtsSynthesizeRequest> Requests { get; } = [];

    public Task<byte[]> SynthesizeAsync(TtsSynthesizeRequest request, CancellationToken ct = default)
    {
        Requests.Add(request);
        return Throw is not null
            ? Task.FromException<byte[]>(Throw)
            : Task.FromResult(Bytes);
    }
}
