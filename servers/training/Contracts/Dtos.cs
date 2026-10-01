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
    [property: JsonPropertyName("emotion")] EmotionResult? Emotion,
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

public sealed record TrainingProviderDto(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("endpoint")] string Endpoint,
    [property: JsonPropertyName("apiKeySet")] bool ApiKeySet,
    [property: JsonPropertyName("createdAt")] DateTime CreatedAt,
    [property: JsonPropertyName("updatedAt")] DateTime UpdatedAt);

public sealed record TrainingProviderListDto(
    [property: JsonPropertyName("items")] IReadOnlyList<TrainingProviderDto> Items);

/// <summary>新建 Provider 请求；<c>apiKey</c> 写入后不回显。</summary>
public sealed record TrainingProviderCreateRequest(
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("type")] string? Type,
    [property: JsonPropertyName("endpoint")] string? Endpoint,
    [property: JsonPropertyName("apiKey")] string? ApiKey);

/// <summary>更新 Provider 请求：字段缺失不变；<c>apiKey</c> 传空串视为不改。</summary>
public sealed record TrainingProviderUpdateRequest(
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("type")] string? Type,
    [property: JsonPropertyName("endpoint")] string? Endpoint,
    [property: JsonPropertyName("apiKey")] string? ApiKey);

public sealed record TrainingModelDto(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("providerId")] string ProviderId,
    [property: JsonPropertyName("providerName")] string ProviderName,
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("slug")] string Slug,
    [property: JsonPropertyName("enabled")] bool Enabled,
    [property: JsonPropertyName("createdAt")] DateTime CreatedAt,
    [property: JsonPropertyName("updatedAt")] DateTime UpdatedAt);

public sealed record TrainingModelListDto(
    [property: JsonPropertyName("items")] IReadOnlyList<TrainingModelDto> Items);

public sealed record TrainingModelCreateRequest(
    [property: JsonPropertyName("providerId")] string? ProviderId,
    [property: JsonPropertyName("model")] string? Model);

public sealed record TrainingModelUpdateRequest(
    [property: JsonPropertyName("model")] string? Model,
    [property: JsonPropertyName("enabled")] bool? Enabled);

public sealed record TrainingDiscoveredModelDto(
    [property: JsonPropertyName("model")] string Model);

public sealed record TrainingModelDiscoverResultDto(
    [property: JsonPropertyName("models")] IReadOnlyList<TrainingDiscoveredModelDto> Models);

public sealed record TrainingModelImportRequest(
    [property: JsonPropertyName("models")] IReadOnlyList<string>? Models);

public sealed record TrainingModelImportResultDto(
    [property: JsonPropertyName("imported")] long Imported,
    [property: JsonPropertyName("skipped")] long Skipped);

public sealed record TrainingRecognizeRequest(
    [property: JsonPropertyName("modelId")] string? ModelId);

public sealed record TrainingRecognizeResultDto(
    [property: JsonPropertyName("text")] string Text);

/// <summary>音频生成请求：<c>text</c> 必填、<c>lang</c>/<c>modelId</c> 可选。</summary>
public sealed record TrainingSynthesizeRequest(
    [property: JsonPropertyName("text")] string? Text,
    [property: JsonPropertyName("lang")] string? Lang,
    [property: JsonPropertyName("modelId")] string? ModelId);

/// <summary>向量索引重建结果：重建（写入 Milvus）的条目数。</summary>
public sealed record TrainingVectorsRebuildResultDto(
    [property: JsonPropertyName("updated")] long Updated);

/// <summary>候选短句反向索引重建结果：重建的候选短句总数。</summary>
public sealed record TrainingSegmentsRebuildResultDto(
    [property: JsonPropertyName("rebuilt")] long Rebuilt);/// <summary>结构化情绪数据（十字段固定结构）。</summary>
public sealed record EmotionResult(
    [property: JsonPropertyName("emotionControlWeight")] double EmotionControlWeight,
    [property: JsonPropertyName("emotionDescription")] string EmotionDescription,
    [property: JsonPropertyName("happy")] double Happy,
    [property: JsonPropertyName("angry")] double Angry,
    [property: JsonPropertyName("sad")] double Sad,
    [property: JsonPropertyName("afraid")] double Afraid,
    [property: JsonPropertyName("disgusted")] double Disgusted,
    [property: JsonPropertyName("melancholic")] double Melancholic,
    [property: JsonPropertyName("surprised")] double Surprised,
    [property: JsonPropertyName("calm")] double Calm);

public sealed record TrainingEmotionTaskCreateRequest(
    [property: JsonPropertyName("itemIds")] IReadOnlyList<string>? ItemIds,
    [property: JsonPropertyName("modelId")] string? ModelId);

public sealed record TrainingEmotionTaskCreateResultDto(
    [property: JsonPropertyName("created")] long Created,
    [property: JsonPropertyName("skipped")] long Skipped);

public sealed record TrainingEmotionTaskDto(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("datasetId")] string DatasetId,
    [property: JsonPropertyName("itemId")] string ItemId,
    [property: JsonPropertyName("audioKey")] string AudioKey,
    [property: JsonPropertyName("modelId")] string ModelId,
    [property: JsonPropertyName("modelSlug")] string ModelSlug,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("attempts")] int Attempts,
    [property: JsonPropertyName("error")] string? Error,
    [property: JsonPropertyName("emotion")] EmotionResult? Emotion,
    [property: JsonPropertyName("createdAt")] DateTime CreatedAt,
    [property: JsonPropertyName("startedAt")] DateTime? StartedAt,
    [property: JsonPropertyName("finishedAt")] DateTime? FinishedAt);

public sealed record TrainingEmotionTaskCountsDto(
    [property: JsonPropertyName("pending")] long Pending,
    [property: JsonPropertyName("running")] long Running,
    [property: JsonPropertyName("done")] long Done,
    [property: JsonPropertyName("failed")] long Failed);

public sealed record TrainingEmotionTaskListDto(
    [property: JsonPropertyName("items")] IReadOnlyList<TrainingEmotionTaskDto> Items,
    [property: JsonPropertyName("counts")] TrainingEmotionTaskCountsDto Counts);
