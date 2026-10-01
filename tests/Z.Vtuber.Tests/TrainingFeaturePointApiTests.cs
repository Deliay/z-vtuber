using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using MongoDB.Driver;
using Z.Vtuber.Common.Persistence;
using Z.Vtuber.Common.Persistence.Entities;
using Z.Vtuber.Training.Contracts;
using Z.Vtuber.Training.Controllers;
using Z.Vtuber.Training.FeaturePoints;

namespace Z.Vtuber.Tests;

/// <summary>
/// Plan 3.4：功能点 API（控制器直测 + Testcontainers Mongo）——
/// resolve 自动登记/更新能力要求、指定优先、取消恢复默认、指定失效标注、错误口径。
/// </summary>
public class TrainingFeaturePointApiTests(MongoDbFixture fixture)
    : IClassFixture<MongoDbFixture>, IAsyncLifetime
{
    public Task InitializeAsync() => fixture.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private TrainingFeaturePointRepository FeaturePoints => new(fixture.Database);
    private TrainingModelRepository Models => new(fixture.Database);
    private TrainingProviderRepository Providers => new(fixture.Database);

    private TrainingFeaturePointsController CreateController() =>
        WithContext(new TrainingFeaturePointsController(
            FeaturePoints, Models, Providers,
            new TrainingModelResolver(FeaturePoints, Models, Providers),
            NullLogger<TrainingFeaturePointsController>.Instance));

    private static T WithContext<T>(T controller) where T : ControllerBase
    {
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
        return controller;
    }

    private static T PayloadOf<T>(ActionResult<T> result) =>
        Assert.IsType<T>(Assert.IsAssignableFrom<ObjectResult>(result.Result).Value);

    private static string ErrorOf<T>(ActionResult<T> result, int statusCode)
    {
        var objectResult = Assert.IsAssignableFrom<ObjectResult>(result.Result);
        Assert.Equal(statusCode, objectResult.StatusCode);
        return Assert.IsType<ErrorResponse>(objectResult.Value).Error;
    }

    private async Task<TrainingModelRecord> SeedModelAsync(
        string model, bool enabled = true, string[]? input = null, string[]? output = null,
        DateTime? createdAt = null)
    {
        var now = createdAt ?? DateTime.UtcNow;
        var provider = await Providers.InsertAsync(new TrainingProviderRecord
        {
            Name = $"p-{Guid.NewGuid():N}", Type = "openai", Endpoint = "http://llm", ApiKey = "k",
            CreatedAt = now, UpdatedAt = now,
        });
        return await Models.InsertAsync(new TrainingModelRecord
        {
            ProviderId = provider.Id, Model = model, Enabled = enabled,
            InputCapabilities = input ?? ["text"], OutputCapabilities = output ?? ["text"],
            CreatedAt = now, UpdatedAt = now,
        });
    }

    private static readonly string[] AudioTextIn = ["audio", "text"];

    [Fact]
    public async Task Resolve_FirstTime_RegistersAndReturnsDefault()
    {
        var model = await SeedModelAsync("earliest", input: AudioTextIn,
            createdAt: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        await SeedModelAsync("later", input: AudioTextIn,
            createdAt: new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc));

        var dto = PayloadOf(await CreateController().Resolve(
            "emotion-from-audio",
            new TrainingFeaturePointResolveRequest("输入语音识别情绪", AudioTextIn, ["text"])));

        Assert.Equal("default", dto.Source);
        Assert.Equal(model.Id.ToString(), dto.ModelId);

        var list = PayloadOf(await CreateController().List());
        var fp = Assert.Single(list.Items);
        Assert.Equal("emotion-from-audio", fp.Slug);
        Assert.Equal("输入语音识别情绪", fp.Name);
        Assert.Equal(AudioTextIn, fp.InputCapabilities.ToArray());
        Assert.Null(fp.SpecifiedModelId);
        Assert.Equal(model.Id.ToString(), fp.DefaultModelId);
        Assert.EndsWith("/earliest", fp.DefaultModelSlug);
    }

    [Fact]
    public async Task Resolve_NoCandidates_ReturnsNone()
    {
        await SeedModelAsync("audio-only", input: ["audio"], output: ["audio"]);

        var dto = PayloadOf(await CreateController().Resolve(
            "fp", new TrainingFeaturePointResolveRequest("F", ["text"], ["text"])));

        Assert.Equal("none", dto.Source);
        Assert.Null(dto.ModelId);
        Assert.Null(dto.ModelSlug);
    }

    [Fact]
    public async Task Resolve_SecondTime_UpdatesNameAndCapabilities()
    {
        await SeedModelAsync("m", input: AudioTextIn);

        var controller = CreateController();
        await controller.Resolve("fp", new TrainingFeaturePointResolveRequest("旧名", AudioTextIn, ["text"]));
        await controller.Resolve("fp", new TrainingFeaturePointResolveRequest("新名", ["text"], ["text"]));

        var fp = Assert.Single(PayloadOf(await controller.List()).Items);
        Assert.Equal("新名", fp.Name);
        Assert.Equal(new[] { "text" }, fp.InputCapabilities.ToArray());
    }

    [Fact]
    public async Task SetModel_Qualified_ThenResolveReturnsSpecified()
    {
        var first = await SeedModelAsync("first", createdAt: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var target = await SeedModelAsync("target", createdAt: new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc));
        var controller = CreateController();
        await controller.Resolve("fp", new TrainingFeaturePointResolveRequest("F", ["text"], ["text"]));

        var set = PayloadOf(await controller.SetModel(
            "fp", new TrainingFeaturePointSetModelRequest(target.Id.ToString())));

        Assert.Equal(target.Id.ToString(), set.SpecifiedModelId);
        Assert.True(set.SpecifiedModelAvailable);
        // 默认选择独立于指定：仍为可用模型中 CreatedAt 最早者（first）。
        Assert.Equal(first.Id.ToString(), set.DefaultModelId);

        var resolved = PayloadOf(await controller.Resolve(
            "fp", new TrainingFeaturePointResolveRequest("F", ["text"], ["text"])));
        Assert.Equal("specified", resolved.Source);
        Assert.Equal(target.Id.ToString(), resolved.ModelId);
    }

    [Fact]
    public async Task ClearModel_RestoresDefaultSelection()
    {
        var model = await SeedModelAsync("only");
        var controller = CreateController();
        await controller.Resolve("fp", new TrainingFeaturePointResolveRequest("F", ["text"], ["text"]));
        await controller.SetModel("fp", new TrainingFeaturePointSetModelRequest(model.Id.ToString()));

        var cleared = PayloadOf(await controller.ClearModel("fp"));

        Assert.Null(cleared.SpecifiedModelId);
        Assert.Equal(model.Id.ToString(), cleared.DefaultModelId);
        var resolved = PayloadOf(await controller.Resolve(
            "fp", new TrainingFeaturePointResolveRequest("F", ["text"], ["text"])));
        Assert.Equal("default", resolved.Source);
    }

    [Fact]
    public async Task SetModel_SpecifiedUnavailable_MarkedAndFallsBack()
    {
        await SeedModelAsync("default-model", createdAt: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var specified = await SeedModelAsync("specified",
            createdAt: new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc));
        var controller = CreateController();
        await controller.Resolve("fp", new TrainingFeaturePointResolveRequest("F", ["text"], ["text"]));
        await controller.SetModel("fp", new TrainingFeaturePointSetModelRequest(specified.Id.ToString()));

        // 停用指定模型：列表标注不可用并回退默认，resolve 回 default；指定本身不被清除。
        await Models.UpdateAsync(specified.Id,
            Builders<TrainingModelRecord>.Update.Set(m => m.Enabled, false));

        var fp = Assert.Single(PayloadOf(await controller.List()).Items);
        Assert.Equal(specified.Id.ToString(), fp.SpecifiedModelId);
        Assert.False(fp.SpecifiedModelAvailable);
        Assert.NotEqual(specified.Id.ToString(), fp.DefaultModelId);

        var resolved = PayloadOf(await controller.Resolve(
            "fp", new TrainingFeaturePointResolveRequest("F", ["text"], ["text"])));
        Assert.Equal("default", resolved.Source);

        // 恢复可用后重新优先返回指定模型。
        await Models.UpdateAsync(specified.Id,
            Builders<TrainingModelRecord>.Update.Set(m => m.Enabled, true));
        var restored = PayloadOf(await controller.Resolve(
            "fp", new TrainingFeaturePointResolveRequest("F", ["text"], ["text"])));
        Assert.Equal("specified", restored.Source);
    }

    [Fact]
    public async Task SetModel_UnqualifiedModel_Returns400()
    {
        await SeedModelAsync("text-only");
        var audioOnly = await SeedModelAsync("audio-only", input: ["audio"], output: ["text"]);
        var controller = CreateController();
        await controller.Resolve("fp", new TrainingFeaturePointResolveRequest("F", ["text", "audio"], ["text"]));

        var result = await controller.SetModel(
            "fp", new TrainingFeaturePointSetModelRequest(audioOnly.Id.ToString()));

        Assert.Equal("model not qualified", ErrorOf(result, StatusCodes.Status400BadRequest));
    }

    [Fact]
    public async Task SetModel_InvalidModelId_Returns400()
    {
        var result = await CreateController().SetModel(
            "fp", new TrainingFeaturePointSetModelRequest("not-an-objectid"));

        Assert.Equal("modelId is required", ErrorOf(result, StatusCodes.Status400BadRequest));
    }

    [Fact]
    public async Task SetModel_UnknownFeaturePointOrModel_Returns404()
    {
        var model = await SeedModelAsync("m");

        var unknownFp = await CreateController().SetModel(
            "missing", new TrainingFeaturePointSetModelRequest(model.Id.ToString()));
        Assert.Equal("feature point not found", ErrorOf(unknownFp, StatusCodes.Status404NotFound));

        var controller = CreateController();
        await controller.Resolve("fp", new TrainingFeaturePointResolveRequest("F", ["text"], ["text"]));
        var unknownModel = await controller.SetModel(
            "fp", new TrainingFeaturePointSetModelRequest(ObjectId.GenerateNewId().ToString()));
        Assert.Equal("model not found", ErrorOf(unknownModel, StatusCodes.Status404NotFound));
    }

    [Fact]
    public async Task ClearModel_UnknownFeaturePoint_Returns404()
    {
        var result = await CreateController().ClearModel("missing");

        Assert.Equal("feature point not found", ErrorOf(result, StatusCodes.Status404NotFound));
    }

    [Fact]
    public async Task Resolve_UnsupportedCapability_Returns400()
    {
        var result = await CreateController().Resolve(
            "fp", new TrainingFeaturePointResolveRequest("F", ["bogus"], ["text"]));

        Assert.Equal("unsupported capability", ErrorOf(result, StatusCodes.Status400BadRequest));
    }

    [Fact]
    public async Task Resolve_MissingCapabilities_Returns400_AndDoesNotRegister()
    {
        var controller = CreateController();

        var missingInput = await controller.Resolve(
            "fp", new TrainingFeaturePointResolveRequest("F", null, ["text"]));
        Assert.Equal("inputCapabilities is required", ErrorOf(missingInput, StatusCodes.Status400BadRequest));

        var missingOutput = await controller.Resolve(
            "fp", new TrainingFeaturePointResolveRequest("F", ["text"], null));
        Assert.Equal("outputCapabilities is required", ErrorOf(missingOutput, StatusCodes.Status400BadRequest));

        // 空 body：两字段均缺失，先报输入必填。
        var emptyBody = await controller.Resolve("fp", null);
        Assert.Equal("inputCapabilities is required", ErrorOf(emptyBody, StatusCodes.Status400BadRequest));

        Assert.Empty(await FeaturePoints.ListAsync());
    }

    [Fact]
    public async Task Resolve_EmptyCapabilities_Returns400()
    {
        var controller = CreateController();

        var emptyInput = await controller.Resolve(
            "fp", new TrainingFeaturePointResolveRequest("F", [], ["text"]));
        Assert.Equal("inputCapabilities is required", ErrorOf(emptyInput, StatusCodes.Status400BadRequest));

        var emptyOutput = await controller.Resolve(
            "fp", new TrainingFeaturePointResolveRequest("F", ["text"], []));
        Assert.Equal("outputCapabilities is required", ErrorOf(emptyOutput, StatusCodes.Status400BadRequest));

        Assert.Empty(await FeaturePoints.ListAsync());
    }

    [Fact]
    public async Task Resolve_WhitespaceSlug_Returns400()
    {
        var result = await CreateController().Resolve(
            "   ", new TrainingFeaturePointResolveRequest("F", ["text"], ["text"]));

        Assert.Equal("feature point slug is required", ErrorOf(result, StatusCodes.Status400BadRequest));
    }

    [Fact]
    public async Task Resolve_SlugTooLong_Returns400()
    {
        var result = await CreateController().Resolve(
            new string('s', 129), new TrainingFeaturePointResolveRequest("F", ["text"], ["text"]));

        Assert.Equal("feature point slug too long", ErrorOf(result, StatusCodes.Status400BadRequest));
    }
}
