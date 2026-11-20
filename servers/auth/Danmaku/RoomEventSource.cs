using System.Text.Json;
using Z.Vtuber.Auth.Configuration;
using Z.Vtuber.Auth.Observability;

namespace Z.Vtuber.Auth.Danmaku;

public static class DanmakuBackoff
{
    /// <summary>1s/2s/4s/8s/16s/30s + 最多 20% 抖动。</summary>
    public static TimeSpan Next(int attempt, int maxSeconds, double jitter01 = 0)
    {
        var capped = Math.Min(Math.Max(attempt, 1), 6);
        var seconds = Math.Min(Math.Pow(2, capped - 1), maxSeconds);
        var jitter = seconds * 0.2 * Math.Clamp(jitter01, 0, 1);
        return TimeSpan.FromSeconds(seconds + jitter);
    }

    /// <summary>
    /// 计算下一次重连尝试序号：稳定运行达到阈值即重置为 1，否则累加。
    /// 异常/断线路径与正常 EOF 路径都必须经过此方法，避免读超时后间隔卡在上限。
    /// </summary>
    public static int NextAttempt(int current, TimeSpan runDuration, TimeSpan stableThreshold) =>
        runDuration >= stableThreshold ? 1 : current + 1;
}

/// <summary>单房间事件源：选主 → SSE 连接 → 心跳/弹幕处理 → 断线退避。</summary>
public sealed class RoomEventSource(
    string roomId,
    IOptions<DanmakuOptions> options,
    IHttpClientFactory httpClientFactory,
    DanmakuBackfillClient backfill,
    IDanmakuEventHandler handler,
    IRoomLeaderLease lease,
    DanmakuHealthRegistry registry,
    AuthMetrics metrics,
    TimeProvider clock,
    ILogger<RoomEventSource> logger)
{
    private readonly DanmakuDeduplicator _deduplicator = new(clock: clock);
    private readonly Random _random = new();
    private DateTime _lastOccurredAt;
    private int _attempt;

    public string RoomId => roomId;

    /// <summary>当前重连尝试序号（测试与诊断用）。</summary>
    public int CurrentAttempt => _attempt;

    public async Task RunAsync(CancellationToken ct)
    {
        _lastOccurredAt = clock.GetUtcNow().UtcDateTime - TimeSpan.FromSeconds(options.Value.BackfillWindowSeconds);

        while (!ct.IsCancellationRequested)
        {
            var acquired = false;
            var failed = false;
            DateTime? streamStartedAt = null;
            try
            {
                acquired = await lease.TryAcquireAsync(roomId, ct);
                if (!acquired)
                {
                    var other = await lease.IsHeldByOtherAsync(roomId, ct);
                    registry.MarkStandby(roomId, other);
                    await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, options.Value.LeaderLockTtlSeconds / 3)), ct);
                    continue;
                }

                registry.MarkLeader(roomId);
                using var leaderCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var renew = RenewLoopAsync(leaderCts);
                streamStartedAt = clock.GetUtcNow().UtcDateTime;

                try
                {
                    await StreamAsync(leaderCts.Token);
                }
                finally
                {
                    leaderCts.Cancel();
                    try
                    {
                        await renew;
                    }
                    catch (OperationCanceledException)
                    {
                    }

                    registry.MarkDisconnected(roomId);
                    await lease.ReleaseAsync(roomId, CancellationToken.None);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                failed = true;
                logger.LogWarning(ex, "Danmaku event source for room {RoomId} stopped unexpectedly.", roomId);
                registry.MarkError(roomId, ex.GetType().Name);
                if (acquired)
                {
                    await lease.ReleaseAsync(roomId, CancellationToken.None);
                }
            }
            finally
            {
                // 稳定 60 秒重置必须在异常路径同样生效（读超时/断线是设计内路径）。
                if (streamStartedAt is not null || failed)
                {
                    var runDuration = streamStartedAt is { } startedAt
                        ? clock.GetUtcNow().UtcDateTime - startedAt
                        : TimeSpan.Zero;
                    _attempt = DanmakuBackoff.NextAttempt(
                        _attempt, runDuration, TimeSpan.FromSeconds(60));
                }
            }

            var delay = DanmakuBackoff.Next(_attempt, options.Value.ReconnectMaxSeconds, _random.NextDouble());
            metrics.RoomReconnect();
            logger.LogInformation("Reconnecting danmaku room {RoomId} in {Delay}.", roomId, delay);
            await Task.Delay(delay, ct);
        }

        registry.MarkDisconnected(roomId);
    }

    private async Task RenewLoopAsync(CancellationTokenSource leaderCts)
    {
        var period = TimeSpan.FromSeconds(Math.Max(1, options.Value.LeaderLockTtlSeconds / 3));
        using var timer = new PeriodicTimer(period);
        while (await timer.WaitForNextTickAsync(leaderCts.Token))
        {
            if (!await lease.RenewAsync(roomId, leaderCts.Token))
            {
                logger.LogWarning("Lost danmaku leader lease for room {RoomId}; stopping stream.", roomId);
                leaderCts.Cancel();
                return;
            }
        }
    }

    private async Task StreamAsync(CancellationToken ct)
    {
        var client = httpClientFactory.CreateClient("DanmakuSource");
        var path = options.Value.BuildSubscribePath(roomId);
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Accept.ParseAdd("text/event-stream");

        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);
        registry.MarkConnected(roomId);
        metrics.RoomConnected();

        _ = BackfillAsync(ct);

        var timeout = TimeSpan.FromSeconds(options.Value.ReadTimeoutSeconds);
        while (!ct.IsCancellationRequested)
        {
            var frame = await DanmakuFrameParser.ReadFrameAsync(reader, timeout, ct);
            if (frame is null)
            {
                logger.LogInformation("Danmaku stream for room {RoomId} closed by server.", roomId);
                return;
            }

            if (string.Equals(frame.Event, "heartbeat", StringComparison.OrdinalIgnoreCase))
            {
                registry.MarkHeartbeat(roomId);
                continue;
            }

            if (!string.Equals(frame.Event, "danmaku", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            await HandleFrameAsync(frame, ct);
        }
    }

    private async Task HandleFrameAsync(DanmakuFrame frame, CancellationToken ct)
    {
        try
        {
            using var document = JsonDocument.Parse(frame.Data);
            if (!DanmakuPayloadReader.TryRead(document.RootElement, out var evt) || evt is null)
            {
                return;
            }

            await ProcessEventAsync(evt, ct);
        }
        catch (JsonException ex)
        {
            logger.LogDebug(ex, "Ignored malformed danmaku frame from room {RoomId}.", roomId);
        }
    }

    private async Task ProcessEventAsync(DanmakuEvent evt, CancellationToken ct)
    {
        if (!string.Equals(evt.RoomId, roomId, StringComparison.Ordinal))
        {
            return;
        }

        var sender = DanmakuSenderParser.Parse(evt.Payload);
        if (sender is null)
        {
            return;
        }

        if (!_deduplicator.TryAdd(roomId, evt.OccurredAt, sender.Uid, sender.Text))
        {
            return;
        }

        try
        {
            if (evt.OccurredAt > _lastOccurredAt)
            {
                _lastOccurredAt = evt.OccurredAt;
            }

            await handler.HandleAsync(roomId, evt, ct);
            registry.MarkEvent(roomId);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _deduplicator.Remove(roomId, evt.OccurredAt, sender.Uid, sender.Text);
            throw;
        }
        catch (Exception ex)
        {
            // Mongo 瞬时异常等失败时撤销去重标记，避免补投重放被丢弃导致 token 永远 pending。
            _deduplicator.Remove(roomId, evt.OccurredAt, sender.Uid, sender.Text);
            logger.LogWarning(ex, "Failed to process danmaku event from room {RoomId}; event will be retried by backfill.", roomId);
        }
    }

    private async Task BackfillAsync(CancellationToken ct)
    {
        try
        {
            var now = clock.GetUtcNow().UtcDateTime;
            var floor = now - TimeSpan.FromSeconds(options.Value.BackfillWindowSeconds);
            var from = _lastOccurredAt > floor ? _lastOccurredAt - TimeSpan.FromSeconds(2) : floor;
            if (from < floor)
            {
                from = floor;
            }

            var result = await backfill.FetchAsync(roomId, from, now, ct);
            foreach (var evt in result.Events)
            {
                await ProcessEventAsync(evt, ct);
            }

            metrics.RoomBackfillProcessed(result.Events.Count);
            if (result.Truncated)
            {
                metrics.RoomBackfillTruncated();
            }

            logger.LogInformation(
                "Danmaku backfill for room {RoomId} processed {Count} events (truncated={Truncated}).",
                roomId, result.Events.Count, result.Truncated);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Danmaku backfill failed for room {RoomId}; live stream continues.", roomId);
        }
    }
}

/// <summary>按配置 <c>RoomIds</c> 为每个房间建立独立事件源。</summary>
public sealed class RoomEventSourceHost(
    IServiceProvider services,
    IOptions<DanmakuOptions> options,
    ILogger<RoomEventSourceHost> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var tasks = ResolveRooms(options.Value)
            .Select(roomId =>
            {
                var source = ActivatorUtilities.CreateInstance<RoomEventSource>(services, roomId);
                logger.LogInformation("Starting danmaku event source for room {RoomId}.", roomId);
                return source.RunAsync(stoppingToken);
            })
            .ToArray();

        try
        {
            await Task.WhenAll(tasks);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    /// <summary>按配置顺序去重；非法配置由 <c>DanmakuOptionsValidator</c> 在启动时拦截。</summary>
    internal static IReadOnlyList<string> ResolveRooms(DanmakuOptions options) =>
        options.RoomIds.Distinct(StringComparer.Ordinal).ToList();
}
