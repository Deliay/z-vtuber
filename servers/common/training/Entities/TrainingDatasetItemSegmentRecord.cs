using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace Z.Vtuber.Common.Persistence.Entities;

/// <summary>
/// 训练数据集条目的候选短句（拼音反向索引载体）：条目 <c>Text</c> 按断句规则拆分后的每条短句，
/// 连同无声调拼音音节序列与所属条目标识落库，供 <c>synthesize</c> 按数据集加载后在内存计算拼音相似度。
/// 派生数据：<c>training_dataset_items</c> 为事实源，可由 <c>segments/rebuild</c> 随时重建。
/// </summary>
public sealed class TrainingDatasetItemSegmentRecord
{
    [BsonId]
    public ObjectId Id { get; set; } = ObjectId.GenerateNewId();

    /// <summary>所属数据集。</summary>
    public ObjectId DatasetId { get; set; }

    /// <summary>所属数据 id（命中去重单位；回查条目取音频/情绪）。</summary>
    public ObjectId ItemId { get; set; }

    /// <summary>冗余条目 <c>AudioKey</c>（命中头/日志直读，免回查）。</summary>
    public string AudioKey { get; set; } = string.Empty;

    /// <summary>短句在条目文本中的序号（确定性排序用）。</summary>
    public int SegmentIndex { get; set; }

    /// <summary>候选短句原文（Trim 后非空）。</summary>
    public string Text { get; set; } = string.Empty;

    /// <summary>归一化拼音（逐字无声调小写、单空格分隔音节；加载后 split 即得音节序列）。</summary>
    public string Pinyin { get; set; } = string.Empty;

    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime CreatedAt { get; set; }
}
