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
/// Plan 1.10：<see cref="TrainingModelsController"/> 控制器直测（Testcontainers Mongo）——
/// 列表 <c>enabled</c> 过滤与 <c>slug == "{providerName}/{model}"</c>、手动添加（Provider 404 / model 空超长 400 / 重复 409）、
/// 更新启停/改名（409）、删除 204/404。
/// </summary>
public class TrainingModelApiTests(MongoDbFixture fixture) : IClassFixture<MongoDbFixture>, IAsyncLifetime
{
    public Task InitializeAsync() => fixture.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private TrainingModelRepository Models => new(fixture.Database);

    private TrainingProviderRepository Providers => new(fixture.Database);

    private TrainingModelsController CreateController() =>
        WithContext(new TrainingModelsController(
            Models, Providers, NullLogger<TrainingModelsController>.Instance));

    private static T WithContext<T>(T controller) where T : ControllerBase
    {
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
        return controller;
    }

    private static T PayloadOf<T>(ActionResult<T> result)
    {
        var objectResult = Assert.IsAssignableFrom<ObjectResult>(result.Result);
        return Assert.IsType<T>(objectResult.Value);
    }

    private static string ErrorOf<T>(ActionResult<T> result, int statusCode)
    {
        var objectResult = Assert.IsAssignableFrom<ObjectResult>(result.Result);
        Assert.Equal(statusCode, objectResult.StatusCode);
        return Assert.IsType<ErrorResponse>(objectResult.Value).Error;
    }

    private static string ErrorOfResult(ActionResult result, int statusCode)
    {
        var objectResult = Assert.IsAssignableFrom<ObjectResult>(result);
        Assert.Equal(statusCode, objectResult.StatusCode);
        return Assert.IsType<ErrorResponse>(objectResult.Value).Error;
    }

    private async Task<TrainingProviderRecord> SeedProviderAsync(string name = "openai-prod")
    {
        var now = DateTime.UtcNow;
        return await Providers.InsertAsync(new TrainingProviderRecord
        {
            Name = name, Type = "openai", Endpoint = "http://llm", ApiKey = "k",
            CreatedAt = now, UpdatedAt = now,
        });
    }

    private async Task<TrainingModelRecord> SeedModelAsync(
        ObjectId providerId, string model, bool enabled = true, DateTime? createdAt = null)
    {
        var now = createdAt ?? DateTime.UtcNow;
        return await Models.InsertAsync(new TrainingModelRecord
        {
            ProviderId = providerId, Model = model, Enabled = enabled,
            CreatedAt = now, UpdatedAt = now,
        });
    }

    // ---- 列表（接口 7）----

    [Fact]
    public async Task List_BuildsSlugFromProviderName_AndFiltersByEnabled()
    {
        var provider = await SeedProviderAsync("openai-prod");
        var baseTime = new DateTime(2026, 9, 27, 0, 0, 0, DateTimeKind.Utc);
        var enabled = await SeedModelAsync(provider.Id, "gpt-4o", createdAt: baseTime);
        await SeedModelAsync(provider.Id, "gpt-4o-mini", enabled: false, createdAt: baseTime.AddMinutes(1));

        var all = PayloadOf(await CreateController().List(null, null, null));
        Assert.Equal(2, all.Items.Count);
        Assert.Equal($"{provider.Name}/{enabled.Model}", all.Items[0].Slug);
        Assert.Equal("openai-prod/gpt-4o", all.Items[0].Slug);
        Assert.Equal(provider.Id.ToString(), all.Items[0].ProviderId);
        Assert.Equal("openai-prod", all.Items[0].ProviderName);

        var enabledOnly = PayloadOf(await CreateController().List(true, null, null));
        Assert.Equal("openai-prod/gpt-4o", Assert.Single(enabledOnly.Items).Slug);

        var disabledOnly = PayloadOf(await CreateController().List(false, null, null));
        Assert.Equal("openai-prod/gpt-4o-mini", Assert.Single(disabledOnly.Items).Slug);
    }

    [Fact]
    public async Task List_ModelWithMissingProvider_UsesEmptyProviderName()
    {
        var orphan = await SeedModelAsync(ObjectId.GenerateNewId(), "orphan-model");

        var list = PayloadOf(await CreateController().List(null, null, null));

        var dto = Assert.Single(list.Items);
        Assert.Equal(orphan.Id.ToString(), dto.Id);
        Assert.Equal(string.Empty, dto.ProviderName);
        Assert.Equal("/orphan-model", dto.Slug);
    }

    // ---- 手动添加（接口 8）----

    [Fact]
    public async Task Create_Valid_Returns201WithSlug()
    {
        var provider = await SeedProviderAsync();

        var result = await CreateController().Create(
            new TrainingModelCreateRequest(provider.Id.ToString(), "  gpt-4o  "));

        var dto = PayloadOf(result);
        Assert.Equal("gpt-4o", dto.Model);
        Assert.Equal("openai-prod/gpt-4o", dto.Slug);
        Assert.True(dto.Enabled);
        Assert.Equal(provider.Id.ToString(), dto.ProviderId);
    }

    [Theory]
    [InlineData("not-an-objectid")]
    [InlineData("62a2a2a2a2a2a2a2a2a2a2a2")]
    public async Task Create_InvalidOrUnknownProvider_Returns404(string providerId)
    {
        var result = await CreateController().Create(new TrainingModelCreateRequest(providerId, "m"));

        Assert.Equal("provider not found", ErrorOf(result, StatusCodes.Status404NotFound));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Create_EmptyModel_Returns400(string model)
    {
        var provider = await SeedProviderAsync();

        var result = await CreateController().Create(
            new TrainingModelCreateRequest(provider.Id.ToString(), model));

        Assert.Equal("model is required", ErrorOf(result, StatusCodes.Status400BadRequest));
    }

    [Fact]
    public async Task Create_ModelTooLong_Returns400()
    {
        var provider = await SeedProviderAsync();

        var result = await CreateController().Create(
            new TrainingModelCreateRequest(provider.Id.ToString(), new string('m', 129)));

        Assert.Equal("model too long", ErrorOf(result, StatusCodes.Status400BadRequest));
    }

    [Fact]
    public async Task Create_Duplicate_Returns409()
    {
        var provider = await SeedProviderAsync();
        await CreateController().Create(new TrainingModelCreateRequest(provider.Id.ToString(), "gpt-4o"));

        var result = await CreateController().Create(
            new TrainingModelCreateRequest(provider.Id.ToString(), "gpt-4o"));

        Assert.Equal("model exists", ErrorOf(result, StatusCodes.Status409Conflict));
    }

    // ---- 更新（接口 9）----

    [Fact]
    public async Task Update_DisableAndRename_ChangesOnlyProvidedFields()
    {
        var provider = await SeedProviderAsync();
        var model = await SeedModelAsync(provider.Id, "before");

        var disabled = PayloadOf(await CreateController().Update(
            model.Id.ToString(), new TrainingModelUpdateRequest(null, false)));
        Assert.False(disabled.Enabled);
        Assert.Equal("before", disabled.Model);

        var renamed = PayloadOf(await CreateController().Update(
            model.Id.ToString(), new TrainingModelUpdateRequest("after", null)));
        Assert.Equal("after", renamed.Model);
        Assert.False(renamed.Enabled);
        Assert.Equal("openai-prod/after", renamed.Slug);

        var stored = await Models.GetByIdAsync(model.Id);
        Assert.Equal("after", stored!.Model);
        Assert.False(stored.Enabled);
    }

    [Fact]
    public async Task Update_RenameToExisting_Returns409()
    {
        var provider = await SeedProviderAsync();
        var first = await SeedModelAsync(provider.Id, "one");
        await SeedModelAsync(provider.Id, "two");

        var result = await CreateController().Update(
            first.Id.ToString(), new TrainingModelUpdateRequest("two", null));

        Assert.Equal("model exists", ErrorOf(result, StatusCodes.Status409Conflict));
        Assert.Equal("one", (await Models.GetByIdAsync(first.Id))!.Model);
    }

    [Theory]
    [InlineData("not-an-objectid")]
    [InlineData("62a2a2a2a2a2a2a2a2a2a2a2")]
    public async Task Update_InvalidOrUnknown_Returns404(string modelId)
    {
        var result = await CreateController().Update(
            modelId, new TrainingModelUpdateRequest("x", null));

        Assert.Equal("model not found", ErrorOf(result, StatusCodes.Status404NotFound));
    }

    // ---- 删除（接口 10）----

    [Fact]
    public async Task Delete_Existing_Returns204()
    {
        var provider = await SeedProviderAsync();
        var model = await SeedModelAsync(provider.Id, "m");

        var result = await CreateController().Delete(model.Id.ToString());

        var noContent = Assert.IsType<NoContentResult>(result);
        Assert.Equal(StatusCodes.Status204NoContent, noContent.StatusCode);
        Assert.Null(await Models.GetByIdAsync(model.Id));
    }

    [Theory]
    [InlineData("not-an-objectid")]
    [InlineData("62a2a2a2a2a2a2a2a2a2a2a2")]
    public async Task Delete_InvalidOrUnknown_Returns404(string modelId)
    {
        var result = await CreateController().Delete(modelId);

        Assert.Equal("model not found", ErrorOfResult(result, StatusCodes.Status404NotFound));
    }
}
