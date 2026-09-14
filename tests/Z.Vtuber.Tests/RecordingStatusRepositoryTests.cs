using MongoDB.Driver;
using Z.Vtuber.Common.Persistence;
using Z.Vtuber.Common.Persistence.Entities;

namespace Z.Vtuber.Tests;

public class RecordingStatusRepositoryTests(MongoDbFixture fixture) : IClassFixture<MongoDbFixture>, IAsyncLifetime
{
    public Task InitializeAsync() => fixture.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private RecordingStatusRepository Repository => new(fixture.Database);

    [Fact]
    public async Task Upsert_ThenGet_RoundTripsFields()
    {
        var repository = Repository;
        await repository.UpsertAsync(new RecordingStatusRecord
        {
            RoomId = 100,
            State = "recording",
            HostName = "host-1",
            SessionTitle = "晚间歌回",
            Files = ["a.flv", "b.flv"],
            UpdatedAt = DateTime.UtcNow,
        });

        var record = await repository.GetByRoomIdAsync(100);

        Assert.NotNull(record);
        Assert.Equal("recording", record!.State);
        Assert.Equal("host-1", record.HostName);
        Assert.Equal("晚间歌回", record.SessionTitle);
        Assert.Equal(["a.flv", "b.flv"], record.Files);
    }

    [Fact]
    public async Task Upsert_SameRoom_UpdatesExistingDocument()
    {
        var repository = Repository;
        await repository.UpsertAsync(new RecordingStatusRecord { RoomId = 200, State = "not_live", UpdatedAt = DateTime.UtcNow });
        await repository.UpsertAsync(new RecordingStatusRecord { RoomId = 200, State = "recording", UpdatedAt = DateTime.UtcNow });

        var all = await repository.GetAllAsync();
        var record = Assert.Single(all, r => r.RoomId == 200);
        Assert.Equal("recording", record.State);
    }

    [Fact]
    public async Task GetByRoomIds_ReturnsMatchingOnly()
    {
        var repository = Repository;
        await repository.UpsertAsync(new RecordingStatusRecord { RoomId = 301, State = "not_live", UpdatedAt = DateTime.UtcNow });
        await repository.UpsertAsync(new RecordingStatusRecord { RoomId = 302, State = "recording", UpdatedAt = DateTime.UtcNow });

        var records = await repository.GetByRoomIdsAsync([301]);

        var record = Assert.Single(records);
        Assert.Equal(301, record.RoomId);
    }

    [Fact]
    public async Task GetByRoomIds_EmptyInput_ReturnsEmpty()
    {
        var records = await Repository.GetByRoomIdsAsync([]);

        Assert.Empty(records);
    }

    [Fact]
    public async Task EnsureIndexes_IsIdempotent()
    {
        var initializer = new MongoDbInitializer(fixture.Database, Microsoft.Extensions.Logging.Abstractions.NullLogger<MongoDbInitializer>.Instance);

        await initializer.EnsureIndexesAsync();
        await initializer.EnsureIndexesAsync();

        var indexes = await fixture.Database
            .GetCollection<RecordingStatusRecord>(MongoCollectionNames.RecordingStatus)
            .Indexes.ListAsync();
        var list = await indexes.ToListAsync();
        Assert.Contains(list, i => i["name"].AsString == "RoomId_1");
    }
}
