using System.Collections.Concurrent;
using Mikibot.Crawler.WebsocketCrawler.Client;
using Mikibot.Crawler.WebsocketCrawler.Data;
using Z.Vtuber.Common.Live;
using Z.Vtuber.Common.Persistence;
using Z.Vtuber.Common.Persistence.Entities;
using Z.Vtuber.Common.Services;

namespace Z.Vtuber.Crawler.Live;

public sealed class LiveEventSource(
    ISubscriptionService subscription,
    AccountBasicInfoRepository accountRepository,
    ICookiePoolService cookiePool,
    DanmakuClientFactory factory,
    LiveEventRepository eventRepository,
    LiveEventTextIndexRepository textIndexRepository,
    ILiveEventPublisher publisher,
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
            var mids = await subscription.GetMidsAsync(SubscriptionType.Danmaku, ct);
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
            DanmakuClient? client = null;
            IDisposable? lockRelease = null;
            try
            {
                var cookie = (await cookiePool.NextAsync(ct)).Cookie;
                // Lock-aware: cookie lock held through CreateAsync + ConnectAsync, released immediately after.
                (client, lockRelease) = await factory.CreateWithLockAsync(cookie, ct);
                await client.ConnectAsync(roomId, ct);

                // Lock released immediately after connection established — event loop does NOT hold the cookie lock.
                lockRelease.Dispose();
                lockRelease = null;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                lockRelease?.Dispose();
                client?.Dispose();
                break;
            }
            catch (NoAvailableCookieException)
            {
                lockRelease?.Dispose();
                client?.Dispose();
                logger.LogWarning("Danmaku connection for room {RoomId} skipped: no active cookie; retrying in {Delay}s.", roomId, NoCookieRetryDelay.TotalSeconds);
                try
                {
                    await Task.Delay(NoCookieRetryDelay, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                continue;
            }
            catch (Exception ex)
            {
                lockRelease?.Dispose();
                client?.Dispose();
                logger.LogWarning(ex, "Danmaku connection for room {RoomId} lost; reconnecting in {Delay}s.", roomId, ReconnectDelay.TotalSeconds);
                try
                {
                    await Task.Delay(ReconnectDelay, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                continue;
            }

            // Lock released, client connected — event loop runs without holding the cookie lock.
            try
            {
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

                    var liveEventRecord = new LiveEventRecord
                    {
                        RoomId = liveEvent.RoomId,
                        LiveSessionId = liveEvent.LiveSessionId,
                        Type = liveEvent.Type,
                        Payload = liveEvent.Payload,
                        OccurredAt = liveEvent.OccurredAt,
                        Price = liveEvent.Price ?? GiftPriceExtractor.ExtractGoldSeeds(liveEvent.Type, liveEvent.Payload),
                    };

                    await eventRepository.InsertAsync(liveEventRecord, ct);

                    // 文本索引写入：弹幕/SC 事件提取文本并写入索引集合
                    try
                    {
                        var extraction = LiveEventTextExtractor.Extract(liveEvent.Type, liveEvent.Payload);
                        if (extraction is not null)
                        {
                            var textIndexRecord = new LiveEventTextIndexRecord
                            {
                                EventId = liveEventRecord.Id,
                                RoomId = liveEvent.RoomId,
                                Type = liveEvent.Type,
                                Text = extraction.Text,
                                SenderName = extraction.SenderName,
                                SenderUid = extraction.SenderUid,
                                SenderMedal = extraction.SenderMedal,
                                OccurredAt = liveEvent.OccurredAt
                            };
                            await textIndexRepository.InsertAsync(textIndexRecord, ct);
                        }
                    }
                    catch (MongoDB.Driver.MongoWriteException ex) when (ex.WriteError.Code == 11000)
                    {
                        // 唯一键冲突（EventId 已存在），仅记日志不阻断
                        logger.LogDebug(ex, "Text index record for event {EventId} already exists; skipping.", liveEventRecord.Id);
                    }
                    catch (Exception ex)
                    {
                        // 提取/写索引异常不阻断总线发布与 SSE
                        logger.LogWarning(ex, "Failed to write text index for event {EventId}; continuing.", liveEventRecord.Id);
                    }

                    await publisher.PublishAsync(liveEvent, ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
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
            finally
            {
                client?.Dispose();
            }
        }
    }
}
