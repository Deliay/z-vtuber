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
            GroupId = "123456",
            GroupName = "运维群",
        };

        var inserted = await repo.InsertAsync(record);

        Assert.NotEqual(ObjectId.Empty, inserted.Id);
        Assert.Equal("cookie_invalid", inserted.EventType);
        Assert.Equal("123456", inserted.GroupId);
        Assert.Equal("运维群", inserted.GroupName);
    }

    [Fact]
    public async Task ListAsync_ReturnsAllRecords()
    {
        var repo = CreateRepository();
        await repo.InsertAsync(new NotificationSubscriptionRecord
        {
            EventType = "cookie_invalid",
            GroupId = "123456",
            GroupName = "运维群",
        });
        await repo.InsertAsync(new NotificationSubscriptionRecord
        {
            EventType = "cookie_invalid",
            GroupId = "789012",
            GroupName = "测试群",
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
            GroupId = "123456",
            GroupName = "运维群",
        });
        await repo.InsertAsync(new NotificationSubscriptionRecord
        {
            EventType = "other_type",
            GroupId = "789012",
            GroupName = "测试群",
        });

        var results = await repo.GetByEventTypeAsync("cookie_invalid");

        Assert.Single(results);
        Assert.Equal("123456", results[0].GroupId);
    }

    [Fact]
    public async Task DeleteAsync_ExistingRecord_ReturnsTrue()
    {
        var repo = CreateRepository();
        var inserted = await repo.InsertAsync(new NotificationSubscriptionRecord
        {
            EventType = "cookie_invalid",
            GroupId = "123456",
            GroupName = "运维群",
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
            GroupId = "123456",
            GroupName = "运维群",
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

    [Fact]
    public async Task InsertAsync_DuplicateEventTypeAndGroupId_ThrowsMongoWriteException()
    {
        var repo = CreateRepository();
        await repo.InsertAsync(new NotificationSubscriptionRecord
        {
            EventType = "cookie_invalid",
            GroupId = "123456",
            GroupName = "运维群",
        });

        var duplicate = new NotificationSubscriptionRecord
        {
            EventType = "cookie_invalid",
            GroupId = "123456",
            GroupName = "运维群",
        };

        await Assert.ThrowsAsync<MongoWriteException>(() => repo.InsertAsync(duplicate));
    }

    [Fact]
    public async Task InsertAsync_GroupIdAndGroupName_StoredAsSnakeCase()
    {
        var repo = CreateRepository();
        var record = new NotificationSubscriptionRecord
        {
            EventType = "cookie_invalid",
            GroupId = "123456",
            GroupName = "运维群",
        };

        var inserted = await repo.InsertAsync(record);

        // 验证 BSON 字段名
        var collection = fixture.Database.GetCollection<BsonDocument>("notification_subscriptions");
        var bson = await collection.Find(new BsonDocument("_id", inserted.Id)).FirstAsync();
        Assert.True(bson.Contains("group_id"));
        Assert.True(bson.Contains("group_name"));
        Assert.Equal("123456", bson["group_id"].AsString);
        Assert.Equal("运维群", bson["group_name"].AsString);
    }
}