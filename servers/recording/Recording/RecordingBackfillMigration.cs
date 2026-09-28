using MongoDB.Driver;
using Z.Vtuber.Common.Persistence;
using Z.Vtuber.Common.Persistence.Entities;
using Z.Vtuber.Recording.Configuration;

namespace Z.Vtuber.Recording.Recording;

/// <summary>
/// 存量录播一次性幂等清洗（随 recording 服务启动执行）：
/// 遍历 <c>{StorageDir}/{roomId}/*.flv</c>，以文件创建时间匹配直播场次
/// （<c>bilibili_live_session</c> 窗口，无匹配回退 <c>bilibili_live_status</c>），
/// 同场次多文件（RAW/AV1）聚合为一条 <c>recordings</c>；RoomId+StartedAt 为幂等键跳过已存在，
/// 不匹配文件记 warning 不阻断；marker <c>recording_backfill_v1</c> 保证重复启动不重复写入。
/// </summary>
public sealed class RecordingBackfillMigration(
    IMongoDatabase database,
    RecordingOptions options,
    ILogger<RecordingBackfillMigration> logger)
{
    public const string MigrationKey = "recording_backfill_v1";

    private const int DatePrefixLength = 19; // yyyy-MM-dd-hh-mm-ss

    private readonly RecordingRepository _recordings = new(database);
    private readonly LiveSessionRepository _sessions = new(database);
    private readonly LiveStatusRepository _statuses = new(database);
    private readonly AccountBasicInfoRepository _accounts = new(database);
    private readonly IMongoCollection<MetaRecord> _meta =
        database.GetCollection<MetaRecord>(CoreCollectionNames.Meta);

    public async Task RunAsync(CancellationToken ct = default)
    {
        if (await _meta.Find(m => m.Key == MigrationKey).AnyAsync(ct))
        {
            logger.LogInformation("Recording backfill migration already applied; skipping.");
            return;
        }

        if (!Directory.Exists(options.StorageDir))
        {
            logger.LogInformation(
                "Recording storage directory {StorageDir} does not exist; nothing to backfill.",
                options.StorageDir);
            await MarkAppliedAsync(ct);
            return;
        }

        var matchedFiles = 0;
        var inserted = 0;
        var skipped = 0;
        var unmatched = 0;

        foreach (var roomDir in Directory.EnumerateDirectories(options.StorageDir))
        {
            var roomName = Path.GetFileName(roomDir);
            if (!long.TryParse(roomName, out var roomId) || roomId <= 0)
            {
                continue;
            }

            var groups = new Dictionary<string, BackfillGroup>(StringComparer.Ordinal);

            foreach (var path in Directory.EnumerateFiles(roomDir, "*.flv"))
            {
                var fileName = Path.GetFileName(path);
                var createdAt = GetFileCreatedAtUtc(path);

                var session = await _sessions.GetByRoomIdAndTimeWindowAsync(roomId, createdAt, ct);
                var status = session is null
                    ? await FindStatusNearAsync(roomId, createdAt, ct)
                    : null;

                if (session is null && status is null)
                {
                    unmatched++;
                    logger.LogWarning(
                        "Recording backfill: no live session/status matched {FileName} (room {RoomId}, created {CreatedAt:o}); skipping.",
                        fileName,
                        roomId,
                        createdAt);
                    continue;
                }

                matchedFiles++;
                TryParseSessionName(fileName, out _, out var parsedTitle);

                var groupKey = session?.LiveId ?? $"status:{status!.Id}";
                var startedAt = session?.StartDate ?? status!.CreatedAt;
                var title = string.IsNullOrWhiteSpace(parsedTitle)
                    ? session?.Title ?? status!.Title
                    : parsedTitle!;

                if (!groups.TryGetValue(groupKey, out var group))
                {
                    // 补 roomId→mid 映射（bilibili_account_basic_info），取不到时按 0 兜底
                    var mid = await _accounts.GetLatestMidByRoomIdAsync(roomId, ct) ?? 0;
                    group = new BackfillGroup(roomId, mid, startedAt, title, session?.StopDate);
                    groups[groupKey] = group;
                }

                if (!group.Files.Contains(fileName, StringComparer.Ordinal))
                {
                    group.Files.Add(fileName);
                }
            }

            foreach (var group in groups.Values)
            {
                if (await _recordings.GetByRoomIdStartedAtAsync(group.RoomId, group.StartedAt, ct) is not null)
                {
                    skipped++;
                    continue;
                }

                try
                {
                    await _recordings.InsertAsync(new RecordingRecord
                    {
                        RoomId = group.RoomId,
                        Mid = group.Mid,
                        Title = group.Title,
                        Files = [.. group.Files],
                        StartedAt = group.StartedAt,
                        EndedAt = group.EndedAt,
                        CreatedAt = DateTime.UtcNow,
                    }, ct);
                    inserted++;
                }
                catch (MongoWriteException ex) when (ex.WriteError?.Category == ServerErrorCategory.DuplicateKey)
                {
                    // 并发实例同时清洗：唯一/幂等检查已由另一实例写入
                    skipped++;
                }
            }
        }

        logger.LogInformation(
            "Recording backfill migration completed: {Matched} files matched, {Inserted} recordings inserted, {Skipped} skipped, {Unmatched} unmatched.",
            matchedFiles,
            inserted,
            skipped,
            unmatched);

        await MarkAppliedAsync(ct);
    }

    private async Task<LiveStatusRecord?> FindStatusNearAsync(long roomId, DateTime t, CancellationToken ct)
    {
        var windowStart = t - TimeSpan.FromHours(12);
        var windowEnd = t + TimeSpan.FromHours(2);
        var (_, items) = await _statuses.GetPageAsync(roomId, 1, 100, windowStart, windowEnd, ct);
        var near = items.FirstOrDefault(s => s.LiveStatus) ?? items.FirstOrDefault();
        if (near is not null)
        {
            return near;
        }

        return await _statuses.GetLatestByRoomIdAsync(roomId, ct);
    }

    private static DateTime GetFileCreatedAtUtc(string path)
    {
        var created = File.GetCreationTimeUtc(path);
        if (created == default || created.Year < 2000)
        {
            created = File.GetLastWriteTimeUtc(path);
        }

        return created;
    }

    private async Task MarkAppliedAsync(CancellationToken ct)
    {
        await _meta.ReplaceOneAsync(
            Builders<MetaRecord>.Filter.Eq(m => m.Key, MigrationKey),
            new MetaRecord { Key = MigrationKey },
            new ReplaceOptions { IsUpsert = true },
            ct);
    }

    /// <summary>
    /// 解析文件名模板 <c>[encode][roomId][anchor]yyyy-MM-dd-hh-mm-ss-title.flv</c>：
    /// 提取场次名（anchor 与 title）；文件名时间为 12 小时制，仅解析场次名辅助，不用于时间匹配。
    /// </summary>
    internal static bool TryParseSessionName(string? fileName, out string? anchorName, out string? title)
    {
        anchorName = null;
        title = null;
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return false;
        }

        var name = fileName.EndsWith(".flv", StringComparison.OrdinalIgnoreCase)
            ? fileName[..^4]
            : fileName;

        if (name.Length == 0 || name[0] != '[')
        {
            return false;
        }

        var encodeEnd = name.IndexOf(']');
        if (encodeEnd < 0)
        {
            return false;
        }

        var roomStart = name.IndexOf('[', encodeEnd + 1);
        if (roomStart < 0)
        {
            return false;
        }

        var roomEnd = name.IndexOf(']', roomStart + 1);
        if (roomEnd < 0)
        {
            return false;
        }

        var anchorStart = name.IndexOf('[', roomEnd + 1);
        if (anchorStart < 0)
        {
            return false;
        }

        var anchorEnd = name.IndexOf(']', anchorStart + 1);
        if (anchorEnd < 0)
        {
            return false;
        }

        anchorName = NullIfWhiteSpace(name[(anchorStart + 1)..anchorEnd]);

        var remainder = name[(anchorEnd + 1)..];
        title = remainder.Length > DatePrefixLength && remainder[DatePrefixLength] == '-'
            ? NullIfWhiteSpace(remainder[(DatePrefixLength + 1)..])
            : NullIfWhiteSpace(remainder.TrimStart('-'));

        return true;
    }

    private static string? NullIfWhiteSpace(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

    private sealed class BackfillGroup(long roomId, long mid, DateTime startedAt, string title, DateTime? endedAt)
    {
        public long RoomId { get; } = roomId;
        public long Mid { get; } = mid;
        public DateTime StartedAt { get; } = startedAt;
        public string Title { get; } = title;
        public DateTime? EndedAt { get; } = endedAt;
        public List<string> Files { get; } = [];
    }
}
