using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Driver;
using Z.Vtuber.Common.Persistence;
using Z.Vtuber.Common.Persistence.Entities;

namespace Z.Vtuber.Tests;

/// <summary>Cookie 域索引初始化（含微博 cookie 两集合，决策 9 归 Cookie 域）。</summary>
public class CookieMongoSetupTests(MongoDbFixture fixture) : IClassFixture<MongoDbFixture>, IAsyncLifetime
{
    public Task InitializeAsync() => fixture.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private CookieMongoSetup Setup() => new(fixture.Database, NullLogger<CookieMongoSetup>.Instance);

    [Fact]
    public async Task EnsureIndexes_CreatesExpectedIndexes()
    {
        await Setup().EnsureIndexesAsync();

        var cookies = await TestIndexHelpers.ListIndexNamesAsync(
            fixture.Database.GetCollection<CookieRecord>(CookieCollectionNames.Cookies));
        Assert.Contains("Status_1_LastUsedAt_1", cookies);
        Assert.Contains("AccountId_1", cookies);

        var sessions = await TestIndexHelpers.ListIndexNamesAsync(
            fixture.Database.GetCollection<CookieSessionRecord>(CookieCollectionNames.CookieSessions));
        Assert.Contains("Status_1", sessions);
        Assert.Contains("CookieId_1", sessions);

        var account = await TestIndexHelpers.ListIndexNamesAsync(
            fixture.Database.GetCollection<CookieAccountInfoRecord>(CookieCollectionNames.CookieAccountInfo));
        Assert.Contains("CookieId_1", account);

        var weiboCookies = await TestIndexHelpers.ListIndexNamesAsync(
            fixture.Database.GetCollection<CookieRecord>(CookieCollectionNames.WeiboCookies));
        Assert.Contains("Status_1_LastUsedAt_1", weiboCookies);

        var weiboSessions = await TestIndexHelpers.ListIndexNamesAsync(
            fixture.Database.GetCollection<CookieSessionRecord>(CookieCollectionNames.WeiboCookieSessions));
        Assert.Contains("CookieId_1", weiboSessions);
    }

    [Fact]
    public async Task EnsureIndexes_IsIdempotent()
    {
        var setup = Setup();
        await setup.EnsureIndexesAsync();
        await setup.EnsureIndexesAsync();

        var cookies = await TestIndexHelpers.ListIndexNamesAsync(
            fixture.Database.GetCollection<CookieRecord>(CookieCollectionNames.Cookies));
        Assert.Single(cookies, n => n == "AccountId_1");
    }
}
