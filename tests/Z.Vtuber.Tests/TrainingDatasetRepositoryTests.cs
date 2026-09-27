using MongoDB.Bson;
using MongoDB.Driver;
using Z.Vtuber.Common.Persistence;
using Z.Vtuber.Common.Persistence.Entities;

namespace Z.Vtuber.Tests;

/// <summary>
/// Plan 1.1：`TrainingDatasetRepository` 直测（Testcontainers Mongo）——落库（Name/Type）、
/// `(Type, Name)` 唯一索引重名抛 DuplicateKey、`ListAsync(type)` 过滤与 CreatedAt 升序、
/// `RenameAsync` 命中/未命中/改重名、`DeleteAsync` 命中/未命中。
/// </summary>
public class TrainingDatasetRepositoryTests(MongoDbFixture fixture) : IClassFixture<MongoDbFixture>, IAsyncLifetime
{
    private const string AudioType = "audio";

    public Task InitializeAsync() => fixture.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private TrainingDatasetRepository Repository => new(fixture.Database);

    private static TrainingDatasetRecord Dataset(
        string name, string type = AudioType, DateTime? createdAt = null)
    {
        var now = createdAt ?? DateTime.UtcNow;
        return new TrainingDatasetRecord
        {
            Name = name,
            Type = type,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    [Fact]
    public async Task Insert_NewDataset_PersistsTrimmedNameAndType()
    {
        var repository = Repository;

        var inserted = await repository.InsertAsync(Dataset("  白石麻衣  "));

        Assert.True(inserted.Id != ObjectId.Empty);
        var stored = await repository.ListAsync(AudioType);
        var record = Assert.Single(stored);
        Assert.Equal(inserted.Id, record.Id);
        // Name trim：落库为 trim 后的值（API 层已 trim，仓储兜底），Type 原样落库
        Assert.Equal("白石麻衣", record.Name);
        Assert.Equal(AudioType, record.Type);
        Assert.True(record.CreatedAt != default);
        Assert.True(record.UpdatedAt != default);
    }

    [Fact]
    public async Task Insert_DuplicateTypeAndName_ThrowsDuplicateKey()
    {
        var repository = Repository;
        await repository.InsertAsync(Dataset("同名数据集"));

        var ex = await Assert.ThrowsAsync<MongoWriteException>(
            () => repository.InsertAsync(Dataset("同名数据集")));

        Assert.Equal(ServerErrorCategory.DuplicateKey, ex.WriteError?.Category);
    }

    [Fact]
    public async Task Insert_SameNameDifferentType_IsAllowed()
    {
        // 唯一索引口径为 (Type, Name)：不同类型可同名
        var repository = Repository;
        await repository.InsertAsync(Dataset("名字", type: AudioType));
        await repository.InsertAsync(Dataset("名字", type: "text"));

        Assert.Equal(2, (await repository.ListAsync(AudioType)).Count + (await repository.ListAsync("text")).Count);
    }

    [Fact]
    public async Task List_FiltersByType_OrderedByCreatedAtAscending()
    {
        var repository = Repository;
        var baseTime = new DateTime(2026, 9, 27, 0, 0, 0, DateTimeKind.Utc);

        // 逆序插入，验证按 CreatedAt 升序返回
        await repository.InsertAsync(Dataset("c", createdAt: baseTime.AddMinutes(2)));
        await repository.InsertAsync(Dataset("a", createdAt: baseTime));
        await repository.InsertAsync(Dataset("b", createdAt: baseTime.AddMinutes(1)));
        await repository.InsertAsync(Dataset("其他类型", type: "text", createdAt: baseTime.AddMinutes(3)));

        var items = await repository.ListAsync(AudioType);

        Assert.Equal(new[] { "a", "b", "c" }, items.Select(d => d.Name).ToArray());
        Assert.All(items, d => Assert.Equal(AudioType, d.Type));
    }

    [Fact]
    public async Task List_UnknownType_ReturnsEmpty()
    {
        var repository = Repository;
        await repository.InsertAsync(Dataset("only-audio"));

        Assert.Empty(await repository.ListAsync("text"));
        Assert.Empty(await repository.ListAsync(string.Empty));
    }

    [Fact]
    public async Task Rename_Hit_UpdatesNameAndUpdatedAt()
    {
        var repository = Repository;
        var id = (await repository.InsertAsync(Dataset("旧名字"))).Id;
        var before = (await repository.ListAsync(AudioType)).Single().UpdatedAt;

        await Task.Delay(15);
        var renamed = await repository.RenameAsync(id, "  新名字  ");

        Assert.True(renamed);
        var record = (await repository.ListAsync(AudioType)).Single();
        // 改名同样落库 trim 后的值
        Assert.Equal("新名字", record.Name);
        Assert.True(record.UpdatedAt > before);
    }

    [Fact]
    public async Task Rename_UnknownId_ReturnsFalseAndWritesNothing()
    {
        var repository = Repository;
        await repository.InsertAsync(Dataset("保持不变"));

        var renamed = await repository.RenameAsync(ObjectId.GenerateNewId(), "改名结果");

        Assert.False(renamed);
        Assert.Equal("保持不变", (await repository.ListAsync(AudioType)).Single().Name);
    }

    [Fact]
    public async Task Rename_ToExistingName_ThrowsDuplicateKey()
    {
        var repository = Repository;
        await repository.InsertAsync(Dataset("占用名字"));
        var otherId = (await repository.InsertAsync(Dataset("另一个"))).Id;

        var ex = await Assert.ThrowsAsync<MongoWriteException>(
            () => repository.RenameAsync(otherId, "占用名字"));

        Assert.Equal(ServerErrorCategory.DuplicateKey, ex.WriteError?.Category);
        // 重名失败后原名保持不变
        Assert.Equal("另一个", (await repository.ListAsync(AudioType)).Single(d => d.Id == otherId).Name);
    }

    [Fact]
    public async Task Delete_Existing_ReturnsTrueAndRemovesRecord()
    {
        var repository = Repository;
        var keep = await repository.InsertAsync(Dataset("保留"));
        var remove = await repository.InsertAsync(Dataset("删除"));

        var deleted = await repository.DeleteAsync(remove.Id);

        Assert.True(deleted);
        var rest = await repository.ListAsync(AudioType);
        Assert.Single(rest);
        Assert.Equal(keep.Id, rest[0].Id);
    }

    [Fact]
    public async Task Delete_UnknownId_ReturnsFalse()
    {
        var repository = Repository;
        await repository.InsertAsync(Dataset("唯一"));

        var deleted = await repository.DeleteAsync(ObjectId.GenerateNewId());

        Assert.False(deleted);
        Assert.Single(await repository.ListAsync(AudioType));
    }
}
