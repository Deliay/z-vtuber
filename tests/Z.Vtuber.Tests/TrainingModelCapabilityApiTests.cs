using System.Text.Json;
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
/// Plan 3.3：模型能力读写与过滤（控制器直测 + Testcontainers Mongo）——
/// create/update 能力归一化、非法 400、update 缺省不改/空数组清空、列表按能力过滤。
/// </summary>
public class TrainingModelCapabilityApiTests(MongoDbFixture fixture)
    : IClassFixture<MongoDbFixture>, IAsyncLifetime
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

    private static T PayloadOf<T>(ActionResult<T> result) =>
        Assert.IsType<T>(Assert.IsAssignableFrom<ObjectResult>(result.Result).Value);

    private static string ErrorOf<T>(ActionResult<T> result, int statusCode)
    {
        var objectResult = Assert.IsAssignableFrom<ObjectResult>(result.Result);
        Assert.Equal(statusCode, objectResult.StatusCode);
        return Assert.IsType<ErrorResponse>(objectResult.Value).Error;
    }

    private async Task<TrainingProviderRecord> SeedProviderAsync()
    {
        var now = DateTime.UtcNow;
        return await Providers.InsertAsync(new TrainingProviderRecord
        {
            Name = $"p-{Guid.NewGuid():N}", Type = "openai", Endpoint = "http://llm", ApiKey = "k",
            CreatedAt = now, UpdatedAt = now,
        });
    }

    private static TrainingModelUpdateRequest Update(string json) =>
        JsonSerializer.Deserialize<TrainingModelUpdateRequest>(json)!;

    [Fact]
    public async Task Create_WithCapabilities_NormalizesAndReturnsThem()
    {
        var provider = await SeedProviderAsync();

        var result = await CreateController().Create(new TrainingModelCreateRequest(
            provider.Id.ToString(), "gpt-audio", [" Audio ", "TEXT", "audio"], ["text"]));

        var dto = PayloadOf(result);
        Assert.Equal(new[] { "audio", "text" }, dto.InputCapabilities.ToArray());
        Assert.Equal(new[] { "text" }, dto.OutputCapabilities.ToArray());
    }

    [Fact]
    public async Task Create_MissingCapabilities_YieldsEmpty()
    {
        var provider = await SeedProviderAsync();

        var dto = PayloadOf(await CreateController().Create(
            new TrainingModelCreateRequest(provider.Id.ToString(), "plain")));

        Assert.Empty(dto.InputCapabilities);
        Assert.Empty(dto.OutputCapabilities);
    }

    [Fact]
    public async Task Create_UnsupportedCapability_Returns400()
    {
        var provider = await SeedProviderAsync();

        var result = await CreateController().Create(new TrainingModelCreateRequest(
            provider.Id.ToString(), "bad", ["audio", "bogus"], null));

        Assert.Equal("unsupported capability", ErrorOf(result, StatusCodes.Status400BadRequest));
    }

    [Fact]
    public async Task Update_CapabilitiesAbsent_Unchanged()
    {
        var provider = await SeedProviderAsync();
        var created = PayloadOf(await CreateController().Create(new TrainingModelCreateRequest(
            provider.Id.ToString(), "m", ["audio"], ["text"])));

        var updated = PayloadOf(await CreateController().Update(
            created.Id, Update("""{"enabled":false}""")));

        Assert.False(updated.Enabled);
        Assert.Equal(new[] { "audio" }, updated.InputCapabilities.ToArray());
        Assert.Equal(new[] { "text" }, updated.OutputCapabilities.ToArray());
    }

    [Fact]
    public async Task Update_ExplicitNullCapabilities_Unchanged()
    {
        var provider = await SeedProviderAsync();
        var created = PayloadOf(await CreateController().Create(new TrainingModelCreateRequest(
            provider.Id.ToString(), "m", ["audio"], ["text"])));

        var updated = PayloadOf(await CreateController().Update(
            created.Id, Update("""{"inputCapabilities":null,"outputCapabilities":null}""")));

        Assert.Equal(new[] { "audio" }, updated.InputCapabilities.ToArray());
        Assert.Equal(new[] { "text" }, updated.OutputCapabilities.ToArray());
    }

    [Fact]
    public async Task Update_EmptyArray_ClearsCapabilities()
    {
        var provider = await SeedProviderAsync();
        var created = PayloadOf(await CreateController().Create(new TrainingModelCreateRequest(
            provider.Id.ToString(), "m", ["audio"], ["text"])));

        var updated = PayloadOf(await CreateController().Update(
            created.Id, Update("""{"inputCapabilities":[],"outputCapabilities":["image"]}""")));

        Assert.Empty(updated.InputCapabilities);
        Assert.Equal(new[] { "image" }, updated.OutputCapabilities.ToArray());
    }

    [Fact]
    public async Task Update_UnsupportedCapability_Returns400()
    {
        var provider = await SeedProviderAsync();
        var created = PayloadOf(await CreateController().Create(
            new TrainingModelCreateRequest(provider.Id.ToString(), "m")));

        var result = await CreateController().Update(
            created.Id, Update("""{"inputCapabilities":["nope"]}"""));

        Assert.Equal("unsupported capability", ErrorOf(result, StatusCodes.Status400BadRequest));
    }

    [Fact]
    public async Task List_FiltersByInputAndOutputCapabilities()
    {
        var provider = await SeedProviderAsync();
        var controller = CreateController();
        PayloadOf(await controller.Create(new TrainingModelCreateRequest(
            provider.Id.ToString(), "audio-text", ["audio", "text"], ["text"])));
        PayloadOf(await controller.Create(new TrainingModelCreateRequest(
            provider.Id.ToString(), "text-only", ["text"], ["text"])));
        PayloadOf(await controller.Create(new TrainingModelCreateRequest(
            provider.Id.ToString(), "no-cap")));

        var audioText = PayloadOf(await controller.List(null, "audio,text", "text"));
        Assert.Equal("audio-text", Assert.Single(audioText.Items).Model);

        var text = PayloadOf(await controller.List(true, "text", "text"));
        Assert.Equal(new[] { "audio-text", "text-only" }, text.Items.Select(m => m.Model).ToArray());

        var all = PayloadOf(await controller.List(null, null, null));
        Assert.Equal(3, all.Items.Count);
    }

    [Fact]
    public async Task List_UnsupportedCapabilityQuery_Returns400()
    {
        var result = await CreateController().List(null, "bogus", null);

        Assert.Equal("unsupported capability", ErrorOf(result, StatusCodes.Status400BadRequest));
    }
}
