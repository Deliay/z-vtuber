using MongoDB.Bson;
using MongoDB.Driver;
using Z.Vtuber.Common.Persistence;
using Z.Vtuber.Common.Persistence.Entities;

namespace Z.Vtuber.Tests;

/// <summary>
/// Plan 2.3：<see cref="TrainingFeaturePointRepository"/> 直测（Testcontainers Mongo）——
/// 登记 upsert（SetOnInsert CreatedAt、更新 Name/能力要求、保留指定）、Slug 唯一索引、SetModel 指定/置空。
/// </summary>
public class TrainingFeaturePointRepositoryTests(MongoDbFixture fixture)
    : IClassFixture<MongoDbFixture>, IAsyncLifetime
{
    public Task InitializeAsync() => fixture.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private TrainingFeaturePointRepository Repository => new(fixture.Database);

    private static TrainingFeaturePointRecord Record(
        string slug, string name, string[] input, string[] output) =>
        new()
        {
            Slug = slug,
            Name = name,
            InputCapabilities = input,
            OutputCapabilities = output,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

    [Fact]
    public async Task Register_FirstTime_InsertsAndListsBySlugAscending()
    {
        var repo = Repository;
        await repo.RegisterAsync(Record("zeta", "Z", ["text"], ["text"]));
        await repo.RegisterAsync(Record("alpha", "A", ["audio"], ["text"]));

        var list = await repo.ListAsync();
        Assert.Equal(new[] { "alpha", "zeta" }, list.Select(f => f.Slug).ToArray());
    }

    [Fact]
    public async Task Register_Again_UpdatesNameAndCapabilities_KeepsCreatedAtAndModelId()
    {
        var repo = Repository;
        var registered = await repo.RegisterAsync(Record("fp", "旧名", ["text"], ["text"]));
        var modelId = ObjectId.GenerateNewId();
        await repo.SetModelAsync("fp", modelId, DateTime.UtcNow);

        var updated = await repo.RegisterAsync(Record("fp", "新名", ["audio", "text"], ["text"]));

        Assert.Equal("新名", updated.Name);
        Assert.Equal(["audio", "text"], updated.InputCapabilities);
        Assert.True(updated.ModelId == modelId); // 指定不被登记覆盖
        Assert.Equal(registered.CreatedAt, updated.CreatedAt);
    }

    [Fact]
    public async Task Register_DuplicateSlug_DoesNotInsertSecondDocument()
    {
        var repo = Repository;
        await repo.RegisterAsync(Record("fp", "A", ["text"], ["text"]));
        await repo.RegisterAsync(Record("fp", "B", ["audio"], ["text"]));

        Assert.Single(await repo.ListAsync());
    }

    [Fact]
    public async Task SetModel_Null_ClearsSpecified()
    {
        var repo = Repository;
        await repo.RegisterAsync(Record("fp", "A", ["text"], ["text"]));
        await repo.SetModelAsync("fp", ObjectId.GenerateNewId(), DateTime.UtcNow);

        var cleared = await repo.SetModelAsync("fp", null, DateTime.UtcNow);

        Assert.NotNull(cleared);
        Assert.Null(cleared!.ModelId);
    }

    [Fact]
    public async Task SetModel_UnknownSlug_ReturnsNull()
    {
        Assert.Null(await Repository.SetModelAsync("missing", ObjectId.GenerateNewId(), DateTime.UtcNow));
    }

    [Fact]
    public async Task GetBySlug_Unknown_ReturnsNull()
    {
        Assert.Null(await Repository.GetBySlugAsync("missing"));
    }
}
