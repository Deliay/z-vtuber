using System.Text.Json;
using System.Text.Json.Serialization;

namespace Z.Vtuber.Training.Contracts;

public sealed record ErrorResponse([property: JsonPropertyName("error")] string Error);

public sealed record TrainingDatasetDto(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("createdAt")] DateTime CreatedAt,
    [property: JsonPropertyName("updatedAt")] DateTime UpdatedAt);

public sealed record TrainingDatasetListDto([property: JsonPropertyName("items")] IReadOnlyList<TrainingDatasetDto> Items);

public sealed record TrainingDatasetCreateRequest(
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("type")] string? Type);

public sealed record TrainingDatasetRenameRequest([property: JsonPropertyName("name")] string? Name);

public sealed record TrainingDatasetItemDto(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("datasetId")] string DatasetId,
    [property: JsonPropertyName("audioType")] string AudioType,
    [property: JsonPropertyName("source")] string Source,
    [property: JsonPropertyName("audioUri")] string AudioUri,
    [property: JsonPropertyName("audioKey")] string AudioKey,
    [property: JsonPropertyName("startMs")] long StartMs,
    [property: JsonPropertyName("endMs")] long? EndMs,
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("extensions")] JsonElement? Extensions,
    [property: JsonPropertyName("createdAt")] DateTime CreatedAt,
    [property: JsonPropertyName("updatedAt")] DateTime UpdatedAt);

public sealed record TrainingDatasetItemListDto([property: JsonPropertyName("items")] IReadOnlyList<TrainingDatasetItemDto> Items);

public sealed record TrainingDatasetImportRequest([property: JsonPropertyName("taskId")] string? TaskId);

public sealed record TrainingDatasetImportResultDto(
    [property: JsonPropertyName("imported")] long Imported,
    [property: JsonPropertyName("skipped")] long Skipped);

public sealed record TrainingDatasetBatchDeleteRequest([property: JsonPropertyName("itemIds")] IReadOnlyList<string>? ItemIds);

public sealed record TrainingDatasetBatchDeleteResultDto([property: JsonPropertyName("deleted")] long Deleted);

/// <summary>
/// 条目更新请求：用非可空 <see cref="JsonElement"/> 区分「字段缺失」与「显式 null」——
/// <c>ValueKind == JsonValueKind.Undefined</c> 表示字段缺失（不变）；
/// <c>audioUri/text/startMs</c> 显式 null → 400，<c>endMs</c> 显式 null → 置空。
/// </summary>
public sealed class TrainingDatasetItemUpdateRequest
{
    [JsonPropertyName("audioUri")]
    public JsonElement AudioUri { get; set; }

    [JsonPropertyName("text")]
    public JsonElement Text { get; set; }

    [JsonPropertyName("startMs")]
    public JsonElement StartMs { get; set; }

    [JsonPropertyName("endMs")]
    public JsonElement EndMs { get; set; }
}
