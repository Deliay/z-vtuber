using System.Text.Json;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using Z.Vtuber.Training.Configuration;

namespace Z.Vtuber.Training.Vectors;

/// <summary>
/// <see cref="IVectorStore"/> 的 Milvus 实现（REST API v2）：两集合
/// <c>item_text_embeddings</c>（dim=<c>Embedding:Dimensions</c>，COSINE）/<c>item_emotion_vectors</c>（dim=8，L2），
/// 字段 <c>id</c>(itemId，主键 VarChar)/<c>datasetId</c>(过滤)/<c>audioKey</c>/<c>vector</c>，FLAT 精确索引。
/// 检索取 top-k 后在内存按（相似度降序，itemId 升序）稳定排序。任何 Milvus 业务/传输错误抛
/// <see cref="VectorStoreUnavailableException"/>（映射 502）。
/// </summary>
public sealed class MilvusVectorStore(
    IMilvusRestClient client,
    IOptions<MilvusOptions> milvusOptions,
    IOptions<EmbeddingOptions> embeddingOptions,
    ILogger<MilvusVectorStore> logger) : IVectorStore
{
    private const int EmotionDimensions = 8;
    private readonly MilvusOptions _options = milvusOptions.Value;

    public async Task EnsureCollectionsAsync(CancellationToken ct = default)
    {
        await EnsureCollectionAsync(
            _options.TextCollection, embeddingOptions.Value.Dimensions, "COSINE", ct);
        await EnsureCollectionAsync(
            _options.EmotionCollection, EmotionDimensions, "L2", ct);
    }

    private async Task EnsureCollectionAsync(string collection, int dimension, string metric, CancellationToken ct)
    {
        var describe = await client.PostAsync(
            "/v2/vectordb/collections/describe",
            new { dbName = _options.Database, collectionName = collection },
            ct);
        if (!describe.IsSuccess)
        {
            var create = await client.PostAsync(
                "/v2/vectordb/collections/create",
                BuildCreatePayload(collection, dimension, metric),
                ct);
            if (!create.IsSuccess)
            {
                throw new VectorStoreUnavailableException(
                    $"milvus create collection '{collection}' failed: {create.Message}");
            }

            logger.LogInformation(
                "Milvus collection created: {Collection} dim={Dimension} metric={Metric}",
                collection, dimension, metric);
        }

        var load = await client.PostAsync(
            "/v2/vectordb/collections/load",
            new { dbName = _options.Database, collectionName = collection },
            ct);
        if (!load.IsSuccess)
        {
            throw new VectorStoreUnavailableException(
                $"milvus load collection '{collection}' failed: {load.Message}");
        }
    }

    private object BuildCreatePayload(string collection, int dimension, string metric) => new
    {
        dbName = _options.Database,
        collectionName = collection,
        schema = new
        {
            autoId = false,
            enableDynamicField = false,
            fields = new object[]
            {
                new
                {
                    fieldName = "id",
                    dataType = "VarChar",
                    isPrimary = true,
                    elementTypeParams = new Dictionary<string, string> { ["max_length"] = "64" },
                },
                new
                {
                    fieldName = "datasetId",
                    dataType = "VarChar",
                    elementTypeParams = new Dictionary<string, string> { ["max_length"] = "64" },
                },
                new
                {
                    fieldName = "audioKey",
                    dataType = "VarChar",
                    elementTypeParams = new Dictionary<string, string> { ["max_length"] = "512" },
                },
                new
                {
                    fieldName = "vector",
                    dataType = "FloatVector",
                    elementTypeParams = new Dictionary<string, string> { ["dim"] = dimension.ToString() },
                },
            },
        },
        indexParams = new object[]
        {
            new
            {
                fieldName = "vector",
                indexName = "vector_idx",
                metricType = metric,
                @params = new Dictionary<string, string> { ["index_type"] = "FLAT" },
            },
        },
    };

    public async Task<IReadOnlyList<VectorMatch>> SearchTextAsync(
        ObjectId datasetId, float[] query, int limit, CancellationToken ct = default) =>
        await SearchAsync(_options.TextCollection, datasetId, query, limit, ascending: false, ct);

    public async Task<IReadOnlyList<VectorMatch>> SearchEmotionAsync(
        ObjectId datasetId, float[] query, int limit, CancellationToken ct = default) =>
        await SearchAsync(_options.EmotionCollection, datasetId, query, limit, ascending: true, ct);

    private async Task<IReadOnlyList<VectorMatch>> SearchAsync(
        string collection, ObjectId datasetId, float[] query, int limit, bool ascending, CancellationToken ct)
    {
        var response = await client.PostAsync(
            "/v2/vectordb/entities/search",
            new
            {
                dbName = _options.Database,
                collectionName = collection,
                data = new[] { query },
                filter = $"datasetId == \"{datasetId}\"",
                limit = Math.Max(1, limit),
                outputFields = new[] { "audioKey" },
                searchParams = new Dictionary<string, object>
                {
                    ["metricType"] = ascending ? "L2" : "COSINE",
                    ["params"] = new Dictionary<string, object>(),
                },
            },
            ct);
        if (!response.IsSuccess)
        {
            throw new VectorStoreUnavailableException(
                $"milvus search '{collection}' failed: {response.Message}");
        }

        var matches = new List<VectorMatch>();
        if (response.Data is { ValueKind: JsonValueKind.Array } data)
        {
            foreach (var hit in data.EnumerateArray())
            {
                if (hit.ValueKind != JsonValueKind.Object
                    || !hit.TryGetProperty("id", out var idElement)
                    || idElement.ValueKind != JsonValueKind.String
                    || !ObjectId.TryParse(idElement.GetString(), out var itemId)
                    || !hit.TryGetProperty("distance", out var distanceElement)
                    || !distanceElement.TryGetDouble(out var distance))
                {
                    continue;
                }

                var audioKey = hit.TryGetProperty("audioKey", out var audioKeyElement)
                    && audioKeyElement.ValueKind == JsonValueKind.String
                    ? audioKeyElement.GetString() ?? string.Empty
                    : string.Empty;
                matches.Add(new VectorMatch(itemId, audioKey, (float)distance));
            }
        }

        // 稳定 tie-break：相似度（COSINE 降序 / L2 升序）后按 itemId 升序，杜绝同分并列不稳。
        return ascending
            ? matches.OrderBy(m => m.Score).ThenBy(m => m.ItemId).ToList()
            : matches.OrderByDescending(m => m.Score).ThenBy(m => m.ItemId).ToList();
    }

    public async Task UpsertItemVectorsAsync(
        ObjectId datasetId, ObjectId itemId, string audioKey,
        float[]? textEmbedding, float[]? emotionVector, CancellationToken ct = default)
    {
        if (textEmbedding is not null)
        {
            await UpsertAsync(_options.TextCollection, datasetId, itemId, audioKey, textEmbedding, ct);
        }

        if (emotionVector is not null)
        {
            await UpsertAsync(_options.EmotionCollection, datasetId, itemId, audioKey, emotionVector, ct);
        }
    }

    private async Task UpsertAsync(
        string collection, ObjectId datasetId, ObjectId itemId, string audioKey, float[] vector, CancellationToken ct)
    {
        var response = await client.PostAsync(
            "/v2/vectordb/entities/upsert",
            new
            {
                dbName = _options.Database,
                collectionName = collection,
                data = new[]
                {
                    new
                    {
                        id = itemId.ToString(),
                        datasetId = datasetId.ToString(),
                        audioKey,
                        vector,
                    },
                },
            },
            ct);
        if (!response.IsSuccess)
        {
            throw new VectorStoreUnavailableException(
                $"milvus upsert '{collection}' failed: {response.Message}");
        }
    }

    public async Task DeleteDatasetVectorsAsync(ObjectId datasetId, CancellationToken ct = default)
    {
        await DeleteAsync(_options.TextCollection, datasetId, ct);
        await DeleteAsync(_options.EmotionCollection, datasetId, ct);
    }

    private async Task DeleteAsync(string collection, ObjectId datasetId, CancellationToken ct)
    {
        var response = await client.PostAsync(
            "/v2/vectordb/entities/delete",
            new
            {
                dbName = _options.Database,
                collectionName = collection,
                filter = $"datasetId == \"{datasetId}\"",
            },
            ct);
        if (!response.IsSuccess)
        {
            throw new VectorStoreUnavailableException(
                $"milvus delete '{collection}' failed: {response.Message}");
        }
    }
}
