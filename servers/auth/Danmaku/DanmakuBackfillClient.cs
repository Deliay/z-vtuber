using System.Text.Json;
using Z.Vtuber.Auth.Configuration;

namespace Z.Vtuber.Auth.Danmaku;

public sealed record DanmakuBackfillResult(IReadOnlyList<DanmakuEvent> Events, bool Truncated);

/// <summary>
/// 断线补投：分页读取既有 <c>event/range</c>，按 occurredAt 升序返回；失败不阻塞实时流。
/// </summary>
public sealed class DanmakuBackfillClient(
    HttpClient httpClient,
    IOptions<DanmakuOptions> options,
    ILogger<DanmakuBackfillClient> logger)
{
    private const int PageSize = 100;

    public async Task<DanmakuBackfillResult> FetchAsync(
        string roomId, DateTime from, DateTime until, CancellationToken ct)
    {
        var opts = options.Value;
        var collected = new List<DanmakuEvent>();
        var truncated = false;

        for (var page = 1; page <= opts.MaxBackfillPages; page++)
        {
            var path = opts.BuildRangePath(roomId);
            var query =
                $"type=danmaku&startedAt={Uri.EscapeDataString(from.ToString("O"))}" +
                $"&endedAt={Uri.EscapeDataString(until.ToString("O"))}" +
                $"&page={page}&pageSize={PageSize}";

            using var response = await httpClient.GetAsync($"{path}?{query}", HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning(
                    "Danmaku backfill failed for room {RoomId} page {Page}: HTTP {Status}.",
                    roomId, page, (int)response.StatusCode);
                break;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            JsonDocument document;
            try
            {
                document = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            }
            catch (JsonException ex)
            {
                logger.LogWarning(ex, "Danmaku backfill returned malformed JSON for room {RoomId}.", roomId);
                break;
            }

            using var _ = document;
            if (!document.RootElement.TryGetProperty("items", out var items) ||
                items.ValueKind != JsonValueKind.Array)
            {
                break;
            }

            var count = 0;
            foreach (var item in items.EnumerateArray())
            {
                count++;
                if (DanmakuPayloadReader.TryRead(item, out var evt) && evt is not null &&
                    string.Equals(evt.RoomId, roomId, StringComparison.Ordinal))
                {
                    collected.Add(evt);
                }
            }

            if (count < PageSize)
            {
                break;
            }

            if (page == opts.MaxBackfillPages)
            {
                truncated = true;
            }
        }

        var ordered = collected.OrderBy(x => x.OccurredAt).ToList();
        if (truncated)
        {
            logger.LogWarning(
                "Danmaku backfill for room {RoomId} truncated at {Pages} pages ({Count} events).",
                roomId, opts.MaxBackfillPages, ordered.Count);
        }

        return new DanmakuBackfillResult(ordered, truncated);
    }
}
