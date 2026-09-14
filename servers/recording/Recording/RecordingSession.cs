using Z.Vtuber.Recording.Audio;
using Z.Vtuber.Recording.Configuration;
using Z.Vtuber.Recording.Live;

namespace Z.Vtuber.Recording.Recording;

public interface IRecordingSessionRunner
{
    Task RunAsync(long roomId, string? anchorName, LiveRoomInfo room, string cookie, CancellationToken ct);
}

/// <summary>
/// 单场次录制编排：抢锁 → 选流 → 渲染文件名 → 启动 ffmpeg 多输出 → stdout 泵喂 Audio Hub
/// → 状态上报 → 进程退出处理 → 释放锁；持锁期间 fencing 校验失锁立即 kill ffmpeg。
/// </summary>
public sealed class RecordingSession(
    IRecordingLockProvider locks,
    IBiliLiveApi bili,
    IRecordingProcessFactory processes,
    AudioStreamHub audio,
    RecordingStatusReporter reporter,
    RecordingOptions options,
    ILogger<RecordingSession> logger) : IRecordingSessionRunner
{
    private const int AudioChunkSize = 16384;

    public async Task RunAsync(long roomId, string? anchorName, LiveRoomInfo room, string cookie, CancellationToken ct)
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
            await RunLockedAsync(roomId, anchorName, room, cookie, sessionLock, ct);
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
        string? anchorName,
        LiveRoomInfo room,
        string cookie,
        IRecordingLock sessionLock,
        CancellationToken ct)
    {
        var candidates = await bili.GetStreamCandidatesAsync(cookie, roomId, ct);
        var stream = StreamResolver.Pick(candidates);
        if (stream is null)
        {
            await reporter.ReportErrorAsync(roomId, "no playable stream", room.Title, ct);
            return;
        }

        var directory = Path.Combine(options.StorageDir, roomId.ToString());
        Directory.CreateDirectory(directory);

        var date = DateTime.UtcNow;
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
            stream.BitrateKbps,
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
            await reporter.ReportErrorAsync(roomId, $"ffmpeg start failed: {ex.Message}", room.Title, ct);
            return;
        }

        audio.MarkStreaming(roomId);
        await reporter.ReportRecordingAsync(roomId, room.Title, files, ct);

        using var sessionCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var pumpTask = PumpAudioAsync(process.StandardOutput, roomId, sessionCts.Token);
        var exitTask = process.WaitForExitAsync(CancellationToken.None);
        var fencingLost = false;

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
                    process.Kill();
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            process.Kill();
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

        if (fencingLost || ct.IsCancellationRequested)
        {
            await SafeReportNotLiveAsync(roomId);
        }
        else if (exitCode == 0)
        {
            await reporter.ReportNotLiveAsync(roomId);
        }
        else
        {
            await reporter.ReportErrorAsync(roomId, $"ffmpeg exited with code {exitCode}", room.Title);
        }
    }

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

    private async Task SafeReportErrorAsync(long roomId, string error, string? sessionTitle, CancellationToken ct)
    {
        try
        {
            await reporter.ReportErrorAsync(roomId, error, sessionTitle, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to report error status for room {RoomId}.", roomId);
        }
    }

    private async Task SafeReportNotLiveAsync(long roomId)
    {
        try
        {
            await reporter.ReportNotLiveAsync(roomId);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to report not_live status for room {RoomId}.", roomId);
        }
    }
}
