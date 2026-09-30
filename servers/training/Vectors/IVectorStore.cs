using MongoDB.Bson;

namespace Z.Vtuber.Training.Vectors;

/// <summary>向量检索命中：条目 id、audioKey 与相似度分数（COSINE 越大越相似；L2 越小越相似）。</summary>
public sealed record VectorMatch(ObjectId ItemId, string AudioKey, float Score);

/// <summary>
/// Milvus 向量仓储/检索（派生索引，可由 MongoDB 条目经 <see cref="VectorSyncService"/> 重建）：
/// 两个集合 <c>item_text_embeddings</c>（dim=<c>Embedding:Dimensions</c>，COSINE）与
/// <c>item_emotion_vectors</c>（dim=8，L2）；FLAT 精确检索 + 稳定 tie-break（同分按 itemId 升序）。
/// 失败抛 <see cref="VectorStoreUnavailableException"/>（映射 502 <c>vector store unavailable</c>）。
/// </summary>
public interface IVectorStore
{
    /// <summary>幂等建立两个集合与 FLAT 索引（启动/首次使用调用）。</summary>
    Task EnsureCollectionsAsync(CancellationToken ct = default);

    /// <summary>按 <paramref name="datasetId"/> 过滤，文本集合 COSINE 检索 top-<paramref name="limit"/>（按相似度降序、同分 itemId 升序）。</summary>
    Task<IReadOnlyList<VectorMatch>> SearchTextAsync(
        ObjectId datasetId, float[] query, int limit, CancellationToken ct = default);

    /// <summary>按 <paramref name="datasetId"/> 过滤，情感集合 L2 检索 top-<paramref name="limit"/>（按距离升序、同分 itemId 升序）。</summary>
    Task<IReadOnlyList<VectorMatch>> SearchEmotionAsync(
        ObjectId datasetId, float[] query, int limit, CancellationToken ct = default);

    /// <summary>
    /// 按 <c>itemId</c> upsert 一条条目的向量：文本向量入文本集合、八维情感向量入情感集合；
    /// <paramref name="textEmbedding"/>/<paramref name="emotionVector"/> 为 null 的集合**删除该条目对应行**
    /// （条目 text/情绪被清空后不得残留旧向量，否则会污染 top-k 候选）。
    /// </summary>
    Task UpsertItemVectorsAsync(
        ObjectId datasetId, ObjectId itemId, string audioKey,
        float[]? textEmbedding, float[]? emotionVector, CancellationToken ct = default);

    /// <summary>
    /// 删除<b>指定数据集内</b>该条目在两个集合中的向量（条目删除后清理派生索引）。
    /// 删除条件按 <c>datasetId</c> + <c>itemId</c> 双重限定：<paramref name="itemId"/> 可能来自请求体，
    /// 仅按 id 删会跨数据集误删他数据集同 id 的向量。
    /// </summary>
    Task DeleteItemVectorsAsync(ObjectId datasetId, ObjectId itemId, CancellationToken ct = default);

    /// <summary>删除某数据集的全部向量（级联删除数据集/重建前清理）。</summary>
    Task DeleteDatasetVectorsAsync(ObjectId datasetId, CancellationToken ct = default);
}
