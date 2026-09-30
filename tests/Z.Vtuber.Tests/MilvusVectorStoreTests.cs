using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using Z.Vtuber.Training.Configuration;
using Z.Vtuber.Training.Vectors;

namespace Z.Vtuber.Tests;

/// <summary>
/// Milvus 向量仓储契约（假 REST 客户端）：集合幂等建立（FLAT/COSINE/L2 与维度）、
/// 检索按 datasetId 过滤 + 稳定 tie-break（同分 itemId 升序）、upsert/delete 落两个集合、
/// 业务错误 → <see cref="VectorStoreUnavailableException"/>。
/// </summary>
public class MilvusVectorStoreTests
{
    private const int TextDim = 1024;

    private static MilvusVectorStore Create(StubMilvusRestClient client) =>
        new(
            client,
            Options.Create(new MilvusOptions()),
            Options.Create(new EmbeddingOptions { Dimensions = TextDim }),
            NullLogger<MilvusVectorStore>.Instance);

    private static string Hit(ObjectId id, double distance, string audioKey = "key") =>
        $$"""{"id":"{{id}}","distance":{{distance.ToString(System.Globalization.CultureInfo.InvariantCulture)}},"audioKey":"{{audioKey}}"}""";

    private static MilvusResponse Ok(string? dataJson = null) =>
        new(0, string.Empty, dataJson is null ? null : TrainingSynthesizeFakes.Json(dataJson));

    [Fact]
    public async Task EnsureCollections_CreatesBothWhenMissing_WithExpectedSchema()
    {
        var client = new StubMilvusRestClient
        {
            Responder = (path, _) => path switch
            {
                "/v2/vectordb/collections/describe" => new MilvusResponse(100, "collection not found", null),
                _ => Ok(),
            },
        };

        await Create(client).EnsureCollectionsAsync();

        Assert.Equal(2, client.CountOf("/v2/vectordb/collections/create"));
        Assert.Equal(2, client.CountOf("/v2/vectordb/collections/load"));

        var creates = client.Requests.Where(r => r.Path == "/v2/vectordb/collections/create").ToList();
        var text = creates.Single(r => r.Payload.GetProperty("collectionName").GetString() == "item_text_embeddings");
        var emotion = creates.Single(r => r.Payload.GetProperty("collectionName").GetString() == "item_emotion_vectors");

        var textVector = text.Payload.GetProperty("schema").GetProperty("fields").EnumerateArray()
            .Single(f => f.GetProperty("fieldName").GetString() == "vector");
        Assert.Equal(TextDim.ToString(), textVector.GetProperty("elementTypeParams").GetProperty("dim").GetString());
        Assert.Equal("COSINE", text.Payload.GetProperty("indexParams")[0].GetProperty("metricType").GetString());
        Assert.Equal("FLAT", text.Payload.GetProperty("indexParams")[0].GetProperty("params").GetProperty("index_type").GetString());

        var emotionVector = emotion.Payload.GetProperty("schema").GetProperty("fields").EnumerateArray()
            .Single(f => f.GetProperty("fieldName").GetString() == "vector");
        Assert.Equal("8", emotionVector.GetProperty("elementTypeParams").GetProperty("dim").GetString());
        Assert.Equal("L2", emotion.Payload.GetProperty("indexParams")[0].GetProperty("metricType").GetString());
    }

    [Fact]
    public async Task EnsureCollections_IdempotentWhenPresent()
    {
        var client = new StubMilvusRestClient { Responder = (_, _) => Ok() };

        await Create(client).EnsureCollectionsAsync();

        Assert.Equal(0, client.CountOf("/v2/vectordb/collections/create"));
        Assert.Equal(2, client.CountOf("/v2/vectordb/collections/load"));
    }

    [Fact]
    public async Task SearchText_FiltersByDataset_RanksAndTieBreaksByItemId()
    {
        var datasetId = ObjectId.GenerateNewId();
        var low = ObjectId.Parse("000000000000000000000001");
        var high = ObjectId.Parse("0000000000000000000000ff");
        var client = new StubMilvusRestClient
        {
            Responder = (path, _) => path == "/v2/vectordb/entities/search"
                ? Ok($"[{Hit(high, 0.5)}, {Hit(low, 0.9, "low")}, {Hit(high, 0.9, "high")}]")
                : Ok(),
        };

        var matches = await Create(client).SearchTextAsync(datasetId, [0.1f], 16);

        Assert.Equal(3, matches.Count);
        // 0.9 两条同分 → itemId 升序（low 在前）；随后是 0.5
        Assert.Equal(low, matches[0].ItemId);
        Assert.Equal(high, matches[1].ItemId);
        Assert.Equal(0.5f, matches[2].Score);

        var search = client.Requests.Single(r => r.Path == "/v2/vectordb/entities/search");
        Assert.Equal("item_text_embeddings", search.Payload.GetProperty("collectionName").GetString());
        Assert.Equal($"datasetId == \"{datasetId}\"", search.Payload.GetProperty("filter").GetString());
        Assert.Equal(16, search.Payload.GetProperty("limit").GetInt32());
        Assert.Equal(
            "COSINE",
            search.Payload.GetProperty("searchParams").GetProperty("metricType").GetString());
    }

    [Fact]
    public async Task SearchEmotion_UsesL2Ascending()
    {
        var datasetId = ObjectId.GenerateNewId();
        var near = ObjectId.GenerateNewId();
        var far = ObjectId.GenerateNewId();
        var client = new StubMilvusRestClient
        {
            Responder = (path, _) => path == "/v2/vectordb/entities/search"
                ? Ok($"[{Hit(far, 0.9)}, {Hit(near, 0.1)}]")
                : Ok(),
        };

        var matches = await Create(client).SearchEmotionAsync(datasetId, [0f], 16);

        Assert.Equal(near, matches[0].ItemId);
        var search = client.Requests.Single(r => r.Path == "/v2/vectordb/entities/search");
        Assert.Equal("item_emotion_vectors", search.Payload.GetProperty("collectionName").GetString());
        Assert.Equal("L2", search.Payload.GetProperty("searchParams").GetProperty("metricType").GetString());
    }

    [Fact]
    public async Task Search_BusinessError_ThrowsVectorStoreUnavailable()
    {
        var client = new StubMilvusRestClient
        {
            Responder = (path, _) => path == "/v2/vectordb/entities/search"
                ? new MilvusResponse(1100, "milvus down", null)
                : Ok(),
        };

        await Assert.ThrowsAsync<VectorStoreUnavailableException>(
            () => Create(client).SearchTextAsync(ObjectId.GenerateNewId(), [0.1f], 16));
    }

    [Fact]
    public async Task Upsert_WritesTextAndEmotionSeparately()
    {
        var client = new StubMilvusRestClient { Responder = (_, _) => Ok() };
        var datasetId = ObjectId.GenerateNewId();
        var itemId = ObjectId.GenerateNewId();

        await Create(client).UpsertItemVectorsAsync(datasetId, itemId, "key", [1f, 2f], [0f, 0f, 0.8f, 0f, 0f, 0f, 0f, 0f]);

        var upserts = client.Requests.Where(r => r.Path == "/v2/vectordb/entities/upsert").ToList();
        Assert.Equal(2, upserts.Count);
        Assert.Contains(upserts, r => r.Payload.GetProperty("collectionName").GetString() == "item_text_embeddings");
        Assert.Contains(upserts, r => r.Payload.GetProperty("collectionName").GetString() == "item_emotion_vectors");
    }

    [Fact]
    public async Task Upsert_OnlyText_WhenEmotionNull()
    {
        var client = new StubMilvusRestClient { Responder = (_, _) => Ok() };

        await Create(client).UpsertItemVectorsAsync(
            ObjectId.GenerateNewId(), ObjectId.GenerateNewId(), "key", [1f], null);

        Assert.Equal(1, client.CountOf("/v2/vectordb/entities/upsert"));
        Assert.Equal(
            "item_text_embeddings",
            client.Requests[0].Payload.GetProperty("collectionName").GetString());
    }

    [Fact]
    public async Task DeleteDataset_DeletesBothCollections()
    {
        var client = new StubMilvusRestClient { Responder = (_, _) => Ok() };
        var datasetId = ObjectId.GenerateNewId();

        await Create(client).DeleteDatasetVectorsAsync(datasetId);

        var deletes = client.Requests.Where(r => r.Path == "/v2/vectordb/entities/delete").ToList();
        Assert.Equal(2, deletes.Count);
        foreach (var delete in deletes)
        {
            Assert.Equal($"datasetId == \"{datasetId}\"", delete.Payload.GetProperty("filter").GetString());
        }
    }
}
