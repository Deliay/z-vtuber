using MongoDB.Driver;
using Z.Vtuber.Common.Persistence.Entities;

namespace Z.Vtuber.Common.Persistence;

public sealed class CookieMongoSetup(IMongoDatabase database, ILogger<CookieMongoSetup> logger)
    : MongoDbInitializer(database, logger)
{
    public async Task EnsureIndexesAsync(CancellationToken ct = default)
    {
        await EnsureCookiesIndexesAsync(ct);
        await EnsureCookieSessionIndexesAsync(ct);
        await EnsureCookieAccountInfoIndexesAsync(ct);
        await EnsureWeiboCookieIndexesAsync(ct);
        Logger.LogInformation("MongoDB cookie indexes ensured for database {Database}", DatabaseName);
    }

    private async Task EnsureCookiesIndexesAsync(CancellationToken ct)
    {
        var collection = Database.GetCollection<CookieRecord>(CookieCollectionNames.Cookies);
        var keys = Builders<CookieRecord>.IndexKeys
            .Ascending(c => c.Status)
            .Ascending(c => c.LastUsedAt);
        await collection.Indexes.CreateOneAsync(new CreateIndexModel<CookieRecord>(keys), cancellationToken: ct);

        var accountIdKeys = Builders<CookieRecord>.IndexKeys.Ascending(c => c.AccountId);
        await collection.Indexes.CreateOneAsync(
            new CreateIndexModel<CookieRecord>(accountIdKeys, new CreateIndexOptions { Name = "AccountId_1" }),
            cancellationToken: ct);
    }

    private async Task EnsureCookieSessionIndexesAsync(CancellationToken ct)
    {
        var collection = Database.GetCollection<CookieSessionRecord>(CookieCollectionNames.CookieSessions);
        var statusKeys = Builders<CookieSessionRecord>.IndexKeys.Ascending(s => s.Status);
        await collection.Indexes.CreateOneAsync(
            new CreateIndexModel<CookieSessionRecord>(statusKeys, new CreateIndexOptions { Name = "Status_1" }),
            cancellationToken: ct);

        var cookieIdKeys = Builders<CookieSessionRecord>.IndexKeys.Ascending(s => s.CookieId);
        await collection.Indexes.CreateOneAsync(
            new CreateIndexModel<CookieSessionRecord>(cookieIdKeys, new CreateIndexOptions { Name = "CookieId_1" }),
            cancellationToken: ct);
    }

    private Task EnsureCookieAccountInfoIndexesAsync(CancellationToken ct)
    {
        var collection = Database.GetCollection<CookieAccountInfoRecord>(CookieCollectionNames.CookieAccountInfo);
        var keys = Builders<CookieAccountInfoRecord>.IndexKeys.Ascending(r => r.CookieId);
        var options = new CreateIndexOptions { Unique = true, Name = "CookieId_1" };
        return collection.Indexes.CreateOneAsync(new CreateIndexModel<CookieAccountInfoRecord>(keys, options), cancellationToken: ct);
    }

    private async Task EnsureWeiboCookieIndexesAsync(CancellationToken ct)
    {
        // weibo_cookies indexes (same structure as bilibili_cookies)
        var cookiesCollection = Database.GetCollection<CookieRecord>(CookieCollectionNames.WeiboCookies);
        var cookieKeys = Builders<CookieRecord>.IndexKeys
            .Ascending(c => c.Status)
            .Ascending(c => c.LastUsedAt);
        await cookiesCollection.Indexes.CreateOneAsync(new CreateIndexModel<CookieRecord>(cookieKeys), cancellationToken: ct);

        var cookieAccountIdKeys = Builders<CookieRecord>.IndexKeys.Ascending(c => c.AccountId);
        await cookiesCollection.Indexes.CreateOneAsync(
            new CreateIndexModel<CookieRecord>(cookieAccountIdKeys, new CreateIndexOptions { Name = "AccountId_1" }),
            cancellationToken: ct);

        // weibo_cookie_sessions indexes (same structure as bilibili_cookie_sessions)
        var sessionsCollection = Database.GetCollection<CookieSessionRecord>(CookieCollectionNames.WeiboCookieSessions);
        var sessionStatusKeys = Builders<CookieSessionRecord>.IndexKeys.Ascending(s => s.Status);
        await sessionsCollection.Indexes.CreateOneAsync(
            new CreateIndexModel<CookieSessionRecord>(sessionStatusKeys, new CreateIndexOptions { Name = "Status_1" }),
            cancellationToken: ct);

        var sessionCookieIdKeys = Builders<CookieSessionRecord>.IndexKeys.Ascending(s => s.CookieId);
        await sessionsCollection.Indexes.CreateOneAsync(
            new CreateIndexModel<CookieSessionRecord>(sessionCookieIdKeys, new CreateIndexOptions { Name = "CookieId_1" }),
            cancellationToken: ct);

        Logger.LogInformation("Ensured weibo cookie indexes.");
    }
}
