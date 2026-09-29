using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace Z.Vtuber.Common.Persistence.Entities;

/// <summary>
/// 模型（<c>training_models</c>）：所属 Provider + 实际发给服务商的模型 ID。
/// 唯一索引 <c>(ProviderId, Model)</c>；<c>slug</c>（<c>{providerName}/{model}</c>）为派生字段不落库。
/// </summary>
[BsonIgnoreExtraElements]
public sealed class TrainingModelRecord
{
    [BsonId]
    public ObjectId Id { get; set; } = ObjectId.GenerateNewId();

    public ObjectId ProviderId { get; set; }

    /// <summary>实际发给服务商的模型 ID（trim 后非空 ≤ 128）。</summary>
    public string Model { get; set; } = string.Empty;

    /// <summary>是否启用（默认 true）；识别类端点仅允许启用模型。</summary>
    public bool Enabled { get; set; } = true;

    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime CreatedAt { get; set; }

    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime UpdatedAt { get; set; }
}
