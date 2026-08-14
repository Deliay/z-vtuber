using System.Collections.Concurrent;
using Mikibot.Crawler.WebsocketCrawler.Data;
using Z.Vtuber.Persistence;
using Z.Vtuber.Persistence.Entities;
using Z.Vtuber.Services;

namespace Z.Vtuber.Live;

public sealed class LiveEventSource(
    ISubscriptionService subscription,
    AccountBasicInfoRepository accountRepository,
    ICookiePoolService cookiePool,
    DanmakuClientFactory factory,
    LiveEventRepository eventRepository,
    ILiveEventBus bus,
    ILiveSessionTracker sessionTracker,
    ILogger<LiveEventSource> logger) : BackgroundService
{
    private static readonly TimeSpan ReconcileInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan NoCookieRetryDelay = TimeSpan.FromSeconds(30);

    private readonly ConcurrentDictionary<long, CancellationTokenSource> _connections = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(ReconcileInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await ReconcileAsync(stoppingToken);
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        foreach (var (_, cts) in _connections)
        {
            cts.Cancel();
        }

        _connections.Clear();
        await base.StopAsync(cancellationToken);
    }

    private async Task ReconcileAsync(CancellationToken ct)
    {
        var roomIds = new HashSet<long>();
        try
        {
            var mids = await subscription.GetMidsAsync(ct);
            foreach (var mid in mids)
            {
                var roomId = await accountRepository.GetLatestRoomIdByMidAsync(mid, ct);
                if (roomId is > 0)
                {
                    roomIds.Add(roomId.Value);
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to resolve subscribed rooms for danmaku connections.");
            return;
        }

        foreach (var roomId in roomIds)
        {
            if (_connections.ContainsKey(roomId))
            {
                continue;
            }

            var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            if (_connections.TryAdd(roomId, cts))
            {
                _ = RunRoomAsync(roomId, cts.Token);
            }
            else
            {
                cts.Dispose();
            }
        }

        foreach (var roomId in _connections.Keys.ToList())
        {
            if (roomIds.Contains(roomId))
            {
                continue;
            }

            if (_connections.TryRemove(roomId, out var cts))
            {
                cts.Cancel();
                cts.Dispose();
            }
        }
    }

    private async Task RunRoomAsync(long roomId, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var cookie = (await cookiePool.NextAsync(ct)).Cookie;
                using var client = await factory.CreateAsync(cookie, ct);
                await client.ConnectAsync(roomId, ct);

                await foreach (var data in client.Events(ct))
                {
                    if (data is not Normal normal)
                    {
                        continue;
                    }

                    var liveEvent = DanmakuEventNormalizer.Normalize(
                        roomId,
                        sessionTracker.GetCurrentSessionId(roomId),
                        normal.RawContent,
                        DateTime.UtcNow);
                    if (liveEvent is null)
                    {
                        continue;
                    }

                    await eventRepository.InsertAsync(new LiveEventRecord
                    {
                        RoomId = liveEvent.RoomId,
                        LiveSessionId = liveEvent.LiveSessionId,
                        Type = liveEvent.Type,
                        Payload = liveEvent.Payload,
                        OccurredAt = liveEvent.OccurredAt,
                    }, ct);

                    bus.TryPublish(roomId, liveEvent);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (NoAvailableCookieException)
            {
                logger.LogWarning("Danmaku connection for room {RoomId} skipped: no active cookie; retrying in {Delay}s.", roomId, NoCookieRetryDelay.TotalSeconds);
                try
                {
                    await Task.Delay(NoCookieRetryDelay, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Danmaku connection for room {RoomId} lost; reconnecting in {Delay}s.", roomId, ReconnectDelay.TotalSeconds);
                try
                {
                    await Task.Delay(ReconnectDelay, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
            }
        }
    }
}
