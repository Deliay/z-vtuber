using MongoDB.Bson;
using Z.Vtuber.Common.Persistence;
using Z.Vtuber.Common.Persistence.Entities;
using Z.Vtuber.Common.Live;
using Z.Vtuber.Recording.Audio;
using Z.Vtuber.Recording.Configuration;
using Z.Vtuber.Recording.Live;
using Z.Vtuber.Recording.Services;

namespace Z.Vtuber.Recording.Recording;

public interface IRecordingSessionRunner
{
    Task RunAsync(
        long roomId,
        long mid,
        string? anchorName,
        LiveRoomInfo room,
        string cookieId,
        string cookie,
        CancellationToken ct);
}

/// <summary>
/// 单场次录制编排：抢锁 → 选流 → 渲染文件名 → 启动 ffmpeg 多输出 → stdout 泵喂 Audio Hub
/// → 状态上报 → 进程退出处理 → 释放锁；持锁期间 fencing 校验失锁立即 kill ffmpeg。
/// </summary>
public sealed class RecordingSession(
    IRecordingLockProvider locks,
    IBiliLiveApi bili,
    IStreamBitrateProbe bitrateProbe,
    IRecordingCookieClient cookieClient,
    IRecordingProcessFactory processes,
    AudioStreamHub audio,
    RecordingStatusReporter reporter,
    RecordingRepository recordings,
    IRecordingEventPublisher events,
    IOrphanRecordingFinalizer orphanFinalizer,
    RecordingOptions options,
    ILogger<RecordingSession> logger) : IRecordingSessionRunner
{
    private const int AudioChunkSize = 16384;

    // 录播按北京时间组织文件名（UTC+8）
    private static readonly TimeSpan BeijingOffset = TimeSpan.FromHours(8);

    public async Task RunAsync(
        long roomId,
        long mid,
        string? anchorName,
        LiveRoomInfo room,
        string cookieId,
        string cookie,
        CancellationToken ct)
    {
        IRecordingLock? sessionLock = null;
        try
        {
            sessionLock = await locks.TryAcquireAsync(roomId, ct);
            if (sessionLock is null)
            {
                logger.LogDebug("Recording lock for room {RoomId} held by another instance; skipping.", roomId);
                return;
            }

            sessionLock.StartRenewal(ct);
            await RunLockedAsync(roomId, mid, anchorName, room, cookieId, cookie, sessionLock, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            logger.LogInformation("Recording session for room {RoomId} cancelled (shutdown).", roomId);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Recording session failed for room {RoomId}.", roomId);
            await SafeReportErrorAsync(roomId, ex.Message, room.Title, ct);
        }
        finally
        {
            audio.MarkStopped(roomId);
            if (sessionLock is not null)
            {
                try
                {
                    await sessionLock.ReleaseAsync(CancellationToken.None);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Failed to release recording lock for room {RoomId}.", roomId);
                }

                await sessionLock.DisposeAsync();
            }
        }
    }

    private async Task RunLockedAsync(
        long roomId,
        long mid,
        string? anchorName,
        LiveRoomInfo room,
        string cookieId,
        string cookie,
        IRecordingLock sessionLock,
        CancellationToken ct)
    {
        // 新场次接管同房间前，先幂等收尾该房间遗留的未结束场次（上次异常退出/被接管），
        // 否则孤儿 recordings 永远 EndedAt=null，手动 enqueue 与 file 自动识别均无法建任务。
        await orphanFinalizer.FinalizeRoomAsync(roomId, ct);

        var candidates = await bili.GetStreamCandidatesAsync(cookie, roomId, ct);
        var stream = StreamResolver.Pick(candidates);
        if (stream is null)
        {
            // 无可播放流：可能登录 Cookie 失效，上报 invalid 供 cookie 服务自愈
            await SafeReportInvalidCookieAsync(cookieId, ct);
            await reporter.ReportErrorAsync(roomId, "no playable stream", room.Title, ct: ct);
            return;
        }

        var rawBitrateKbps = stream.BitrateKbps;
        if (rawBitrateKbps is null && options.ProbeBitrate)
        {
            rawBitrateKbps = await bitrateProbe.ProbeAsync(stream, cookie, ct);
        }

        var directory = Path.Combine(options.StorageDir, roomId.ToString());
        Directory.CreateDirectory(directory);

        var date = DateTime.UtcNow.Add(BeijingOffset);
        var rawName = RecordingFileName.Render(
            RecordingFileName.RawEncode, roomId, anchorName, date, room.Title);
        var rawPath = Path.Combine(directory, rawName);
        var files = new List<string> { rawName };

        string? av1Path = null;
        if (options.Av1.Enabled)
        {
            var av1Name = RecordingFileName.Render(
                RecordingFileName.Av1Encode, roomId, anchorName, date, room.Title);
            av1Path = Path.Combine(directory, av1Name);
            files.Add(av1Name);
        }

        var command = FfmpegProcessBuilder.Build(
            stream,
            rawPath,
            av1Path,
            cookie,
            options.Referer,
            options.UserAgent,
            rawBitrateKbps,
            options.DefaultBitrateKbps,
            options.Av1.Enabled,
            options.FfmpegPath);

        IRecordingProcess? process = null;
        try
        {
            process = processes.Start(command);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to start ffmpeg for room {RoomId}.", roomId);
            await reporter.ReportErrorAsync(roomId, $"ffmpeg start failed: {ex.Message}", room.Title, ct: ct);
            return;
        }

        audio.MarkStreaming(roomId);

        // 归档场次并发布 recording-started：失败仅记日志，不阻断录制。
        // 先归档/发布再上报 recording，保证外部观察到 recording 态时场次已归档且事件已发出。
        var startedAt = DateTime.UtcNow;
        ObjectId? recordingId = null;
        try
        {
            recordingId = await recordings.InsertAsync(new RecordingRecord
            {
                RoomId = roomId,
                Mid = mid,
                Title = room.Title,
                Files = [.. files],
                StartedAt = startedAt,
                CreatedAt = startedAt,
            }, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to archive recording for room {RoomId}; continuing.", roomId);
        }

        if (recordingId is not null)
        {
            try
            {
                await events.PublishStartedAsync(new RecordingStartedEvent(
                    recordingId.Value.ToString(),
                    roomId,
                    mid,
                    room.Title,
                    BuildLiveAudioUrl(roomId),
                    startedAt), ct);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to publish recording-started for room {RoomId}; continuing.", roomId);
            }
        }

        await reporter.ReportRecordingAsync(roomId, room.Title, files, ct);

        using var sessionCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var pumpTask = PumpAudioAsync(process.StandardOutput, roomId, sessionCts.Token);
        var exitTask = process.WaitForExitAsync(CancellationToken.None);
        var fencingLost = false;
        Exception? monitorFailure = null;

        try
        {
            while (!exitTask.IsCompleted)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(options.FencingIntervalMilliseconds), ct);
                await reporter.TouchAsync(roomId, ct);

                if (!await sessionLock.CheckOwnershipAsync(ct))
                {
                    fencingLost = true;
                    logger.LogWarning("Recording lock for room {RoomId} lost; killing ffmpeg.", roomId);
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 停机
        }
        catch (Exception ex)
        {
            monitorFailure = ex;
            logger.LogError(ex, "Recording monitor loop failed for room {RoomId}; killing ffmpeg.", roomId);
        }
        finally
        {
            // 任何非自然退出（fencing 失锁/停机/监控异常）都必须先终止并回收 ffmpeg，避免锁释放后双写
            if (!exitTask.IsCompleted)
            {
                process.Kill();
            }
        }

        var exitCode = await ResolveExitCodeAsync(exitTask);
        await sessionCts.CancelAsync();
        try
        {
            await pumpTask;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Audio pump ended for room {RoomId}.", roomId);
        }

        audio.MarkStopped(roomId);
        await process.DisposeAsync();

        if (monitorFailure is not null)
        {
            // 进程已回收，向上抛由外层记 error
            throw monitorFailure;
        }

        if (fencingLost)
        {
            // 锁已被新持有者接管：不覆盖其状态，交由新实例上报
            logger.LogInformation(
                "Recording for room {RoomId} ended without status update (lock lost to another holder).",
                roomId);
        }
        else if (ct.IsCancellationRequested)
        {
            // 优雅停机/取消订阅：落终态，避免重启期间残留 recording 状态（新实例接管后会重新上报 recording）；
            // 同时幂等收尾本场次（回写 EndedAt + 发布 finished），使旧 recordings 可 enqueue。
            await reporter.ReportNotLiveAsync(roomId);
            if (recordingId is not null)
            {
                try
                {
                    await orphanFinalizer.FinalizeAsync(recordingId.Value, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(
                        ex, "Failed to finalize recording {RecordingId} on shutdown; continuing.", recordingId);
                }
            }
        }
        else if (exitCode == 0)
        {
            await reporter.ReportNotLiveAsync(roomId);
            if (recordingId is not null)
            {
                var endedAt = DateTime.UtcNow;
                try
                {
                    await recordings.SetEndedAtAsync(recordingId.Value, endedAt, ct);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Failed to set EndedAt for recording {RecordingId}; continuing.", recordingId);
                }

                try
                {
                    var fileUrls = files
                        .Select(f => BuildRecordingFileUrl(roomId, f))
                        .ToArray();
                    await events.PublishFinishedAsync(new RecordingFinishedEvent(
                        recordingId.Value.ToString(),
                        roomId,
                        mid,
                        room.Title,
                        [.. files],
                        fileUrls,
                        endedAt), ct);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Failed to publish recording-finished for room {RoomId}; continuing.", roomId);
                }
            }
        }
        else
        {
            await reporter.ReportErrorAsync(roomId, $"ffmpeg exited with code {exitCode}", room.Title, files);
        }
    }

    private string BuildLiveAudioUrl(long roomId) =>
        $"{options.PublicBaseUrl.TrimEnd('/')}/api/{roomId}/stream/audio";

    private string BuildRecordingFileUrl(long roomId, string fileName) =>
        $"{options.PublicBaseUrl.TrimEnd('/')}/api/{roomId}/recordings/{Uri.EscapeDataString(fileName)}";

    private static async Task<int> ResolveExitCodeAsync(Task<int> exitTask)
    {
        try
        {
            return await exitTask;
        }
        catch
        {
            return -1;
        }
    }

    private async Task PumpAudioAsync(Stream stdout, long roomId, CancellationToken ct)
    {
        var buffer = new byte[AudioChunkSize];
        try
        {
            int read;
            while ((read = await stdout.ReadAsync(buffer, ct)) > 0)
            {
                audio.Publish(roomId, buffer[..read]);
            }
        }
        catch (OperationCanceledException)
        {
            // 正常结束
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Audio stdout pump failed for room {RoomId}.", roomId);
        }
    }

    private async Task SafeReportInvalidCookieAsync(string cookieId, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(cookieId))
        {
            return;
        }

        try
        {
            await cookieClient.ReportInvalidAsync(cookieId, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to report invalid cookie {CookieId}.", cookieId);
        }
    }

    private async Task SafeReportErrorAsync(long roomId, string error, string? sessionTitle, CancellationToken ct)
    {
        try
        {
            await reporter.ReportErrorAsync(roomId, error, sessionTitle, ct: ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to report error status for room {RoomId}.", roomId);
        }
    }
}
