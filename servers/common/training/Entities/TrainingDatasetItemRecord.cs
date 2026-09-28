using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace Z.Vtuber.Common.Persistence.Entities;

public sealed class TrainingDatasetItemRecord
{
    [BsonId]
    public ObjectId Id { get; set; } = ObjectId.GenerateNewId();

    public ObjectId DatasetId { get; set; }

    /// <summary>音频类型，本期 <c>transcript</c>（开放枚举）。</summary>
    public string AudioType { get; set; } = string.Empty;

    /// <summary>音频来源；transcript 类型 = 识别任务 id。</summary>
    public string Source { get; set; } = string.Empty;

    /// <summary>音频地址 <c>b/live/{roomId}/recordings/{file}</c>，可为空（任务无文件）。</summary>
    public string AudioUri { get; set; } = string.Empty;

    /// <summary>音频标识，数据集内唯一（唯一索引 <c>DatasetId_1_AudioKey_1</c>）。</summary>
    public string AudioKey { get; set; } = string.Empty;

    [BsonRepresentation(BsonType.Int64)]
    public long StartMs { get; set; }

    /// <summary>结束时间（毫秒），<c>null</c> = 到音频末尾。</summary>
    [BsonRepresentation(BsonType.Int64)]
    public long? EndMs { get; set; }

    public string Text { get; set; } = string.Empty;

    /// <summary>扩展字段（预留）：情感识别、二次 ASR 等加工结果按键名挂接。</summary>
    public BsonDocument? Extensions { get; set; }

    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime CreatedAt { get; set; }

    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime UpdatedAt { get; set; }
}
