using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using Z.Vtuber.Common.Contracts;
using Z.Vtuber.Common.Persistence;
using Z.Vtuber.Common.Persistence.Entities;
using Z.Vtuber.Training.Ai;
using Z.Vtuber.Training.Configuration;
using Z.Vtuber.Training.Contracts;
using Z.Vtuber.Training.Controllers;
using Z.Vtuber.Training.FeaturePoints;
using Z.Vtuber.Training.ImageGeneration;

namespace Z.Vtuber.Tests;

/// <summary>
/// 绘图生成端点（控制器直测 + 真 Mongo + 假图片客户端）：
/// 200 图片二进制、text/image/类型/大小校验（失败不发起上游调用）、显式模型 404/400/能力校验、
/// 缺省功能点解析（指定优先 → 默认首候选 → 无 409）、上游失败 502 与 Claude-like 派发 400。
/// </summary>
public class TrainingImageGenerateApiTests(MongoDbFixture fixture)
    : IClassFixture<MongoDbFixture>, IAsyncLifetime
{
    public Task InitializeAsync() => fixture.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private TrainingProviderRepository Providers => new(fixture.Database);

    private TrainingModelRepository Models => new(fixture.Database);

    private TrainingFeaturePointRepository FeaturePoints => new(fixture.Database);

    private sealed class StubImageClient : IImageModelClient
    {
        public List<(TrainingModelRecord Model, string Prompt, IReadOnlyList<GenerateImageInput> Images)> Calls { get; } = [];

        public GeneratedImage Result { get; set; } = new([4, 5, 6], "image/png");

        public Exception? Throw { get; set; }

        public Task<GeneratedImage> GenerateImageAsync(
            TrainingProviderRecord provider, TrainingModelRecord model,
            string prompt, IReadOnlyList<GenerateImageInput> images, CancellationToken ct = default)
        {
            Calls.Add((model, prompt, images));
            return Throw is not null
                ? Task.FromException<GeneratedImage>(Throw)
                : Task.FromResult(Result);
        }
    }

    private sealed class StubImageClientFactory(IImageModelClient client) : IImageModelClientFactory
    {
        public List<string> Types { get; } = [];

        public Exception? CreateThrow { get; set; }

        public IImageModelClient Create(string providerType)
        {
            Types.Add(providerType);
            if (CreateThrow is not null)
            {
                throw CreateThrow;
            }

            return client;
        }
    }

    private TrainingImageGenerateController Create(IImageModelClientFactory factory, ImageOptions? options = null)
    {
        var resolver = new TrainingModelResolver(FeaturePoints, Models, Providers);
        var service = new ImageGenerateService(
            Models, Providers, resolver, factory, NullLogger<ImageGenerateService>.Instance);
        return new TrainingImageGenerateController(
            service, options ?? new ImageOptions(), NullLogger<TrainingImageGenerateController>.Instance);
    }

    private static IFormFile File(byte[] bytes, string contentType, string fileName = "ref.png")
    {
        var stream = new MemoryStream(bytes);
        return new FormFile(stream, 0, bytes.Length, "image", fileName)
        {
            Headers = new HeaderDictionary(),
            ContentType = contentType,
        };
    }

    private static IFormFileCollection Files(params IFormFile[] files)
    {
        var collection = new FormFileCollection();
        foreach (var file in files)
        {
            collection.Add(file);
        }

        return collection;
    }

    private async Task<TrainingProviderRecord> SeedProviderAsync(string type = TrainingProviderTypes.OpenAi)
    {
        var now = DateTime.UtcNow;
        return await Providers.InsertAsync(new TrainingProviderRecord
        {
            Name = $"p-{Guid.NewGuid():N}", Type = type, Endpoint = "http://llm", ApiKey = "k",
            CreatedAt = now, UpdatedAt = now,
        });
    }

    private async Task<TrainingModelRecord> SeedModelAsync(
        ObjectId providerId,
        bool enabled = true,
        string[]? input = null,
        string[]? output = null,
        DateTime? createdAt = null)
    {
        var now = createdAt ?? DateTime.UtcNow;
        return await Models.InsertAsync(new TrainingModelRecord
        {
            ProviderId = providerId,
            Model = $"m-{Guid.NewGuid():N}",
            Enabled = enabled,
            InputCapabilities = input ?? [TrainingModelCapabilities.Text, TrainingModelCapabilities.Image],
            OutputCapabilities = output ?? [TrainingModelCapabilities.Image],
            CreatedAt = now,
            UpdatedAt = now,
        });
    }

    private async Task SetSpecifiedModelAsync(ObjectId modelId)
    {
        var now = DateTime.UtcNow;
        await FeaturePoints.RegisterAsync(new TrainingFeaturePointRecord
        {
            Slug = FeaturePointIds.ImageGenerate,
            Name = "绘图生成",
            InputCapabilities = [TrainingModelCapabilities.Text, TrainingModelCapabilities.Image],
            OutputCapabilities = [TrainingModelCapabilities.Image],
            CreatedAt = now,
            UpdatedAt = now,
        });
        await FeaturePoints.SetModelAsync(FeaturePointIds.ImageGenerate, modelId, now);
    }

    private static string ErrorOf(IActionResult result, int statusCode)
    {
        var objectResult = Assert.IsAssignableFrom<ObjectResult>(result);
        Assert.Equal(statusCode, objectResult.StatusCode);
        return Assert.IsType<ErrorResponse>(objectResult.Value).Error;
    }

    [Fact]
    public async Task Generate_Success_ExplicitModel_ReturnsImageAndForwardsInput()
    {
        var provider = await SeedProviderAsync();
        var model = await SeedModelAsync(provider.Id);
        var client = new StubImageClient { Result = new GeneratedImage([4, 5, 6], "image/png") };
        var factory = new StubImageClientFactory(client);

        var result = await Create(factory).Generate(
            "  一只猫  ", Files(File([1, 2, 3], "image/png", "cat.png")), model.Id.ToString());

        var file = Assert.IsType<FileContentResult>(result);
        Assert.Equal("image/png", file.ContentType);
        Assert.Equal([4, 5, 6], file.FileContents);
        Assert.Equal(TrainingProviderTypes.OpenAi, Assert.Single(factory.Types));
        var call = Assert.Single(client.Calls);
        Assert.Equal(model.Id, call.Model.Id);
        Assert.Equal("一只猫", call.Prompt);
        var image = Assert.Single(call.Images);
        Assert.Equal([1, 2, 3], image.Bytes);
        Assert.Equal("image/png", image.MimeType);
        Assert.Equal("cat.png", image.FileName);
    }

    [Fact]
    public async Task Generate_MultipleImages_Succeeds_AndForwardsAllInOrder()
    {
        var provider = await SeedProviderAsync();
        var model = await SeedModelAsync(provider.Id);
        var client = new StubImageClient();
        var factory = new StubImageClientFactory(client);

        var result = await Create(factory).Generate(
            "文本",
            Files(
                File([1, 2], "image/png", "a.png"),
                File([3, 4, 5], "image/jpeg", "b.jpg"),
                File([6], "image/webp", "c.webp")),
            model.Id.ToString());

        Assert.IsType<FileContentResult>(result);
        var call = Assert.Single(client.Calls);
        Assert.Equal(3, call.Images.Count);
        Assert.Equal([1, 2], call.Images[0].Bytes);
        Assert.Equal("image/png", call.Images[0].MimeType);
        Assert.Equal("a.png", call.Images[0].FileName);
        Assert.Equal([3, 4, 5], call.Images[1].Bytes);
        Assert.Equal("image/jpeg", call.Images[1].MimeType);
        Assert.Equal([6], call.Images[2].Bytes);
        Assert.Equal("image/webp", call.Images[2].MimeType);
    }

    [Fact]
    public async Task Generate_TooManyImages_Returns400_AndSkipsUpstream()
    {
        var provider = await SeedProviderAsync();
        var model = await SeedModelAsync(provider.Id);
        var client = new StubImageClient();
        var factory = new StubImageClientFactory(client);

        var result = await Create(factory, new ImageOptions { MaxImageCount = 2 }).Generate(
            "文本",
            Files(
                File([1], "image/png", "a.png"),
                File([1], "image/png", "b.png"),
                File([1], "image/png", "c.png")),
            model.Id.ToString());

        Assert.Equal("too many images", ErrorOf(result, StatusCodes.Status400BadRequest));
        Assert.Empty(client.Calls);
        Assert.Empty(factory.Types);
    }

    [Fact]
    public async Task Generate_ElevenImages_DefaultMaxImageCount_Returns400()
    {
        var files = Enumerable.Range(0, 11)
            .Select(i => File([1], "image/png", $"f{i}.png"))
            .ToArray();

        var result = await Create(new StubImageClientFactory(new StubImageClient()))
            .Generate("文本", Files(files), null);

        Assert.Equal("too many images", ErrorOf(result, StatusCodes.Status400BadRequest));
    }

    [Fact]
    public async Task Generate_SecondImageUnsupportedType_Returns400_AndSkipsUpstream()
    {
        var client = new StubImageClient();
        var result = await Create(new StubImageClientFactory(client)).Generate(
            "文本",
            Files(File([1], "image/png", "a.png"), File([1], "image/gif", "b.gif")),
            null);

        Assert.Equal("unsupported image type", ErrorOf(result, StatusCodes.Status400BadRequest));
        Assert.Empty(client.Calls);
    }

    [Fact]
    public async Task Generate_SecondImageTooLarge_Returns400_AndSkipsUpstream()
    {
        var client = new StubImageClient();
        var result = await Create(new StubImageClientFactory(client), new ImageOptions { MaxImageBytes = 2 })
            .Generate(
                "文本",
                Files(File([1], "image/png", "a.png"), File([1, 2, 3], "image/png", "b.png")),
                null);

        Assert.Equal("image too large", ErrorOf(result, StatusCodes.Status400BadRequest));
        Assert.Empty(client.Calls);
    }

    [Fact]
    public async Task Generate_ZeroBytePartsOnly_Returns400ImageRequired()
    {
        var client = new StubImageClient();
        var result = await Create(new StubImageClientFactory(client)).Generate(
            "文本", Files(File([], "image/png", "a.png"), File([], "image/png", "b.png")), null);

        Assert.Equal("image is required", ErrorOf(result, StatusCodes.Status400BadRequest));
        Assert.Empty(client.Calls);
    }

    [Fact]
    public async Task Generate_ZeroBytePartMixedWithValid_IgnoresZeroByte()
    {
        var provider = await SeedProviderAsync();
        var model = await SeedModelAsync(provider.Id);
        var client = new StubImageClient();

        var result = await Create(new StubImageClientFactory(client)).Generate(
            "文本",
            Files(File([], "image/png", "empty.png"), File([7, 8], "image/png", "ok.png")),
            model.Id.ToString());

        Assert.IsType<FileContentResult>(result);
        var call = Assert.Single(client.Calls);
        var image = Assert.Single(call.Images);
        Assert.Equal([7, 8], image.Bytes);
        Assert.Equal("ok.png", image.FileName);
    }

    [Fact]
    public async Task Generate_EmptyText_Returns400()
    {
        var result = await Create(new StubImageClientFactory(new StubImageClient()))
            .Generate("   ", Files(File([1], "image/png")), null);

        Assert.Equal("text is required", ErrorOf(result, StatusCodes.Status400BadRequest));
    }

    [Fact]
    public async Task Generate_MissingImage_Returns400()
    {
        var result = await Create(new StubImageClientFactory(new StubImageClient()))
            .Generate("文本", null, null);

        Assert.Equal("image is required", ErrorOf(result, StatusCodes.Status400BadRequest));
    }

    [Fact]
    public async Task Generate_EmptyImage_Returns400()
    {
        var result = await Create(new StubImageClientFactory(new StubImageClient()))
            .Generate("文本", Files(File([], "image/png")), null);

        Assert.Equal("image is required", ErrorOf(result, StatusCodes.Status400BadRequest));
    }

    [Fact]
    public async Task Generate_UnsupportedType_Returns400_AndSkipsUpstream()
    {
        var client = new StubImageClient();
        var result = await Create(new StubImageClientFactory(client))
            .Generate("文本", Files(File([1, 2, 3], "image/gif")), null);

        Assert.Equal("unsupported image type", ErrorOf(result, StatusCodes.Status400BadRequest));
        Assert.Empty(client.Calls);
    }

    [Fact]
    public async Task Generate_TooLarge_Returns400_AndSkipsUpstream()
    {
        var client = new StubImageClient();
        var result = await Create(new StubImageClientFactory(client), new ImageOptions { MaxImageBytes = 2 })
            .Generate("文本", Files(File([1, 2, 3], "image/png")), null);

        Assert.Equal("image too large", ErrorOf(result, StatusCodes.Status400BadRequest));
        Assert.Empty(client.Calls);
    }

    [Fact]
    public async Task Generate_InvalidModelId_Returns404()
    {
        var result = await Create(new StubImageClientFactory(new StubImageClient()))
            .Generate("文本", Files(File([1], "image/png")), ObjectId.GenerateNewId().ToString());

        Assert.Equal("model not found", ErrorOf(result, StatusCodes.Status404NotFound));

        var malformed = await Create(new StubImageClientFactory(new StubImageClient()))
            .Generate("文本", Files(File([1], "image/png")), "not-an-object-id");

        Assert.Equal("model not found", ErrorOf(malformed, StatusCodes.Status404NotFound));
    }

    [Fact]
    public async Task Generate_DisabledModel_Returns400()
    {
        var provider = await SeedProviderAsync();
        var model = await SeedModelAsync(provider.Id, enabled: false);

        var result = await Create(new StubImageClientFactory(new StubImageClient()))
            .Generate("文本", Files(File([1], "image/png")), model.Id.ToString());

        Assert.Equal("model disabled", ErrorOf(result, StatusCodes.Status400BadRequest));
    }

    [Fact]
    public async Task Generate_ModelMissingInputCapability_Returns400()
    {
        var provider = await SeedProviderAsync();
        var model = await SeedModelAsync(
            provider.Id, input: [TrainingModelCapabilities.Image], output: [TrainingModelCapabilities.Image]);

        var result = await Create(new StubImageClientFactory(new StubImageClient()))
            .Generate("文本", Files(File([1], "image/png")), model.Id.ToString());

        Assert.Equal("model does not support image input", ErrorOf(result, StatusCodes.Status400BadRequest));
    }

    [Fact]
    public async Task Generate_ModelMissingOutputCapability_Returns400()
    {
        var provider = await SeedProviderAsync();
        var model = await SeedModelAsync(
            provider.Id, input: [TrainingModelCapabilities.Text, TrainingModelCapabilities.Image],
            output: [TrainingModelCapabilities.Text]);

        var result = await Create(new StubImageClientFactory(new StubImageClient()))
            .Generate("文本", Files(File([1], "image/png")), model.Id.ToString());

        Assert.Equal("model does not support image output", ErrorOf(result, StatusCodes.Status400BadRequest));
    }

    [Fact]
    public async Task Generate_Default_NoCandidate_Returns409()
    {
        var result = await Create(new StubImageClientFactory(new StubImageClient()))
            .Generate("文本", Files(File([1], "image/png")), null);

        Assert.Equal("no image model available", ErrorOf(result, StatusCodes.Status409Conflict));
    }

    [Fact]
    public async Task Generate_Default_PicksSpecifiedModel()
    {
        var provider = await SeedProviderAsync();
        var first = await SeedModelAsync(provider.Id, createdAt: DateTime.UtcNow.AddMinutes(-10));
        var specified = await SeedModelAsync(provider.Id);
        await SetSpecifiedModelAsync(specified.Id);

        var client = new StubImageClient();
        var result = await Create(new StubImageClientFactory(client))
            .Generate("文本", Files(File([1], "image/png")), null);

        Assert.IsType<FileContentResult>(result);
        Assert.NotEqual(first.Id, Assert.Single(client.Calls).Model.Id);
        Assert.Equal(specified.Id, Assert.Single(client.Calls).Model.Id);
    }

    [Fact]
    public async Task Generate_Default_PicksFirstCandidateByCreatedAt()
    {
        var provider = await SeedProviderAsync();
        var first = await SeedModelAsync(provider.Id, createdAt: DateTime.UtcNow.AddMinutes(-10));
        await SeedModelAsync(provider.Id, createdAt: DateTime.UtcNow);

        var client = new StubImageClient();
        var result = await Create(new StubImageClientFactory(client))
            .Generate("文本", Files(File([1], "image/png")), null);

        Assert.IsType<FileContentResult>(result);
        Assert.Equal(first.Id, Assert.Single(client.Calls).Model.Id);
    }

    [Fact]
    public async Task Generate_UpstreamHttpError_Returns502()
    {
        var provider = await SeedProviderAsync();
        var model = await SeedModelAsync(provider.Id);
        var client = new StubImageClient { Throw = new HttpRequestException("boom") };

        var result = await Create(new StubImageClientFactory(client))
            .Generate("文本", Files(File([1], "image/png")), model.Id.ToString());

        Assert.Equal("model service unavailable", ErrorOf(result, StatusCodes.Status502BadGateway));
    }

    [Fact]
    public async Task Generate_UpstreamTimeout_Returns502()
    {
        var provider = await SeedProviderAsync();
        var model = await SeedModelAsync(provider.Id);
        var client = new StubImageClient { Throw = new TaskCanceledException("timeout") };

        var result = await Create(new StubImageClientFactory(client))
            .Generate("文本", Files(File([1], "image/png")), model.Id.ToString());

        Assert.Equal("model service unavailable", ErrorOf(result, StatusCodes.Status502BadGateway));
    }

    [Fact]
    public async Task Generate_InvalidModelOutput_Returns502()
    {
        var provider = await SeedProviderAsync();
        var model = await SeedModelAsync(provider.Id);
        var client = new StubImageClient { Throw = new ModelOutputException("bad") };

        var result = await Create(new StubImageClientFactory(client))
            .Generate("文本", Files(File([1], "image/png")), model.Id.ToString());

        Assert.Equal("invalid model output", ErrorOf(result, StatusCodes.Status502BadGateway));
    }

    [Fact]
    public async Task Generate_ClaudeProvider_Returns400_ImageOutputNotSupported()
    {
        var provider = await SeedProviderAsync(TrainingProviderTypes.Claude);
        var model = await SeedModelAsync(provider.Id);
        var factory = new ImageModelClientFactory(
            new StubHttpClientFactory(new HttpClient(new HttpMessageHandlerStub())),
            Microsoft.Extensions.Options.Options.Create(new ImageOptions()),
            NullLoggerFactory.Instance);

        var result = await Create(factory)
            .Generate("文本", Files(File([1], "image/png")), model.Id.ToString());

        Assert.Equal("model does not support image output", ErrorOf(result, StatusCodes.Status400BadRequest));
    }

    private sealed class HttpMessageHandlerStub : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("no upstream call expected");
    }
}
