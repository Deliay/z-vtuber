using MongoDB.Bson;
using MongoDB.Driver;
using Z.Vtuber.Common.Persistence;
using Z.Vtuber.Common.Persistence.Entities;

namespace Z.Vtuber.Tests;

/// <summary>
/// Plan 1.1：`TrainingProviderRepository` 直测（Testcontainers Mongo）——落库（Name trim/Type/Endpoint/ApiKey）、
/// `Name` 唯一索引重名抛 DuplicateKey、`ListAsync` CreatedAt 升序、`UpdateAsync` 部分字段、
/// `DeleteAsync` 命中/未命中、`GetByIdAsync`。
/// </summary>
public class TrainingProviderRepositoryTests(MongoDbFixture fixture) : IClassFixture<MongoDbFixture>, IAsyncLifetime
{
    public Task InitializeAsync() => fixture.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private TrainingProviderRepository Repository => new(fixture.Database);

    private static TrainingProviderRecord Provider(
        string name, string type = TrainingProviderTypes.OpenAi,
        string endpoint = "https://api.example.com/v1", string apiKey = "sk-secret",
        DateTime? createdAt = null)
    {
        var now = createdAt ?? DateTime.UtcNow;
        return new TrainingProviderRecord
        {
            Name = name,
            Type = type,
            Endpoint = endpoint,
            ApiKey = apiKey,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    [Fact]
    public async Task Insert_NewProvider_PersistsTrimmedNameAndFields()
    {
        var repository = Repository;

        var inserted = await repository.InsertAsync(
            Provider("  openai-prod  ", endpoint: "https://api.openai.com"));

        var record = Assert.Single(await repository.ListAsync());
        Assert.Equal(inserted.Id, record.Id);
        Assert.Equal("openai-prod", record.Name);
        Assert.Equal(TrainingProviderTypes.OpenAi, record.Type);
        Assert.Equal("https://api.openai.com", record.Endpoint);
        Assert.Equal("sk-secret", record.ApiKey);
        Assert.True(record.CreatedAt != default);
        Assert.True(record.UpdatedAt != default);
    }

    [Fact]
    public async Task Insert_DuplicateName_ThrowsDuplicateKey()
    {
        var repository = Repository;
        await repository.InsertAsync(Provider("same-name"));

        var ex = await Assert.ThrowsAsync<MongoWriteException>(
            () => repository.InsertAsync(Provider("same-name", type: TrainingProviderTypes.Claude)));

        Assert.Equal(ServerErrorCategory.DuplicateKey, ex.WriteError?.Category);
    }

    [Fact]
    public async Task List_OrderedByCreatedAtAscending()
    {
        var repository = Repository;
        var baseTime = new DateTime(2026, 9, 28, 0, 0, 0, DateTimeKind.Utc);

        await repository.InsertAsync(Provider("c", createdAt: baseTime.AddMinutes(2)));
        await repository.InsertAsync(Provider("a", createdAt: baseTime));
        await repository.InsertAsync(Provider("b", createdAt: baseTime.AddMinutes(1)));

        Assert.Equal(new[] { "a", "b", "c" },
            (await repository.ListAsync()).Select(p => p.Name).ToArray());
    }

    [Fact]
    public async Task Update_PartialFields_ReturnsUpdatedRecord()
    {
        var repository = Repository;
        var id = (await repository.InsertAsync(Provider("before"))).Id;

        var updated = await repository.UpdateAsync(id,
            Builders<TrainingProviderRecord>.Update
                .Set(p => p.Name, "after")
                .Set(p => p.Endpoint, "https://new.example.com"));

        Assert.NotNull(updated);
        Assert.Equal("after", updated!.Name);
        Assert.Equal("https://new.example.com", updated.Endpoint);
        // 未更新的字段保持原值
        Assert.Equal(TrainingProviderTypes.OpenAi, updated.Type);
        Assert.Equal("sk-secret", updated.ApiKey);
    }

    [Fact]
    public async Task Update_UnknownId_ReturnsNull()
    {
        var repository = Repository;
        await repository.InsertAsync(Provider("keep"));

        var updated = await repository.UpdateAsync(
            ObjectId.GenerateNewId(), Builders<TrainingProviderRecord>.Update.Set(p => p.Name, "x"));

        Assert.Null(updated);
        Assert.Equal("keep", (await repository.ListAsync()).Single().Name);
    }

    [Fact]
    public async Task Delete_Existing_ReturnsTrueAndRemovesRecord()
    {
        var repository = Repository;
        var keep = await repository.InsertAsync(Provider("keep"));
        var remove = await repository.InsertAsync(Provider("remove"));

        Assert.True(await repository.DeleteAsync(remove.Id));
        var rest = Assert.Single(await repository.ListAsync());
        Assert.Equal(keep.Id, rest.Id);
    }

    [Fact]
    public async Task Delete_UnknownId_ReturnsFalse()
    {
        var repository = Repository;
        await repository.InsertAsync(Provider("only"));

        Assert.False(await repository.DeleteAsync(ObjectId.GenerateNewId()));
        Assert.Single(await repository.ListAsync());
    }

    [Fact]
    public async Task GetById_ExistingAndUnknown()
    {
        var repository = Repository;
        var inserted = await repository.InsertAsync(Provider("single"));

        var loaded = await repository.GetByIdAsync(inserted.Id);
        Assert.NotNull(loaded);
        Assert.Equal("single", loaded!.Name);
        Assert.Null(await repository.GetByIdAsync(ObjectId.GenerateNewId()));
    }
}
