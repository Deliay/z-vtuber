using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using Z.Vtuber.Common.Persistence;
using Z.Vtuber.Common.Persistence.Entities;
using Z.Vtuber.Training.Ai;
using Z.Vtuber.Training.Contracts;
using Z.Vtuber.Training.Controllers;

namespace Z.Vtuber.Tests;

/// <summary>
/// Plan 1.9：<see cref="TrainingProvidersController"/> 控制器直测（Testcontainers Mongo + 假 discover）——
/// 新建 201 / 四类 400 / 重名 409、响应永不含 <c>apiKey</c> 仅 <c>apiKeySet</c>、
/// 编辑部分字段（缺省不改、apiKey 空串不改）、删除级联其下 Models、discover 200/502/404、导入去重。
/// </summary>
public class TrainingProviderApiTests(MongoDbFixture fixture) : IClassFixture<MongoDbFixture>, IAsyncLifetime
{
    public Task InitializeAsync() => fixture.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private TrainingProviderRepository Providers => new(fixture.Database);

    private TrainingModelRepository Models => new(fixture.Database);

    private TrainingProvidersController CreateController(IModelDiscoveryClient? discovery = null) =>
        WithContext(new TrainingProvidersController(
            Providers, Models, discovery ?? new ProviderDiscoverStub(),
            NullLogger<TrainingProvidersController>.Instance));

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

    private static TrainingProviderCreateRequest CreateRequest(
        string? name = "openai-prod",
        string? type = "openai",
        string? endpoint = "https://api.openai.com/v1",
        string? apiKey = "sk-secret") =>
        new(name, type, endpoint, apiKey);

    /// <summary>可配置的模型服务发现桩。</summary>
    private sealed class ProviderDiscoverStub : IModelDiscoveryClient
    {
        public IReadOnlyList<string> Models { get; set; } = [];

        public Exception? Throw { get; set; }

        public Task<IReadOnlyList<string>> DiscoverAsync(
            TrainingProviderRecord provider, CancellationToken ct = default) =>
            Throw is null
                ? Task.FromResult(Models)
                : Task.FromException<IReadOnlyList<string>>(Throw);
    }

    // ---- 列表 / 新建（接口 1-2）----

    [Fact]
    public async Task List_OrdersByCreatedAt_AndNeverExposesApiKey()
    {
        var later = await Providers.InsertAsync(new TrainingProviderRecord
        {
            Name = "later", Type = "claude", Endpoint = "http://b", ApiKey = "k2",
            CreatedAt = new DateTime(2026, 9, 27, 0, 1, 0, DateTimeKind.Utc),
            UpdatedAt = new DateTime(2026, 9, 27, 0, 1, 0, DateTimeKind.Utc),
        });
        var first = await Providers.InsertAsync(new TrainingProviderRecord
        {
            Name = "first", Type = "openai", Endpoint = "http://a", ApiKey = "k1",
            CreatedAt = new DateTime(2026, 9, 27, 0, 0, 0, DateTimeKind.Utc),
            UpdatedAt = new DateTime(2026, 9, 27, 0, 0, 0, DateTimeKind.Utc),
        });

        var list = PayloadOf(await CreateController().List());

        Assert.Equal(new[] { first.Id, later.Id }, list.Items.Select(i => ObjectId.Parse(i.Id)).ToArray());
        Assert.All(list.Items, i => Assert.True(i.ApiKeySet));
        var json = JsonSerializer.Serialize(list);
        Assert.Contains("\"apiKeySet\"", json);
        Assert.DoesNotContain("\"apiKey\"", json);
    }

    [Fact]
    public async Task Create_Valid_Returns201WithNormalizedFields()
    {
        var result = await CreateController().Create(
            CreateRequest(name: "  openai-prod  ", type: "openai",
                endpoint: "  https://api.openai.com/v1/  ", apiKey: "sk-secret"));

        var dto = PayloadOf(result);
        Assert.Equal("openai-prod", dto.Name);
        Assert.Equal("openai", dto.Type);
        Assert.Equal("https://api.openai.com/v1", dto.Endpoint);
        Assert.True(dto.ApiKeySet);

        var stored = Assert.Single(await Providers.ListAsync());
        Assert.Equal("sk-secret", stored.ApiKey);

        var json = JsonSerializer.Serialize(dto);
        Assert.Contains("\"apiKeySet\":true", json);
        Assert.DoesNotContain("\"apiKey\"", json);
    }

    [Theory]
    [InlineData("name", "   ", "name is required")]
    [InlineData("name", null, "name is required")]
    [InlineData("type", "gemini", "unsupported provider type")]
    [InlineData("type", "", "unsupported provider type")]
    [InlineData("endpoint", "", "endpoint is required")]
    [InlineData("endpoint", "ftp://host/models", "invalid endpoint")]
    [InlineData("endpoint", "not-a-url", "invalid endpoint")]
    [InlineData("apiKey", "  ", "api key is required")]
    public async Task Create_InvalidField_Returns400(string field, string? value, string expected)
    {
        var request = CreateRequest();
        request = field switch
        {
            "name" => request with { Name = value },
            "type" => request with { Type = value },
            "endpoint" => request with { Endpoint = value },
            _ => request with { ApiKey = value },
        };

        var result = await CreateController().Create(request);

        Assert.Equal(expected, ErrorOf(result, StatusCodes.Status400BadRequest));
        Assert.Empty(await Providers.ListAsync());
    }

    [Fact]
    public async Task Create_NameTooLong_Returns400()
    {
        var result = await CreateController().Create(CreateRequest(name: new string('a', 65)));

        Assert.Equal("name too long", ErrorOf(result, StatusCodes.Status400BadRequest));
    }

    [Fact]
    public async Task Create_DuplicateName_Returns409()
    {
        await CreateController().Create(CreateRequest(name: "dup"));

        var result = await CreateController().Create(
            CreateRequest(name: "dup", endpoint: "https://other.example"));

        Assert.Equal("provider name exists", ErrorOf(result, StatusCodes.Status409Conflict));
    }

    // ---- 更新（接口 3）----

    [Fact]
    public async Task Update_PartialFields_KeepsMissingAndApiKey()
    {
        var record = await Providers.InsertAsync(new TrainingProviderRecord
        {
            Name = "before", Type = "openai", Endpoint = "http://old", ApiKey = "keep-me",
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });

        var result = await CreateController().Update(
            record.Id.ToString(), new TrainingProviderUpdateRequest(
                Name: "after", Type: null, Endpoint: null, ApiKey: null));

        var dto = PayloadOf(result);
        Assert.Equal("after", dto.Name);
        Assert.Equal("http://old", dto.Endpoint);
        Assert.Equal("openai", dto.Type);

        var stored = Assert.Single(await Providers.ListAsync());
        Assert.Equal("after", stored.Name);
        Assert.Equal("http://old", stored.Endpoint);
        Assert.Equal("keep-me", stored.ApiKey);
    }

    [Fact]
    public async Task Update_EmptyApiKey_DoesNotChangeStoredKey()
    {
        var record = await Providers.InsertAsync(new TrainingProviderRecord
        {
            Name = "keep", Type = "openai", Endpoint = "http://old", ApiKey = "keep-me",
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });

        var result = await CreateController().Update(
            record.Id.ToString(),
            new TrainingProviderUpdateRequest(null, null, "http://new", ApiKey: ""));

        var dto = PayloadOf(result);
        Assert.Equal("http://new", dto.Endpoint);
        Assert.True(dto.ApiKeySet);

        var stored = Assert.Single(await Providers.ListAsync());
        Assert.Equal("keep-me", stored.ApiKey);
    }

    [Fact]
    public async Task Update_DuplicateName_Returns409()
    {
        await CreateController().Create(CreateRequest(name: "taken", endpoint: "http://a"));
        var record = await Providers.InsertAsync(new TrainingProviderRecord
        {
            Name = "other", Type = "openai", Endpoint = "http://b", ApiKey = "k",
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });

        var result = await CreateController().Update(
            record.Id.ToString(), new TrainingProviderUpdateRequest("taken", null, null, null));

        Assert.Equal("provider name exists", ErrorOf(result, StatusCodes.Status409Conflict));
        Assert.Equal("other", (await Providers.GetByIdAsync(record.Id))!.Name);
    }

    [Theory]
    [InlineData("not-an-objectid")]
    [InlineData("62a2a2a2a2a2a2a2a2a2a2a2")]
    public async Task Update_InvalidOrUnknown_Returns404(string providerId)
    {
        var result = await CreateController().Update(
            providerId, new TrainingProviderUpdateRequest("x", null, null, null));

        Assert.Equal("provider not found", ErrorOf(result, StatusCodes.Status404NotFound));
    }

    // ---- 删除（接口 4）----

    [Fact]
    public async Task Delete_CascadesModels()
    {
        var provider = await Providers.InsertAsync(new TrainingProviderRecord
        {
            Name = "cascade", Type = "openai", Endpoint = "http://a", ApiKey = "k",
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        var other = await Providers.InsertAsync(new TrainingProviderRecord
        {
            Name = "keep", Type = "claude", Endpoint = "http://b", ApiKey = "k",
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        await Models.InsertAsync(new TrainingModelRecord
        {
            ProviderId = provider.Id, Model = "m1", Enabled = true,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        await Models.InsertAsync(new TrainingModelRecord
        {
            ProviderId = provider.Id, Model = "m2", Enabled = true,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        var kept = await Models.InsertAsync(new TrainingModelRecord
        {
            ProviderId = other.Id, Model = "k1", Enabled = true,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });

        var result = await CreateController().Delete(provider.Id.ToString());

        var noContent = Assert.IsType<NoContentResult>(result);
        Assert.Equal(StatusCodes.Status204NoContent, noContent.StatusCode);
        Assert.Null(await Providers.GetByIdAsync(provider.Id));
        var remaining = Assert.Single(await Models.ListAsync());
        Assert.Equal(kept.Id, remaining.Id);
    }

    [Theory]
    [InlineData("not-an-objectid")]
    [InlineData("62a2a2a2a2a2a2a2a2a2a2a2")]
    public async Task Delete_InvalidOrUnknown_Returns404(string providerId)
    {
        var result = await CreateController().Delete(providerId);

        Assert.Equal("provider not found", ErrorOfResult(result, StatusCodes.Status404NotFound));
    }

    // ---- 同步模型（接口 5）----

    [Fact]
    public async Task Discover_ReturnsModelsFromProvider()
    {
        var provider = await Providers.InsertAsync(new TrainingProviderRecord
        {
            Name = "discover", Type = "openai", Endpoint = "http://llm", ApiKey = "k",
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        var stub = new ProviderDiscoverStub { Models = ["gpt-4o", "gpt-4o-mini"] };

        var result = await CreateController(stub).Discover(provider.Id.ToString());

        var dto = PayloadOf(result);
        Assert.Equal(new[] { "gpt-4o", "gpt-4o-mini" }, dto.Models.Select(m => m.Model).ToArray());
    }

    [Fact]
    public async Task Discover_EndpointFailure_Returns502ProviderServiceUnavailable()
    {
        var provider = await Providers.InsertAsync(new TrainingProviderRecord
        {
            Name = "down", Type = "openai", Endpoint = "http://llm", ApiKey = "k",
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        var stub = new ProviderDiscoverStub { Throw = new HttpRequestException("refused") };

        var result = await CreateController(stub).Discover(provider.Id.ToString());

        Assert.Equal("provider service unavailable", ErrorOf(result, StatusCodes.Status502BadGateway));
    }

    [Fact]
    public async Task Discover_InvalidResponse_Returns502InvalidProviderResponse()
    {
        var provider = await Providers.InsertAsync(new TrainingProviderRecord
        {
            Name = "bad", Type = "openai", Endpoint = "http://llm", ApiKey = "k",
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        var stub = new ProviderDiscoverStub
        {
            Throw = new InvalidProviderResponseException("bad payload"),
        };

        var result = await CreateController(stub).Discover(provider.Id.ToString());

        Assert.Equal("invalid provider response", ErrorOf(result, StatusCodes.Status502BadGateway));
    }

    [Fact]
    public async Task Discover_UnknownProvider_Returns404()
    {
        var result = await CreateController()
            .Discover(ObjectId.GenerateNewId().ToString());

        Assert.Equal("provider not found", ErrorOf(result, StatusCodes.Status404NotFound));
    }

    // ---- 导入模型（接口 6）----

    [Fact]
    public async Task Import_InsertsModels_AndCountsDuplicateAsSkipped()
    {
        var provider = await Providers.InsertAsync(new TrainingProviderRecord
        {
            Name = "import", Type = "openai", Endpoint = "http://llm", ApiKey = "k",
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        var controller = CreateController();

        var first = PayloadOf(await controller.Import(
            provider.Id.ToString(), new TrainingModelImportRequest(["gpt-4o", "gpt-4o-mini"])));
        Assert.Equal(2, first.Imported);
        Assert.Equal(0, first.Skipped);

        var second = PayloadOf(await controller.Import(
            provider.Id.ToString(),
            new TrainingModelImportRequest(["gpt-4o", "  ", "claude-3"])));

        // gpt-4o 撞唯一索引、空白项非法 → skipped；claude-3 新导入
        Assert.Equal(1, second.Imported);
        Assert.Equal(2, second.Skipped);

        var stored = await Models.ListAsync();
        Assert.Equal(3, stored.Count);
        Assert.All(stored, m => Assert.Equal(provider.Id, m.ProviderId));
        Assert.All(stored, m => Assert.True(m.Enabled));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("""{"models":[]}""")]
    public async Task Import_EmptyModels_Returns400(string? modelsJson)
    {
        var provider = await Providers.InsertAsync(new TrainingProviderRecord
        {
            Name = "empty", Type = "openai", Endpoint = "http://llm", ApiKey = "k",
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        var request = modelsJson is null
            ? new TrainingModelImportRequest(null)
            : JsonSerializer.Deserialize<TrainingModelImportRequest>(modelsJson);

        var result = await CreateController().Import(provider.Id.ToString(), request);

        Assert.Equal("models is required", ErrorOf(result, StatusCodes.Status400BadRequest));
    }

    [Fact]
    public async Task Import_UnknownProvider_Returns404()
    {
        var result = await CreateController()
            .Import(ObjectId.GenerateNewId().ToString(), new TrainingModelImportRequest(["m"]));

        Assert.Equal("provider not found", ErrorOf(result, StatusCodes.Status404NotFound));
    }

    [Fact]
    public async Task Import_ModelTooLong_Returns400_AndInsertsNothing()
    {
        // 返工 #11：导入不得绕过手动添加的 MaxModelLength(128) 校验
        var provider = await Providers.InsertAsync(new TrainingProviderRecord
        {
            Name = "too-long", Type = "openai", Endpoint = "http://llm", ApiKey = "k",
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });

        var result = await CreateController().Import(
            provider.Id.ToString(), new TrainingModelImportRequest(["ok", new string('m', 129)]));

        Assert.Equal("model too long", ErrorOf(result, StatusCodes.Status400BadRequest));
        Assert.Empty(await Models.ListAsync());
    }
}
