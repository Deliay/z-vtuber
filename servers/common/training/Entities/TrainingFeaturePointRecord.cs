using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace Z.Vtuber.Common.Persistence.Entities;

/// <summary>
/// 功能点（<c>training_feature_points</c>）：功能点自行声明 slug 与能力要求，请求模型时按 slug upsert 自动登记。
/// 唯一索引 <c>Slug</c>；<see cref="ModelId"/> 为「指定模型」，null = 未设置（默认选择口径）。
/// 指定不做外键级联：模型被删除后 <see cref="ModelId"/> 保留，读取时解析为「指定模型不可用」。
/// </summary>
[BsonIgnoreExtraElements]
public sealed class TrainingFeaturePointRecord
{
    [BsonId]
    public ObjectId Id { get; set; } = ObjectId.GenerateNewId();

    /// <summary>功能点标识（功能点自行声明，trim 后非空 ≤ 128；唯一）。</summary>
    public string Slug { get; set; } = string.Empty;

    /// <summary>功能点名称（请求未传则回落 slug）。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>功能点所需输入能力（<c>audio</c>/<c>video</c>/<c>text</c>/<c>image</c> 子集）。</summary>
    public string[] InputCapabilities { get; set; } = [];

    /// <summary>功能点所需输出能力。</summary>
    public string[] OutputCapabilities { get; set; } = [];

    /// <summary>指定模型；null = 未设置（按默认选择返回可用模型中 CreatedAt 最早者）。</summary>
    public ObjectId? ModelId { get; set; }

    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime CreatedAt { get; set; }

    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime UpdatedAt { get; set; }
}
