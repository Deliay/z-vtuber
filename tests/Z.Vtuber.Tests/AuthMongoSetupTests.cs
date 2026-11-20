using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using MongoDB.Driver;
using Z.Vtuber.Auth.Persistence;

namespace Z.Vtuber.Tests;

public sealed class AuthMongoSetupTests : IClassFixture<MongoDbFixture>, IAsyncLifetime
{
    private readonly MongoDbFixture _fixture;

    public AuthMongoSetupTests(MongoDbFixture fixture) => _fixture = fixture;

    public Task InitializeAsync() => _fixture.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task EnsureIndexes_IsIdempotent()
    {
        var setup = new AuthMongoSetup(_fixture.Database, NullLogger<AuthMongoSetup>.Instance);
        await setup.EnsureIndexesAsync();
        await setup.EnsureIndexesAsync();

        var names = await IndexNamesAsync(AuthCollectionNames.LoginRequests);
        Assert.Contains("RequestId_1", names);
        Assert.Contains("TokenHash_1", names);
        Assert.Contains("ExpiresAt_1_TTL", names);
    }

    [Fact]
    public async Task LoginRequestTtl_KeepsRecordsOneHourAfterExpiry()
    {
        var index = await FindIndexAsync(AuthCollectionNames.LoginRequests, "ExpiresAt_1_TTL");
        Assert.True(index.Contains("expireAfterSeconds"));
        Assert.Equal(3600, index["expireAfterSeconds"].ToInt32());
    }

    [Fact]
    public async Task ProviderTokenTtl_OnlyOnRefreshExpiry()
    {
        var names = await IndexNamesAsync(AuthCollectionNames.ProviderTokens);
        Assert.Contains("RefreshExpiresAt_1_TTL", names);
        Assert.DoesNotContain("ExpiresAt_1_TTL", names);

        var index = await FindIndexAsync(AuthCollectionNames.ProviderTokens, "RefreshExpiresAt_1_TTL");
        Assert.Equal(0, index["expireAfterSeconds"].ToInt32());
    }

    [Fact]
    public async Task UserCompositeIndex_IsUnique()
    {
        var index = await FindIndexAsync(AuthCollectionNames.Users, "Platform_1_PlatformUid_1");
        Assert.True(index["unique"].ToBoolean());
    }

    [Fact]
    public async Task AuditTtl_IsNinetyDays()
    {
        var index = await FindIndexAsync(AuthCollectionNames.AuditLogs, "OccurredAt_-1_TTL");
        Assert.Equal(90 * 24 * 3600, index["expireAfterSeconds"].ToInt32());
    }

    private async Task<List<string>> IndexNamesAsync(string collection)
    {
        using var cursor = await _fixture.Database.GetCollection<BsonDocument>(collection).Indexes.ListAsync();
        var indexes = await cursor.ToListAsync();
        return indexes.Select(x => x["name"].AsString).ToList();
    }

    private async Task<BsonDocument> FindIndexAsync(string collection, string name)
    {
        using var cursor = await _fixture.Database.GetCollection<BsonDocument>(collection).Indexes.ListAsync();
        var indexes = await cursor.ToListAsync();
        return indexes.Single(x => x["name"].AsString == name);
    }
}
