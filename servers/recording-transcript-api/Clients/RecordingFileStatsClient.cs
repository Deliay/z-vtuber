using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace Z.Vtuber.RecordingTranscript.Api.Clients;

/// <summary>单个录播文件的磁盘状态。</summary>
public sealed record RecordingFileStat(bool Exists, long? Size);

/// <summary>一次批量查询中的单个房间及其文件名列表。</summary>
public sealed record RecordingFileStatsQuery(long RoomId, IReadOnlyList<string> Files);

/// <summary>
/// recording 服务磁盘权威查询客户端：单次请求聚合整页 (roomId, files)，
/// 传输/协议异常抛出由调用方降级处理。
/// </summary>
public interface IRecordingFileStatsClient
{
    Task<IReadOnlyDictionary<(long RoomId, string FileName), RecordingFileStat>> GetFileStatsAsync(
        IReadOnlyList<RecordingFileStatsQuery> queries, CancellationToken ct = default);
}

public sealed class RecordingFileStatsClient(
    HttpClient httpClient,
    ILogger<RecordingFileStatsClient> logger) : IRecordingFileStatsClient
{
    private static readonly System.Text.Json.JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public async Task<IReadOnlyDictionary<(long RoomId, string FileName), RecordingFileStat>> GetFileStatsAsync(
        IReadOnlyList<RecordingFileStatsQuery> queries, CancellationToken ct = default)
    {
        var result = new Dictionary<(long RoomId, string FileName), RecordingFileStat>();
        if (queries.Count == 0)
        {
            return result;
        }

        var request = new FileStatsRequest(
            queries.Select(q => new FileStatsRoomRequest(q.RoomId, q.Files)).ToList());

        using var response = await httpClient.PostAsJsonAsync(
            "api/recordings/file-stats", request, SerializerOptions, ct);
        response.EnsureSuccessStatusCode();

        var payload = await response.Content.ReadFromJsonAsync<FileStatsResponse>(SerializerOptions, ct);
        if (payload?.Items is null)
        {
            return result;
        }

        foreach (var room in payload.Items)
        {
            foreach (var file in room.Files ?? [])
            {
                if (file.Name is null)
                {
                    continue;
                }

                result[(room.RoomId, file.Name)] = new RecordingFileStat(file.Exists, file.Size);
            }
        }

        logger.LogDebug("Fetched disk stats for {FileCount} files across {RoomCount} rooms",
            result.Count, queries.Count);
        return result;
    }

    private sealed record FileStatsRequest(
        [property: JsonPropertyName("items")] IReadOnlyList<FileStatsRoomRequest> Items);

    private sealed record FileStatsRoomRequest(
        [property: JsonPropertyName("roomId")] long RoomId,
        [property: JsonPropertyName("files")] IReadOnlyList<string> Files);

    private sealed record FileStatsResponse(
        [property: JsonPropertyName("items")] IReadOnlyList<FileStatsRoom>? Items);

    private sealed record FileStatsRoom(
        [property: JsonPropertyName("roomId")] long RoomId,
        [property: JsonPropertyName("files")] IReadOnlyList<FileStat>? Files);

    private sealed record FileStat(
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("exists")] bool Exists,
        [property: JsonPropertyName("size")] long? Size);
}
