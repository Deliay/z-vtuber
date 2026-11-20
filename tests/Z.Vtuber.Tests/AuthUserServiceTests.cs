using MongoDB.Driver;
using Z.Vtuber.Auth.Auth;
using Z.Vtuber.Auth.Persistence;

namespace Z.Vtuber.Tests;

public sealed class AuthUserServiceTests : IClassFixture<MongoDbFixture>, IAsyncLifetime
{
    private readonly MongoDbFixture _fixture;
    private readonly AuthTestServices _services;

    public AuthUserServiceTests(MongoDbFixture fixture)
    {
        _fixture = fixture;
        _services = AuthTestServices.Create(fixture.Database);
    }

    public Task InitializeAsync() => _fixture.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Upsert_CreatesThenUpdatesSameUser()
    {
        var first = await _services.UserService.UpsertBilibiliUserAsync(403496, "Zero_Ash", "avatar-1", "30066", "127.0.0.1");
        var second = await _services.UserService.UpsertBilibiliUserAsync(403496, "Zero_Ash2", "avatar-2", "12345", "127.0.0.2");

        Assert.True(first.Created);
        Assert.False(second.Created);
        Assert.Equal(first.User.Id, second.User.Id);
        Assert.Equal("Zero_Ash2", second.User.DisplayName);
        Assert.Equal("avatar-2", second.User.AvatarUrl);
        Assert.Equal(2, second.User.LoginCount);
        Assert.Equal("bili_403496", second.User.ProviderSubject);
        Assert.Equal("12345", second.User.LastSourceRoomId);
    }

    [Fact]
    public async Task ConcurrentUpsert_SingleDocument()
    {
        var tasks = Enumerable.Range(0, 4)
            .Select(i => _services.UserService.UpsertBilibiliUserAsync(500, $"user-{i}", null, "30066", null))
            .ToArray();
        var results = await Task.WhenAll(tasks);

        Assert.Single(results.Select(x => x.User.Id).Distinct());
        var all = await _services.Users.FindAllAsync();
        Assert.Single(all);
    }

    [Fact]
    public async Task DisabledUser_IsRejected()
    {
        var created = await _services.UserService.UpsertBilibiliUserAsync(600, "disabled", null, "30066", null);
        await _fixture.Database.GetCollection<AuthUserRecord>(AuthCollectionNames.Users)
            .UpdateOneAsync(x => x.Id == created.User.Id,
                Builders<AuthUserRecord>.Update.Set(x => x.Status, AuthUserStatus.Disabled));

        var result = await _services.UserService.UpsertBilibiliUserAsync(600, "disabled", null, "30066", null);
        Assert.True(result.Disabled);
    }

    [Fact]
    public async Task EmptyName_FallsBackToUid()
    {
        var result = await _services.UserService.UpsertBilibiliUserAsync(700, "  ", null, "30066", null);
        Assert.Equal("UID:700", result.User.DisplayName);
    }
}
