using MongoDB.Bson;
using MongoDB.Driver;
using Z.Vtuber.Common.Persistence;
using Z.Vtuber.Common.Persistence.Entities;

namespace Z.Vtuber.Tests;

[Collection("mongo")]
public class NotificationSubscriptionRepositoryTests(MongoDbFixture fixture) : IAsyncLifetime
{
    public Task InitializeAsync() => fixture.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private NotificationSubscriptionRepository CreateRepository()
        => new(fixture.Database);

    [Fact]
    public async Task InsertAsync_InsertsRecordSuccessfully()
    {
        var repo = CreateRepository();
        var record = new NotificationSubscriptionRecord
        {
            EventType = "cookie_invalid",
            Platform = "test",
            GuildId = "guild1",
            GuildName = "Test Guild",
            ChannelId = "channel1",
            ChannelName = "Test Channel",
        };

        var inserted = await repo.InsertAsync(record);

        Assert.NotEqual(ObjectId.Empty, inserted.Id);
        Assert.Equal("cookie_invalid", inserted.EventType);
        Assert.Equal("channel1", inserted.ChannelId);
    }

    [Fact]
    public async Task ListAsync_ReturnsAllRecords()
    {
        var repo = CreateRepository();
        await repo.InsertAsync(new NotificationSubscriptionRecord
        {
            EventType = "cookie_invalid",
            Platform = "test",
            GuildId = "guild1",
            ChannelId = "channel1",
        });
        await repo.InsertAsync(new NotificationSubscriptionRecord
        {
            EventType = "cookie_invalid",
            Platform = "test",
            GuildId = "guild2",
            ChannelId = "channel2",
        });

        var results = await repo.ListAsync();

        Assert.Equal(2, results.Count);
    }

    [Fact]
    public async Task GetByEventTypeAsync_FiltersCorrectly()
    {
        var repo = CreateRepository();
        await repo.InsertAsync(new NotificationSubscriptionRecord
        {
            EventType = "cookie_invalid",
            Platform = "test",
            GuildId = "guild1",
            ChannelId = "channel1",
        });
        await repo.InsertAsync(new NotificationSubscriptionRecord
        {
            EventType = "other_type",
            Platform = "test",
            GuildId = "guild2",
            ChannelId = "channel2",
        });

        var results = await repo.GetByEventTypeAsync("cookie_invalid");

        Assert.Single(results);
        Assert.Equal("channel1", results[0].ChannelId);
    }

    [Fact]
    public async Task DeleteAsync_ExistingRecord_ReturnsTrue()
    {
        var repo = CreateRepository();
        var inserted = await repo.InsertAsync(new NotificationSubscriptionRecord
        {
            EventType = "cookie_invalid",
            Platform = "test",
            GuildId = "guild1",
            ChannelId = "channel1",
        });

        var deleted = await repo.DeleteAsync(inserted.Id);

        Assert.True(deleted);
        var remaining = await repo.ListAsync();
        Assert.Empty(remaining);
    }

    [Fact]
    public async Task DeleteAsync_NonExistingRecord_ReturnsFalse()
    {
        var repo = CreateRepository();

        var deleted = await repo.DeleteAsync(ObjectId.GenerateNewId());

        Assert.False(deleted);
    }

    [Fact]
    public async Task GetByIdAsync_ExistingRecord_ReturnsRecord()
    {
        var repo = CreateRepository();
        var inserted = await repo.InsertAsync(new NotificationSubscriptionRecord
        {
            EventType = "cookie_invalid",
            Platform = "test",
            GuildId = "guild1",
            ChannelId = "channel1",
        });

        var result = await repo.GetByIdAsync(inserted.Id);

        Assert.NotNull(result);
        Assert.Equal(inserted.Id, result!.Id);
    }

    [Fact]
    public async Task GetByIdAsync_NonExistingRecord_ReturnsNull()
    {
        var repo = CreateRepository();

        var result = await repo.GetByIdAsync(ObjectId.GenerateNewId());

        Assert.Null(result);
    }
}
