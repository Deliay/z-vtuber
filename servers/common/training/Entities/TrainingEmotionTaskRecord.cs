using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace Z.Vtuber.Common.Persistence.Entities;

/// <summary>情绪识别任务状态（存储口径大写；API 出口小写）。</summary>
public static class TrainingEmotionTaskStatus
{
    public const string Pending = "PENDING";
    public const string Running = "RUNNING";
    public const string Done = "DONE";
    public const string Failed = "FAILED";
}

/// <summary>
/// 情绪识别任务（<c>training_emotion_tasks</c>）：多副本由扫描器「周期扫描 + Redis 抢锁」消费，
/// 完成后把十字段情绪结果写入条目 <c>Extensions.emotion</c> 并留 <see cref="Emotion"/> 快照。
/// </summary>
[BsonIgnoreExtraElements]
public sealed class TrainingEmotionTaskRecord
{
    [BsonId]
    public ObjectId Id { get; set; } = ObjectId.GenerateNewId();

    public ObjectId DatasetId { get; set; }

    public ObjectId ItemId { get; set; }

    /// <summary>条目音频标识快照（任务可读性）。</summary>
    public string AudioKey { get; set; } = string.Empty;

    public ObjectId ModelId { get; set; }

    /// <summary>slug 快照（<c>{providerName}/{model}</c>，模型删除后任务仍可读）。</summary>
    public string ModelSlug { get; set; } = string.Empty;

    public string Status { get; set; } = TrainingEmotionTaskStatus.Pending;

    /// <summary>执行次数（重试累加）。</summary>
    public int Attempts { get; set; }

    /// <summary>失败原因（可读）。</summary>
    public string? Error { get; set; }

    /// <summary>识别结果快照（DONE 时写入，结构同 EmotionResult）。</summary>
    public BsonDocument? Emotion { get; set; }

    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime CreatedAt { get; set; }

    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime? StartedAt { get; set; }

    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime? FinishedAt { get; set; }
}
