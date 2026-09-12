using Microsoft.Extensions.Logging;
using MongoDB.Driver;
using Z.Vtuber.Common.Persistence;
using Z.Vtuber.Common.Persistence.Entities;
using Z.Vtuber.Common.Services;
using Z.Vtuber.Crawler.Configuration;
using Z.Vtuber.Crawler.Crawlers;
using Z.Vtuber.Crawler.Jobs;

namespace Z.Vtuber.Crawler.Services;

public class LiveGuardTop3BackfillService
{
    private const int BeijingOffsetHours = 8;

    private readonly AccountBasicInfoRepository _accountRepository;
    private readonly LiveGuardRepository _guardRepository;
    private readonly ICookieServiceClient _cookieServiceClient;
    private readonly IAccountInfoCrawler _accountInfoCrawler;
    private readonly IMongoCollection<MetaRecord> _metaCollection;
    private readonly LiveGuardListFetcher _fetcher;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<LiveGuardTop3BackfillService> _logger;

    public LiveGuardTop3BackfillService(
        AccountBasicInfoRepository accountRepository,
        LiveGuardRepository guardRepository,
        ICookieServiceClient cookieServiceClient,
        IAccountInfoCrawler accountInfoCrawler,
        IMongoDatabase database,
        LiveGuardListFetcher fetcher,
        TimeProvider timeProvider,
        ILogger<LiveGuardTop3BackfillService> logger)
    {
        _accountRepository = accountRepository;
        _guardRepository = guardRepository;
        _cookieServiceClient = cookieServiceClient;
        _accountInfoCrawler = accountInfoCrawler;
        _metaCollection = database.GetCollection<MetaRecord>(MongoCollectionNames.Meta);
        _fetcher = fetcher;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task ExecuteAsync(CancellationToken ct)
    {
        var today = BeijingNow().ToString("yyyy-MM-dd");
        _logger.LogInformation("Starting top3 backfill for {Date}.", today);

        var mids = await _guardRepository.GetDistinctMidsAsync(ct);
        _logger.LogInformation("Found {Count} distinct mids with snapshots.", mids.Count);

        var processedCount = 0;
        var skippedCount = 0;
        var failedCount = 0;
        var backfilledEntriesCount = 0;

        string cookieId;
        string cookie;
        try
        {
            var checkout = await _cookieServiceClient.CheckoutAsync(ct);
            cookieId = checkout.CookieId;
            cookie = checkout.Cookie;
        }
        catch (NoAvailableCookieException)
        {
            _logger.LogWarning("No available cookie, backfill round aborted.");
            return;
        }

        foreach (var mid in mids)
        {
            if (ct.IsCancellationRequested)
            {
                break;
            }

            if (await IsMarkedAsync(mid))
            {
                skippedCount++;
                continue;
            }

            try
            {
                var roomId = await ResolveRoomIdAsync(mid, ct);
                if (roomId is null)
                {
                    _logger.LogWarning("Backfill skipped for mid {Mid}: no roomId.", mid);
                    failedCount++;
                    continue;
                }

                var entries = await _fetcher.FetchAllAsync(cookieId, cookie, mid, roomId.Value, ct);
                if (entries is null)
                {
                    _logger.LogWarning("Backfill skipped for mid {Mid}: fetch failed.", mid);
                    failedCount++;
                    continue;
                }

                var top10 = LiveGuardTop3BackfillPlanner.SelectTop10(entries);
                var snapshots = await _guardRepository.GetSnapshotsByMidAsync(mid, today, ct);

                foreach (var snapshot in snapshots)
                {
                    if (ct.IsCancellationRequested)
                    {
                        break;
                    }

                    var missing = LiveGuardTop3BackfillPlanner.ComputeMissing(snapshot.Guards, top10);
                    if (missing.Count > 0)
                    {
                        var finalGuardCount = snapshot.Guards.Count + missing.Count;
                        await _guardRepository.BackfillMissingEntriesAsync(mid, snapshot.Date, missing, finalGuardCount, ct);
                        backfilledEntriesCount += missing.Count;
                    }
                }

                await MarkAsync(mid);
                processedCount++;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Backfill failed for mid {Mid}.", mid);
                failedCount++;
            }
        }

        _logger.LogInformation(
            "Backfill completed. Processed: {Processed}, Skipped: {Skipped}, Failed: {Failed}, Backfilled entries: {Backfilled}",
            processedCount, skippedCount, failedCount, backfilledEntriesCount);
    }

    private async Task<long?> ResolveRoomIdAsync(long mid, CancellationToken ct)
    {
        var roomId = await _accountRepository.GetLatestRoomIdByMidAsync(mid, ct);
        if (roomId is null)
        {
            await _accountInfoCrawler.CrawlAsync(mid, ct);
            roomId = await _accountRepository.GetLatestRoomIdByMidAsync(mid, ct);
        }
        return roomId;
    }

    private async Task<bool> IsMarkedAsync(long mid)
    {
        var key = $"live_guard_top3_backfill_v1:{mid}";
        var filter = Builders<MetaRecord>.Filter.Eq(m => m.Key, key);
        var result = await _metaCollection.Find(filter).FirstOrDefaultAsync();
        return result is not null;
    }

    private async Task MarkAsync(long mid)
    {
        var key = $"live_guard_top3_backfill_v1:{mid}";
        var filter = Builders<MetaRecord>.Filter.Eq(m => m.Key, key);
        var record = new MetaRecord { Key = key };
        await _metaCollection.ReplaceOneAsync(filter, record, new ReplaceOptions { IsUpsert = true });
    }

    private DateTime BeijingNow() => _timeProvider.GetUtcNow().UtcDateTime.AddHours(BeijingOffsetHours);
}