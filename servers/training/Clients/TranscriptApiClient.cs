using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace Z.Vtuber.Training.Clients;

/// <summary>transcript 任务详情（training 自有 DTO，字段与 contracts/recording-transcript.openapi.yaml 对齐）。</summary>
public sealed record TranscriptTaskBrief(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("roomId")] long RoomId,
    [property: JsonPropertyName("file")] string? File,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("offsetMs")] long OffsetMs);

/// <summary>transcript 片段（start/end 为任务详情返回口径，即已叠加任务播放偏移）。</summary>
public sealed record TranscriptSegmentBrief(
    [property: JsonPropertyName("start")] long Start,
    [property: JsonPropertyName("end")] long End,
    [property: JsonPropertyName("text")] string Text);

public sealed record TranscriptTaskDetail(
    [property: JsonPropertyName("task")] TranscriptTaskBrief Task,
    [property: JsonPropertyName("segments")] IReadOnlyList<TranscriptSegmentBrief> Segments);

/// <summary>recording-transcript-api 任务详情客户端。</summary>
public interface ITranscriptApiClient
{
    /// <summary>任务不存在返回 null；上游不可达/超时抛 HttpRequestException 或 TaskCanceledException。</summary>
    Task<TranscriptTaskDetail?> GetTaskDetailAsync(string taskId, CancellationToken ct = default);
}

/// <summary>
/// transcript 任务详情客户端：<c>GET api/transcripts/task/{taskId}</c>——
/// 404 → null，其余非 2xx 抛 <see cref="HttpRequestException"/>（由控制器映射 502）。
/// </summary>
public sealed class TranscriptApiClient(HttpClient httpClient) : ITranscriptApiClient
{
    /// <summary>named client 名称（DI 注册键，与配置节 TranscriptService 同口径）。</summary>
    internal const string ClientName = "TranscriptService";

    private static readonly System.Text.Json.JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public async Task<TranscriptTaskDetail?> GetTaskDetailAsync(string taskId, CancellationToken ct = default)
    {
        using var response = await httpClient.GetAsync(
            $"api/transcripts/task/{Uri.EscapeDataString(taskId)}", ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<TranscriptTaskDetail>(SerializerOptions, ct);
    }
}
