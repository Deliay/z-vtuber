using System.Collections.Concurrent;
using Z.Vtuber.Common.Persistence;
using Z.Vtuber.Common.Persistence.Entities;
using Z.Vtuber.Common.Services;
using Z.Vtuber.Recording.Configuration;
using Z.Vtuber.Recording.Recording;
using Z.Vtuber.Recording.Services;

namespace Z.Vtuber.Recording.Live;

/// <summary>
/// 录播订阅开播轮询：读取 Recording 类型订阅 → mid 解析 roomId → cookie checkout + room get_info
/// → 未开播上报 not_live，已开播交 RecordingSession（后台执行）。
/// </summary>
public sealed class LiveStatusPoller(
    ISubscriptionService subscription,
    AccountBasicInfoRepository accountRepository,
    IBiliLiveApi bili,
    IRecordingCookieClient cookieClient,
    IRecordingSessionRunner sessionRunner,
    RecordingStatusReporter reporter,
    RecordingStatusRepository statusRepository,
    RecordingOptions options,
    ILogger<LiveStatusPoller> logger) : BackgroundService
{
    private readonly ConcurrentDictionary<long, CancellationTokenSource> _active = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Recording poll cycle failed.");
            }

            try
            {
                var baseInterval = TimeSpan.FromSeconds(options.PollIntervalSeconds);
                // 抖动：避免多实例/多房间同刻齐发
                var jitter = TimeSpan.FromMilliseconds(Random.Shared.Next(0, Math.Max(1, options.PollIntervalSeconds * 500)));
                await Task.Delay(baseInterval + jitter, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        // 停机：仅取消，Dispose 交由各会话的 finally（避免与会话内 linked CTS 竞态）
        foreach (var cts in _active.Values)
        {
            cts.Cancel();
        }

        _active.Clear();
    }

    internal async Task RunOnceAsync(CancellationToken ct)
    {
        var mids = await subscription.GetMidsAsync(SubscriptionType.Recording, ct);
        var subscribedRooms = new HashSet<long>();

        foreach (var mid in mids.Distinct())
        {
            var roomId = await accountRepository.GetLatestRoomIdByMidAsync(mid, ct);
            if (roomId is null or <= 0)
            {
                logger.LogDebug("Recording poll skipped for mid {Mid}: roomId not resolvable yet.", mid);
                continue;
            }

            subscribedRooms.Add(roomId.Value);
            if (_active.ContainsKey(roomId.Value))
            {
                continue;
            }

            await PollAndMaybeStartAsync(mid, roomId.Value, ct);
        }

        // 订阅已被取消：停止仍在进行的录制
        foreach (var pair in _active)
        {
            if (!subscribedRooms.Contains(pair.Key) && _active.TryRemove(pair.Key, out var cts))
            {
                logger.LogInformation("Recording subscription removed for room {RoomId}; stopping session.", pair.Key);
                // 仅取消；Dispose 统一在 RunSessionAsync 的 finally，避免与会话内 linked CTS 竞态
                cts.Cancel();
            }
        }
    }

    private async Task PollAndMaybeStartAsync(long mid, long roomId, CancellationToken ct)
    {
        try
        {
            var checkout = await cookieClient.CheckoutAsync(ct);
            var room = await bili.GetRoomInfoAsync(checkout.Cookie, roomId, ct);

            if (!room.IsLive)
            {
                await reporter.ReportNotLiveAsync(roomId, ct);
                return;
            }

            var anchorName = await GetAnchorNameAsync(mid, ct);
            var sessionCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            if (!_active.TryAdd(roomId, sessionCts))
            {
                sessionCts.Dispose();
                return;
            }

            _ = RunSessionAsync(roomId, anchorName, room, checkout.CookieId, checkout.Cookie, sessionCts);
        }
        catch (NoAvailableCookieException)
        {
            logger.LogWarning("Recording poll skipped for room {RoomId}: no active cookie.", roomId);
            // 无法启动录制时必须纠正陈旧 recording 状态，避免重启 + cookie 失效导致永久残留
            await CorrectStaleRecordingStatusAsync(roomId, "no available cookie", ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // 停机
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Recording poll failed for room {RoomId}.", roomId);
            await CorrectStaleRecordingStatusAsync(roomId, "recording poll failed", ct);
        }
    }

    /// <summary>
    /// 无法启动/继续录制时纠正陈旧状态：库中该房间为 recording 且 UpdatedAt 已超过阈值（无活跃录制在刷新心跳），
    /// 上报 error 反映现实；未过期则不动（可能仍有实例在录制）。
    /// </summary>
    private async Task CorrectStaleRecordingStatusAsync(long roomId, string reason, CancellationToken ct)
    {
        try
        {
            var status = await statusRepository.GetByRoomIdAsync(roomId, ct);
            if (status is null || status.State != RecordingStates.Recording)
            {
                return;
            }

            var threshold = TimeSpan.FromSeconds(options.StaleStatusThresholdSeconds);
            if (DateTime.UtcNow - status.UpdatedAt < threshold)
            {
                return;
            }

            logger.LogWarning("Correcting stale recording status for room {RoomId}: {Reason}", roomId, reason);
            await reporter.ReportErrorAsync(roomId, reason, status.SessionTitle, status.Files, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to correct stale recording status for room {RoomId}.", roomId);
        }
    }

    private async Task RunSessionAsync(
        long roomId,
        string? anchorName,
        LiveRoomInfo room,
        string cookieId,
        string cookie,
        CancellationTokenSource cts)
    {
        try
        {
            await sessionRunner.RunAsync(roomId, anchorName, room, cookieId, cookie, cts.Token);
        }
        catch (OperationCanceledException)
        {
            // 正常停止
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Recording session for room {RoomId} failed.", roomId);
        }
        finally
        {
            if (_active.TryGetValue(roomId, out var current) && ReferenceEquals(current, cts))
            {
                _active.TryRemove(roomId, out _);
            }

            cts.Dispose();
        }
    }

    private async Task<string?> GetAnchorNameAsync(long mid, CancellationToken ct)
    {
        var info = await accountRepository.GetLatestByMidAsync(mid, ct);
        return AnchorNameExtractor.Extract(info?.Data);
    }
}
