using Z.Vtuber.Common.Persistence;
using Z.Vtuber.Common.Persistence.Entities;
using Z.Vtuber.RecordingTranscript.Consumer.Configuration;
using Z.Vtuber.RecordingTranscript.Consumer.Enqueue;

namespace Z.Vtuber.RecordingTranscript.Consumer.Reconcile;

/// <summary>
/// 对账补建（事件 fire-and-forget 兜底，5min 周期 + 抖动）：
/// (a) 开播中漏建：recording_status.state=recording + 已启用 + 当前场次无任务 → 补建 live PENDING；
/// (b) 结束后漏建：recordings.EndedAt != null + 已启用 + 无该 recording+默认模型任务 → 补建 file PENDING。
/// 仅补「启用之后」开播/结束的场次；与唯一索引配合幂等。
/// </summary>
public sealed class TranscriptReconciler(
    RecordingStatusRepository statuses,
    RecordingRepository recordings,
    TranscriptAutoEnqueueRepository autoEnqueue,
    TranscriptTaskRepository tasks,
    TranscriptEnqueueService enqueue,
    TranscriptConsumerOptions options,
    ILogger<TranscriptReconciler> logger) : BackgroundService
{
    private const string RecordingState = "recording";
    private const int RoomRecordingScanLimit = 200;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // 启动抖动，避免多副本同时对账
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(Random.Shared.Next(1, 30)), stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ReconcileOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Transcript reconcile iteration failed; continuing.");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(options.ReconcileIntervalSeconds), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    public async Task ReconcileOnceAsync(CancellationToken ct = default)
    {
        var settings = (await autoEnqueue.GetAllAsync(ct)).Where(s => s.Enabled).ToList();
        if (settings.Count == 0)
        {
            return;
        }

        foreach (var setting in settings)
        {
            var model = await enqueue.ResolveSettingModelAsync(setting, "reconcile", ct);
            if (model is null)
            {
                // 未绑定（待完善）或模型不存在/被禁用：跳过该 roomId，不补建
                continue;
            }

            await ReconcileRoomAsync(setting, model, ct);
        }
    }

    private async Task ReconcileRoomAsync(
        TranscriptAutoEnqueueRecord setting, TranscriptModelRecord model, CancellationToken ct)
    {
        var status = await statuses.GetByRoomIdAsync(setting.RoomId, ct);
        if (status?.State == RecordingState)
        {
            var current = await FindCurrentRecordingAsync(setting.RoomId, ct);
            if (current is not null && current.StartedAt >= setting.UpdatedAt)
            {
                var existing = await tasks.GetByRecordingAndModelAsync(current.Id, model.Id, ct);
                if (existing is null)
                {
                    await enqueue.EnsureLiveTaskAsync(current, model, TranscriptTaskSources.Auto, ct);
                    logger.LogInformation(
                        "Reconciled missing live transcript task for room {RoomId} recording {RecordingId}",
                        setting.RoomId, current.Id);
                }
            }
        }

        var recent = await recordings.ListAsync(setting.RoomId, RoomRecordingScanLimit, null, ct);
        foreach (var recording in recent)
        {
            if (recording.EndedAt is null || recording.EndedAt < setting.UpdatedAt)
            {
                continue;
            }

            var existing = await tasks.GetByRecordingAndModelAsync(recording.Id, model.Id, ct);
            if (existing is null)
            {
                await enqueue.EnsureFileTaskAsync(recording, model, TranscriptTaskSources.Auto, ct);
                logger.LogInformation(
                    "Reconciled missing file transcript task for recording {RecordingId}", recording.Id);
            }
        }
    }

    private async Task<RecordingRecord?> FindCurrentRecordingAsync(long roomId, CancellationToken ct)
    {
        var recent = await recordings.ListAsync(roomId, RoomRecordingScanLimit, null, ct);
        return recent.FirstOrDefault(r => r.EndedAt is null);
    }
}
