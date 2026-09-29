using MongoDB.Bson;
using MongoDB.Driver;
using Z.Vtuber.Common.Persistence;
using Z.Vtuber.Common.Persistence.Entities;

namespace Z.Vtuber.Tests;

/// <summary>
/// Plan 1.2：`TrainingModelRepository` 直测（Testcontainers Mongo）——`(ProviderId, Model)` 唯一索引、
/// 批量导入去重计数、`ListAsync(enabled)` 过滤、改名撞唯一索引、`DeleteByProviderAsync` 级联计数。
/// </summary>
public class TrainingModelRepositoryTests(MongoDbFixture fixture) : IClassFixture<MongoDbFixture>, IAsyncLifetime
{
    public Task InitializeAsync() => fixture.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private TrainingModelRepository Repository => new(fixture.Database);

    private static TrainingModelRecord Model(
        ObjectId providerId, string model, bool enabled = true, DateTime? createdAt = null)
    {
        var now = createdAt ?? DateTime.UtcNow;
        return new TrainingModelRecord
        {
            ProviderId = providerId,
            Model = model,
            Enabled = enabled,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    [Fact]
    public async Task Insert_DuplicateProviderAndModel_ThrowsDuplicateKey()
    {
        var repository = Repository;
        var providerId = ObjectId.GenerateNewId();
        await repository.InsertAsync(Model(providerId, "gpt-4o"));

        var ex = await Assert.ThrowsAsync<MongoWriteException>(
            () => repository.InsertAsync(Model(providerId, "gpt-4o")));

        Assert.Equal(ServerErrorCategory.DuplicateKey, ex.WriteError?.Category);
    }

    [Fact]
    public async Task Insert_SameModelDifferentProvider_IsAllowed()
    {
        var repository = Repository;
        await repository.InsertAsync(Model(ObjectId.GenerateNewId(), "gpt-4o"));
        await repository.InsertAsync(Model(ObjectId.GenerateNewId(), "gpt-4o"));

        Assert.Equal(2, (await repository.ListAsync()).Count);
    }

    [Fact]
    public async Task List_FiltersByEnabled()
    {
        var repository = Repository;
        var providerId = ObjectId.GenerateNewId();
        await repository.InsertAsync(Model(providerId, "on-1", enabled: true));
        await repository.InsertAsync(Model(providerId, "off-1", enabled: false));
        await repository.InsertAsync(Model(providerId, "on-2", enabled: true));

        Assert.Equal(new[] { "on-1", "on-2" },
            (await repository.ListAsync(enabled: true)).Select(m => m.Model).ToArray());
        Assert.Equal(new[] { "off-1" },
            (await repository.ListAsync(enabled: false)).Select(m => m.Model).ToArray());
        Assert.Equal(3, (await repository.ListAsync()).Count);
    }

    [Fact]
    public async Task InsertManyIgnoreDuplicates_CountsFreshDuplicatesAndMixed()
    {
        var repository = Repository;
        var providerId = ObjectId.GenerateNewId();

        // 全新
        var fresh = await repository.InsertManyIgnoreDuplicatesAsync(
            new[] { Model(providerId, "a"), Model(providerId, "b") });
        Assert.Equal((2L, 0L), fresh);

        // 全跳过
        var allSkipped = await repository.InsertManyIgnoreDuplicatesAsync(
            new[] { Model(providerId, "a"), Model(providerId, "b") });
        Assert.Equal(0L, allSkipped.Imported);
        Assert.Equal(2L, allSkipped.Skipped);

        // 混合：c 新、a 重
        var mixed = await repository.InsertManyIgnoreDuplicatesAsync(
            new[] { Model(providerId, "a"), Model(providerId, "c") });
        Assert.Equal(1L, mixed.Imported);
        Assert.Equal(1L, mixed.Skipped);
        // imported + skipped = 提交数
        Assert.Equal(2L, mixed.Imported + mixed.Skipped);
        Assert.Equal(3, (await repository.ListAsync()).Count);
    }

    [Fact]
    public async Task InsertManyIgnoreDuplicates_Empty_ReturnsZeros()
    {
        Assert.Equal((0L, 0L), await Repository.InsertManyIgnoreDuplicatesAsync([]));
    }

    [Fact]
    public async Task Update_Rename_TouchingUniqueIndex_ThrowsDuplicateKey()
    {
        var repository = Repository;
        var providerId = ObjectId.GenerateNewId();
        await repository.InsertAsync(Model(providerId, "taken"));
        var other = await repository.InsertAsync(Model(providerId, "other"));

        var ex = await Assert.ThrowsAsync<MongoWriteException>(
            () => repository.UpdateAsync(other.Id,
                Builders<TrainingModelRecord>.Update.Set(m => m.Model, "taken")));

        Assert.Equal(ServerErrorCategory.DuplicateKey, ex.WriteError?.Category);
    }

    [Fact]
    public async Task Update_ToggleEnabled_ReturnsUpdatedRecord()
    {
        var repository = Repository;
        var id = (await repository.InsertAsync(Model(ObjectId.GenerateNewId(), "m"))).Id;

        var updated = await repository.UpdateAsync(
            id, Builders<TrainingModelRecord>.Update.Set(m => m.Enabled, false));

        Assert.NotNull(updated);
        Assert.False(updated!.Enabled);
        Assert.Single(await repository.ListAsync(enabled: false));
    }

    [Fact]
    public async Task DeleteByProvider_RemovesOnlyThatProvidersModels()
    {
        var repository = Repository;
        var target = ObjectId.GenerateNewId();
        var other = ObjectId.GenerateNewId();
        await repository.InsertAsync(Model(target, "t1"));
        await repository.InsertAsync(Model(target, "t2"));
        await repository.InsertAsync(Model(other, "o1"));

        var deleted = await repository.DeleteByProviderAsync(target);

        Assert.Equal(2, deleted);
        var rest = Assert.Single(await repository.ListAsync());
        Assert.Equal("o1", rest.Model);
    }
}
