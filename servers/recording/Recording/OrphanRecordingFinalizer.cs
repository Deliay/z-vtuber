using MongoDB.Bson;
using Z.Vtuber.Common.Live;
using Z.Vtuber.Common.Persistence;
using Z.Vtuber.Common.Persistence.Entities;
using Z.Vtuber.Recording.Configuration;

namespace Z.Vtuber.Recording.Recording;

/// <summary>
/// 孤儿场次收尾：为已无活跃会话却仍 <c>EndedAt=null</c> 的 recordings 幂等回写 EndedAt 并发布
/// <c>recording-finished</c>，使手动 enqueue 与 file 模式自动识别两条链路恢复。
/// 触发时机：① 服务启动（重启残留）；② 新场次接管同房间（fencing 接管/上次异常退出）。
/// 通过条件更新（EndedAt 仍为 null）保证幂等，不覆盖新持有者已收尾的场次。
/// </summary>
public interface IOrphanRecordingFinalizer
{
    /// <summary>服务启动时收尾全部未结束场次。</summary>
    Task FinalizeAllOpenAsync(CancellationToken ct = default);

    /// <summary>新场次开始前收尾该房间遗留的未结束场次。</summary>
    Task FinalizeRoomAsync(long roomId, CancellationToken ct = default);

    /// <summary>收尾指定场次（停机/取消分支就地收尾当前场次）。</summary>
    Task FinalizeAsync(ObjectId recordingId, CancellationToken ct = default);
}

public sealed class OrphanRecordingFinalizer(
    RecordingRepository recordings,
    IRecordingEventPublisher events,
    RecordingOptions options,
    ILogger<OrphanRecordingFinalizer> logger) : IOrphanRecordingFinalizer
{
    public async Task FinalizeAllOpenAsync(CancellationToken ct = default)
    {
        var open = await recordings.ListOpenAsync(null, ct);
        foreach (var record in open)
        {
            await FinalizeOneAsync(record, ct);
        }
    }

    public async Task FinalizeRoomAsync(long roomId, CancellationToken ct = default)
    {
        var open = await recordings.ListOpenAsync(roomId, ct);
        foreach (var record in open)
        {
            await FinalizeOneAsync(record, ct);
        }
    }

    public async Task FinalizeAsync(ObjectId recordingId, CancellationToken ct = default)
    {
        var record = await recordings.GetByIdAsync(recordingId, ct);
        if (record is null || record.EndedAt is not null)
        {
            return;
        }

        await FinalizeOneAsync(record, ct);
    }

    private async Task FinalizeOneAsync(RecordingRecord record, CancellationToken ct)
    {
        var endedAt = ResolveEndedAt(record);

        bool updated;
        try
        {
            updated = await recordings.TrySetEndedAtAsync(record.Id, endedAt, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to finalize orphan recording {RecordingId}; skipping.", record.Id);
            return;
        }

        if (!updated)
        {
            // 已被其他实例/场次收尾
            logger.LogDebug("Orphan recording {RecordingId} already finalized; skipping publish.", record.Id);
            return;
        }

        logger.LogInformation(
            "Finalized orphan recording {RecordingId} for room {RoomId} (endedAt {EndedAt:o}).",
            record.Id,
            record.RoomId,
            endedAt);

        try
        {
            var fileUrls = record.Files.Select(f => BuildRecordingFileUrl(record.RoomId, f)).ToArray();
            await events.PublishFinishedAsync(new RecordingFinishedEvent(
                record.Id.ToString(),
                record.RoomId,
                record.Mid,
                record.Title,
                [.. record.Files],
                fileUrls,
                endedAt), ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to publish recording-finished for orphan recording {RecordingId}; continuing.", record.Id);
        }
    }

    /// <summary>以最后一个产出文件的 mtime 作为结束时间（更接近实际结束），无文件回退当前时间。</summary>
    private DateTime ResolveEndedAt(RecordingRecord record)
    {
        DateTime? latest = null;
        foreach (var file in record.Files)
        {
            try
            {
                var path = Path.Combine(options.StorageDir, record.RoomId.ToString(), file);
                if (File.Exists(path))
                {
                    var mtime = File.GetLastWriteTimeUtc(path);
                    if (latest is null || mtime > latest.Value)
                    {
                        latest = mtime;
                    }
                }
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Failed to read mtime for recording file {File}.", file);
            }
        }

        return latest ?? DateTime.UtcNow;
    }

    private string BuildRecordingFileUrl(long roomId, string fileName) =>
        $"{options.PublicBaseUrl.TrimEnd('/')}/api/{roomId}/recordings/{Uri.EscapeDataString(fileName)}";
}
