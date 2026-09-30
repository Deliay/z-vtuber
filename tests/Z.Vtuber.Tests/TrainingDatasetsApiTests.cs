using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using Z.Vtuber.Common.Persistence;
using Z.Vtuber.Common.Persistence.Entities;
using Z.Vtuber.Training.Contracts;
using Z.Vtuber.Training.Controllers;

namespace Z.Vtuber.Tests;

/// <summary>
/// Plan 1.4：`TrainingDatasetsController` 控制器直测（Testcontainers Mongo）——
/// 新建 201 / 重名 409 `dataset name exists` / 名字空与超 128 400 / `type` 非 `audio` 400、
/// 列表 `type` 过滤与 CreatedAt 升序、重命名 200/409/404、删除 204 且条目级联清空与 404。
/// </summary>
public class TrainingDatasetsApiTests(MongoDbFixture fixture) : IClassFixture<MongoDbFixture>, IAsyncLifetime
{
    private const string AudioType = "audio";

    public Task InitializeAsync() => fixture.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private TrainingDatasetRepository Datasets => new(fixture.Database);

    private TrainingDatasetItemRepository Items => new(fixture.Database);

    private TrainingDatasetsController CreateController() =>
        WithContext(new TrainingDatasetsController(
            Datasets, Items, NullLogger<TrainingDatasetsController>.Instance));

    private static T WithContext<T>(T controller) where T : ControllerBase
    {
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
        return controller;
    }

    /// <summary>取 <c>ActionResult&lt;T&gt;.Result</c> 的 <see cref="ObjectResult"/> 并断言载荷类型。</summary>
    private static T PayloadOf<T>(ActionResult<T> result)
    {
        var objectResult = Assert.IsAssignableFrom<ObjectResult>(result.Result);
        return Assert.IsType<T>(objectResult.Value);
    }

    private static ObjectResult ErrorOf<T>(ActionResult<T> result) =>
        Assert.IsAssignableFrom<ObjectResult>(result.Result);

    private static string ErrorOf<T>(ActionResult<T> result, int statusCode)
    {
        var objectResult = ErrorOf(result);
        Assert.Equal(statusCode, objectResult.StatusCode);
        return Assert.IsType<ErrorResponse>(objectResult.Value).Error;
    }

    private async Task<TrainingDatasetRecord> SeedDatasetAsync(
        string name, string type = AudioType, DateTime? createdAt = null)
    {
        var now = createdAt ?? DateTime.UtcNow;
        return await Datasets.InsertAsync(new TrainingDatasetRecord
        {
            Name = name,
            Type = type,
            CreatedAt = now,
            UpdatedAt = now,
        });
    }

    private async Task<TrainingDatasetItemRecord> SeedItemAsync(ObjectId datasetId, string audioKey)
    {
        var now = DateTime.UtcNow;
        var record = new TrainingDatasetItemRecord
        {
            DatasetId = datasetId,
            AudioType = "transcript",
            Source = "task-1",
            AudioUri = "b/live/700001/recordings/a.flv",
            AudioKey = audioKey,
            StartMs = 0,
            EndMs = 30000,
            Text = "句子",
            CreatedAt = now,
            UpdatedAt = now,
        };
        await Items.InsertManyIgnoreDuplicatesAsync([record]);
        return record;
    }

    // ---- 新建（接口 2）----

    [Fact]
    public async Task Create_WithAudioType_Returns201AndPersists()
    {
        var result = await CreateController()
            .Create(new TrainingDatasetCreateRequest("  白石麻衣  ", AudioType));

        var objectResult = Assert.IsAssignableFrom<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status201Created, objectResult.StatusCode);
        var dto = Assert.IsType<TrainingDatasetDto>(objectResult.Value);
        // 落库与回包均为 trim 后的名字
        Assert.Equal("白石麻衣", dto.Name);
        Assert.Equal(AudioType, dto.Type);
        Assert.NotEqual(ObjectId.Empty.ToString(), dto.Id);
        Assert.True(dto.CreatedAt != default);
        Assert.True(dto.UpdatedAt != default);

        var stored = Assert.Single(await Datasets.ListAsync(AudioType));
        Assert.Equal(dto.Id, stored.Id.ToString());
        Assert.Equal("白石麻衣", stored.Name);
        Assert.Equal(AudioType, stored.Type);
    }

    [Fact]
    public async Task Create_DuplicateName_Returns409DatasetNameExists()
    {
        var controller = CreateController();
        await controller.Create(new TrainingDatasetCreateRequest("同名数据集", AudioType));

        var result = await controller
            .Create(new TrainingDatasetCreateRequest("  同名数据集 ", AudioType));

        Assert.Equal("dataset name exists", ErrorOf(result, StatusCodes.Status409Conflict));
        Assert.Single(await Datasets.ListAsync(AudioType));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Create_BlankName_Returns400NameIsRequired(string? name)
    {
        var result = await CreateController()
            .Create(new TrainingDatasetCreateRequest(name, AudioType));

        Assert.Equal("name is required", ErrorOf(result, StatusCodes.Status400BadRequest));
        Assert.Empty(await Datasets.ListAsync(AudioType));
    }

    [Fact]
    public async Task Create_NameTooLong_Returns400NameTooLong()
    {
        var result = await CreateController()
            .Create(new TrainingDatasetCreateRequest(new string('名', 129), AudioType));

        Assert.Equal("name too long", ErrorOf(result, StatusCodes.Status400BadRequest));
        Assert.Empty(await Datasets.ListAsync(AudioType));

        // 边界：恰好 128 字符可创建
        var boundary = await CreateController()
            .Create(new TrainingDatasetCreateRequest(new string('名', 128), AudioType));
        Assert.Equal(StatusCodes.Status201Created,
            Assert.IsAssignableFrom<ObjectResult>(boundary.Result).StatusCode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("text")]
    [InlineData("AUDIO")]
    public async Task Create_UnsupportedType_Returns400(string? type)
    {
        var result = await CreateController()
            .Create(new TrainingDatasetCreateRequest("音频集", type));

        Assert.Equal("unsupported dataset type", ErrorOf(result, StatusCodes.Status400BadRequest));
        Assert.Empty(await Datasets.ListAsync(AudioType));
    }

    // ---- 列表（接口 1）----

    [Fact]
    public async Task List_FiltersByTypeAndOrdersByCreatedAtAscending()
    {
        var baseTime = new DateTime(2026, 9, 27, 0, 0, 0, DateTimeKind.Utc);
        await SeedDatasetAsync("c", createdAt: baseTime.AddMinutes(2));
        await SeedDatasetAsync("a", createdAt: baseTime);
        await SeedDatasetAsync("b", createdAt: baseTime.AddMinutes(1));
        await SeedDatasetAsync("其他类型", type: "text", createdAt: baseTime.AddMinutes(3));

        var list = PayloadOf(await CreateController().List(AudioType));

        Assert.Equal(new[] { "a", "b", "c" }, list.Items.Select(d => d.Name).ToArray());
        Assert.All(list.Items, d => Assert.Equal(AudioType, d.Type));
        Assert.All(list.Items, d => Assert.NotEqual(ObjectId.Empty.ToString(), d.Id));
        Assert.Equal(
            new[] { baseTime, baseTime.AddMinutes(1), baseTime.AddMinutes(2) },
            list.Items.Select(d => d.CreatedAt).ToArray());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("text")]
    public async Task List_UnsupportedType_Returns400(string? type)
    {
        await SeedDatasetAsync("音频集");

        var result = await CreateController().List(type);

        Assert.Equal("unsupported dataset type", ErrorOf(result, StatusCodes.Status400BadRequest));
    }

    // ---- 重命名（接口 3）----

    [Fact]
    public async Task Rename_Existing_Returns200WithUpdatedName()
    {
        var dataset = await SeedDatasetAsync("旧名字");
        await TestWait.UntilAsync(() => DateTime.UtcNow >= dataset.CreatedAt.AddMilliseconds(2), TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(2));

        var result = await CreateController()
            .Rename(dataset.Id.ToString(), new TrainingDatasetRenameRequest("  新名字  "));

        var dto = PayloadOf(result);
        Assert.Equal(dataset.Id.ToString(), dto.Id);
        Assert.Equal("新名字", dto.Name);
        Assert.Equal(AudioType, dto.Type);

        var stored = Assert.Single(await Datasets.ListAsync(AudioType));
        Assert.Equal("新名字", stored.Name);
        Assert.True(stored.UpdatedAt > stored.CreatedAt);
        Assert.Equal(stored.UpdatedAt, dto.UpdatedAt);
    }

    [Fact]
    public async Task Rename_DuplicateName_Returns409DatasetNameExists()
    {
        await SeedDatasetAsync("占用名字");
        var other = await SeedDatasetAsync("另一个");

        var result = await CreateController()
            .Rename(other.Id.ToString(), new TrainingDatasetRenameRequest("占用名字"));

        Assert.Equal("dataset name exists", ErrorOf(result, StatusCodes.Status409Conflict));
        // 重名失败后原名保持不变
        Assert.Equal("另一个", (await Datasets.ListAsync(AudioType)).Single(d => d.Id == other.Id).Name);
    }

    [Fact]
    public async Task Rename_UnknownOrInvalidId_Returns404()
    {
        var controller = CreateController();

        var unknown = await controller.Rename(
            ObjectId.GenerateNewId().ToString(), new TrainingDatasetRenameRequest("改名结果"));
        Assert.Equal("dataset not found", ErrorOf(unknown, StatusCodes.Status404NotFound));

        var invalid = await controller.Rename(
            "not-an-objectid", new TrainingDatasetRenameRequest("改名结果"));
        Assert.Equal("dataset not found", ErrorOf(invalid, StatusCodes.Status404NotFound));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Rename_BlankName_Returns400NameIsRequired(string? name)
    {
        var dataset = await SeedDatasetAsync("旧名字");

        var result = await CreateController()
            .Rename(dataset.Id.ToString(), new TrainingDatasetRenameRequest(name));

        Assert.Equal("name is required", ErrorOf(result, StatusCodes.Status400BadRequest));
        Assert.Equal("旧名字", (await Datasets.ListAsync(AudioType)).Single().Name);
    }

    [Fact]
    public async Task Rename_NameTooLong_Returns400NameTooLong()
    {
        var dataset = await SeedDatasetAsync("旧名字");

        var result = await CreateController()
            .Rename(dataset.Id.ToString(), new TrainingDatasetRenameRequest(new string('名', 129)));

        Assert.Equal("name too long", ErrorOf(result, StatusCodes.Status400BadRequest));
        Assert.Equal("旧名字", (await Datasets.ListAsync(AudioType)).Single().Name);
    }

    // ---- 删除（接口 4）----

    [Fact]
    public async Task Delete_Existing_Returns204AndClearsItems()
    {
        var dataset = await SeedDatasetAsync("待删除");
        await SeedItemAsync(dataset.Id, "transcript:task-1:0");
        await SeedItemAsync(dataset.Id, "transcript:task-1:1000");
        var other = await SeedDatasetAsync("保留");
        await SeedItemAsync(other.Id, "transcript:task-1:0");

        var result = await CreateController().Delete(dataset.Id.ToString());

        Assert.IsType<NoContentResult>(result);
        // 条目级联清空，其他数据集不受影响
        Assert.Empty(await Items.ListAsync(dataset.Id));
        Assert.Single(await Items.ListAsync(other.Id));
        var rest = await Datasets.ListAsync(AudioType);
        Assert.Equal(other.Id, Assert.Single(rest).Id);
    }

    [Fact]
    public async Task Delete_UnknownOrInvalidId_Returns404()
    {
        var dataset = await SeedDatasetAsync("保留");
        await SeedItemAsync(dataset.Id, "transcript:task-1:0");
        var controller = CreateController();

        var unknown = await controller.Delete(ObjectId.GenerateNewId().ToString());
        Assert.IsType<NotFoundObjectResult>(unknown);
        Assert.Equal("dataset not found", Assert.IsType<ErrorResponse>(
            Assert.IsType<NotFoundObjectResult>(unknown).Value).Error);

        var invalid = await controller.Delete("not-an-objectid");
        Assert.IsType<NotFoundObjectResult>(invalid);

        Assert.Single(await Datasets.ListAsync(AudioType));
        Assert.Single(await Items.ListAsync(dataset.Id));
    }
}
