using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace Z.Vtuber.Common.Persistence.Entities;

/// <summary>Provider 类型常量（开放枚举）。</summary>
public static class TrainingProviderTypes
{
    public const string OpenAi = "openai";
    public const string Claude = "claude";
}

/// <summary>
/// 模型 Provider（<c>training_providers</c>）：一个 Provider 下可挂多个 Model。
/// 唯一索引 <c>Name</c>——重名插入/改名由数据库抛 DuplicateKey，API 层映射 409。
/// </summary>
[BsonIgnoreExtraElements]
public sealed class TrainingProviderRecord
{
    [BsonId]
    public ObjectId Id { get; set; } = ObjectId.GenerateNewId();

    /// <summary>名称（trim 后非空 ≤ 64，全局唯一）。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>模型类型：<c>openai</c> | <c>claude</c>（见 <see cref="TrainingProviderTypes"/>）。</summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>服务地址（http/https，去尾部 <c>/</c>）。</summary>
    public string Endpoint { get; set; } = string.Empty;

    /// <summary>明文入库（与 <c>transcript_models.ApiKey</c> 同口径），任何接口不回传。</summary>
    public string ApiKey { get; set; } = string.Empty;

    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime CreatedAt { get; set; }

    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime UpdatedAt { get; set; }
}
