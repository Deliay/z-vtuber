using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Driver;
using Z.Vtuber.Persistence;
using Z.Vtuber.Persistence.Entities;

namespace Z.Vtuber.Tests;

[Collection("mongo")]
public class MongoDbInitializerTests(MongoDbFixture fixture) : IAsyncLifetime
{
    public Task InitializeAsync() => fixture.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private static string AuthorJson(long? pubTs) => pubTs is null
        ? """{"modules":{"module_author":{}}}"""
        : string.Format("""{{"modules":{{"module_author":{{"pub_ts":{0}}}}}}}""", pubTs);

    [Fact]
    public async Task Backfill_PopulatesPubTs_FromRawData()
    {
        var collection = fixture.Database.GetCollection<SpaceTimelineRecord>(MongoCollectionNames.SpaceTimeline);
        await collection.InsertOneAsync(new SpaceTimelineRecord { DynamicId = "1", Data = AuthorJson(1000) });
        await collection.InsertOneAsync(new SpaceTimelineRecord { DynamicId = "2", Data = AuthorJson(2000) });

        var initializer = new MongoDbInitializer(fixture.Database, NullLogger<MongoDbInitializer>.Instance);
        await initializer.BackfillSpaceTimelinePubTsAsync();

        var record1 = await collection.Find(r => r.DynamicId == "1").FirstOrDefaultAsync();
        var record2 = await collection.Find(r => r.DynamicId == "2").FirstOrDefaultAsync();
        Assert.Equal(1000, record1.PubTs);
        Assert.Equal(2000, record2.PubTs);
    }

    [Fact]
    public async Task Backfill_IsIdempotent()
    {
        var collection = fixture.Database.GetCollection<SpaceTimelineRecord>(MongoCollectionNames.SpaceTimeline);
        await collection.InsertOneAsync(new SpaceTimelineRecord { DynamicId = "1", Data = AuthorJson(1000) });

        var initializer = new MongoDbInitializer(fixture.Database, NullLogger<MongoDbInitializer>.Instance);
        await initializer.BackfillSpaceTimelinePubTsAsync();
        await initializer.BackfillSpaceTimelinePubTsAsync();

        var count = await collection.CountDocumentsAsync(FilterDefinition<SpaceTimelineRecord>.Empty);
        Assert.Equal(1, count);
        var record = await collection.Find(r => r.DynamicId == "1").FirstOrDefaultAsync();
        Assert.Equal(1000, record.PubTs);
    }

    [Fact]
    public async Task Backfill_LeavesMissingPubTs_Null()
    {
        var collection = fixture.Database.GetCollection<SpaceTimelineRecord>(MongoCollectionNames.SpaceTimeline);
        await collection.InsertOneAsync(new SpaceTimelineRecord { DynamicId = "1", Data = AuthorJson(null) });

        var initializer = new MongoDbInitializer(fixture.Database, NullLogger<MongoDbInitializer>.Instance);
        await initializer.BackfillSpaceTimelinePubTsAsync();

        var record = await collection.Find(r => r.DynamicId == "1").FirstOrDefaultAsync();
        Assert.Null(record.PubTs);
    }
}
