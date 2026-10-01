using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using Z.Vtuber.Common.Persistence;
using Z.Vtuber.Common.Persistence.Entities;
using Z.Vtuber.Training.Ai;
using Z.Vtuber.Training.Clients;
using Z.Vtuber.Training.Configuration;
using Z.Vtuber.Training.Contracts;
using Z.Vtuber.Training.Controllers;

namespace Z.Vtuber.Tests;

/// <summary>
/// Plan 1.11：<see cref="TrainingItemToolsController.Recognize"/> 控制器直测（Testcontainers Mongo + 假音频/假模型）——
/// 200 <c>{text}</c> 且**不写库**、base64 音频与 prompt 确实发往所选模型、
/// Claude-like 音频 400、音频错误码、模型输出/传输错误码、dataset/item/model 404、禁用模型 400。
/// </summary>
public class TrainingRecognizeTests(MongoDbFixture fixture) : IClassFixture<MongoDbFixture>, IAsyncLifetime
{
    public Task InitializeAsync() => fixture.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private TrainingDatasetRepository Datasets => new(fixture.Database);

    private TrainingDatasetItemRepository Items => new(fixture.Database);

    private TrainingModelRepository Models => new(fixture.Database);

    private TrainingProviderRepository Providers => new(fixture.Database);

    private TrainingEmotionTaskRepository Tasks => new(fixture.Database);

    private TrainingItemToolsController CreateController(
        IRecordingAudioClient audio, IChatModelClientFactory aiFactory) =>
        WithContext(new TrainingItemToolsController(
            Datasets, Items, Models, Providers, audio, aiFactory, Tasks,
            NullLogger<TrainingItemToolsController>.Instance));

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

    private sealed class RecognizeStubAudioClient : IRecordingAudioClient
    {
        public AudioClip Clip { get; set; } = new([1, 2, 3, 4], "audio/mpeg");

        public Exception? Throw { get; set; }

        public int Calls { get; private set; }

        public string? LastAudioUri { get; private set; }

        public long? LastStartMs { get; private set; }

        public long? LastEndMs { get; private set; }

        public Task<AudioClip> GetClipAsync(
            string audioUri, long startMs, long? endMs, CancellationToken ct = default)
        {
            Calls++;
            LastAudioUri = audioUri;
            LastStartMs = startMs;
            LastEndMs = endMs;
            return Throw is null
                ? Task.FromResult(Clip)
                : Task.FromException<AudioClip>(Throw);
        }

        public Task<AudioClip> MergeClipsAsync(
            IReadOnlyList<AudioClipRequest> items, CancellationToken ct = default) =>
            throw new NotSupportedException("recognize does not merge audio");
    }

    private sealed class RecognizeStubChatClient : IChatModelClient
    {
        public string Raw { get; set; } = """{"text":"识别结果"}""";

        public Exception? Throw { get; set; }

        public List<ChatPrompt> Prompts { get; } = [];

        public TrainingProviderRecord? Provider { get; private set; }

        public TrainingModelRecord? Model { get; private set; }

        public Task<string> CompleteJsonAsync(
            TrainingProviderRecord provider, TrainingModelRecord model,
            ChatPrompt prompt, CancellationToken ct = default)
        {
            Provider = provider;
            Model = model;
            Prompts.Add(prompt);
            return Throw is not null
                ? Task.FromException<string>(Throw)
                : Task.FromResult(Raw);
        }
    }

    private sealed class RecognizeStubChatFactory(IChatModelClient client) : IChatModelClientFactory
    {
        public IChatModelClient Create(string providerType) =>
            providerType is TrainingProviderTypes.OpenAi or TrainingProviderTypes.Claude
                ? client
                : throw new ProviderTypeException($"unsupported provider type: {providerType}");
    }

    private async Task<ObjectId> SeedDatasetAsync()
    {
        var now = DateTime.UtcNow;
        var record = await Datasets.InsertAsync(new TrainingDatasetRecord
        {
            Name = $"ds-{Guid.NewGuid():N}", Type = "audio", CreatedAt = now, UpdatedAt = now,
        });
        return record.Id;
    }

    private async Task<TrainingDatasetItemRecord> SeedItemAsync(
        ObjectId datasetId, string text = "原句", string audioUri = "b/live/700001/recordings/a.flv")
    {
        var now = DateTime.UtcNow;
        var item = new TrainingDatasetItemRecord
        {
            DatasetId = datasetId,
            AudioType = "transcript",
            Source = "task-1",
            AudioUri = audioUri,
            AudioKey = $"transcript:task-1:{Guid.NewGuid():N}",
            StartMs = 1000,
            EndMs = 5000,
            Text = text,
            CreatedAt = now,
            UpdatedAt = now,
        };
        await Items.InsertManyIgnoreDuplicatesAsync([item]);
        return item;
    }

    private async Task<(TrainingProviderRecord Provider, TrainingModelRecord Model)> SeedModelAsync(
        string type = "openai", bool enabled = true)
    {
        var now = DateTime.UtcNow;
        var provider = await Providers.InsertAsync(new TrainingProviderRecord
        {
            Name = $"provider-{Guid.NewGuid():N}", Type = type, Endpoint = "http://llm",
            ApiKey = "secret", CreatedAt = now, UpdatedAt = now,
        });
        var model = await Models.InsertAsync(new TrainingModelRecord
        {
            ProviderId = provider.Id, Model = "gpt-test", Enabled = enabled,
            CreatedAt = now, UpdatedAt = now,
        });
        return (provider, model);
    }

    // ---- 成功路径（接口 11）----

    [Fact]
    public async Task Recognize_Valid_ReturnsTextAndDoesNotWriteItem()
    {
        var datasetId = await SeedDatasetAsync();
        var item = await SeedItemAsync(datasetId, text: "原句");
        var (provider, model) = await SeedModelAsync();
        var audio = new RecognizeStubAudioClient();
        var chat = new RecognizeStubChatClient
        {
            Raw = "```json\n{\"text\":\"识别出的新文本\"}\n```",
        };

        var result = await CreateController(audio, new RecognizeStubChatFactory(chat))
            .Recognize(datasetId.ToString(), item.Id.ToString(),
                new TrainingRecognizeRequest(model.Id.ToString()));

        Assert.Equal("识别出的新文本", PayloadOf(result).Text);

        // 音频取数参数正确，且 base64 + prompt 确实发往所选模型
        Assert.Equal(1, audio.Calls);
        Assert.Equal(item.AudioUri, audio.LastAudioUri);
        Assert.Equal(1000, audio.LastStartMs);
        Assert.Equal(5000, audio.LastEndMs);
        var prompt = Assert.Single(chat.Prompts);
        Assert.NotNull(prompt.Audio);
        Assert.Equal(Convert.ToBase64String(audio.Clip.Bytes), prompt.Audio!.Base64);
        Assert.Equal("audio/mpeg", prompt.Audio.MimeType);
        Assert.Contains("原句", prompt.User);
        Assert.Equal(provider.Id, chat.Provider!.Id);
        Assert.Equal(model.Id, chat.Model!.Id);

        // 不写库：条目文本与扩展字段保持原样
        var stored = await Items.GetByIdAsync(datasetId, item.Id);
        Assert.Equal("原句", stored!.Text);
        Assert.Null(stored.Extensions);
    }

    [Fact]
    public async Task Recognize_ClaudeLikeWithAudio_Returns400ModelDoesNotSupportAudioInput()
    {
        var datasetId = await SeedDatasetAsync();
        var item = await SeedItemAsync(datasetId);
        var (_, model) = await SeedModelAsync(type: "claude");
        var audio = new RecognizeStubAudioClient();
        // 真实 Claude 适配器：音频非空直接抛 ModelAudioNotSupportedException（不发起 HTTP）
        var claude = new ClaudeLikeChatClient(
            new HttpClient(), new AiOptions(), NullLogger<ClaudeLikeChatClient>.Instance);

        var result = await CreateController(audio, new RecognizeStubChatFactory(claude))
            .Recognize(datasetId.ToString(), item.Id.ToString(),
                new TrainingRecognizeRequest(model.Id.ToString()));

        Assert.Equal("model does not support audio input",
            ErrorOf(result, StatusCodes.Status400BadRequest));
        Assert.Equal("原句", (await Items.GetByIdAsync(datasetId, item.Id))!.Text);
    }

    // ---- 音频错误码 ----

    [Fact]
    public async Task Recognize_EmptyAudioUri_Returns400AudioUriIsEmpty()
    {
        var datasetId = await SeedDatasetAsync();
        var item = await SeedItemAsync(datasetId);
        var (_, model) = await SeedModelAsync();
        var audio = new RecognizeStubAudioClient { Throw = new ArgumentException("audio uri is empty") };

        var result = await CreateController(audio, new RecognizeStubChatFactory(new RecognizeStubChatClient()))
            .Recognize(datasetId.ToString(), item.Id.ToString(),
                new TrainingRecognizeRequest(model.Id.ToString()));

        Assert.Equal("audio uri is empty", ErrorOf(result, StatusCodes.Status400BadRequest));
    }

    [Fact]
    public async Task Recognize_AudioUnavailable_Returns502AudioUnavailable()
    {
        var datasetId = await SeedDatasetAsync();
        var item = await SeedItemAsync(datasetId);
        var (_, model) = await SeedModelAsync();
        var audio = new RecognizeStubAudioClient
        {
            Throw = new AudioUnavailableException("recording down"),
        };

        var result = await CreateController(audio, new RecognizeStubChatFactory(new RecognizeStubChatClient()))
            .Recognize(datasetId.ToString(), item.Id.ToString(),
                new TrainingRecognizeRequest(model.Id.ToString()));

        Assert.Equal("audio unavailable", ErrorOf(result, StatusCodes.Status502BadGateway));
    }

    [Fact]
    public async Task Recognize_AudioTooLarge_Returns400AudioTooLarge()
    {
        var datasetId = await SeedDatasetAsync();
        var item = await SeedItemAsync(datasetId);
        var (_, model) = await SeedModelAsync();
        var audio = new RecognizeStubAudioClient { Throw = new AudioTooLargeException("too big") };

        var result = await CreateController(audio, new RecognizeStubChatFactory(new RecognizeStubChatClient()))
            .Recognize(datasetId.ToString(), item.Id.ToString(),
                new TrainingRecognizeRequest(model.Id.ToString()));

        Assert.Equal("audio too large", ErrorOf(result, StatusCodes.Status400BadRequest));
    }

    // ---- 模型输出/传输错误码 ----

    [Fact]
    public async Task Recognize_NonJsonOutput_Returns502InvalidModelOutput()
    {
        var datasetId = await SeedDatasetAsync();
        var item = await SeedItemAsync(datasetId);
        var (_, model) = await SeedModelAsync();
        var chat = new RecognizeStubChatClient { Raw = "抱歉，我无法输出 JSON" };

        var result = await CreateController(new RecognizeStubAudioClient(), new RecognizeStubChatFactory(chat))
            .Recognize(datasetId.ToString(), item.Id.ToString(),
                new TrainingRecognizeRequest(model.Id.ToString()));

        Assert.Equal("invalid model output", ErrorOf(result, StatusCodes.Status502BadGateway));
        Assert.Equal("原句", (await Items.GetByIdAsync(datasetId, item.Id))!.Text);
    }

    [Fact]
    public async Task Recognize_CompleteJsonThrowsModelOutputException_Returns502InvalidModelOutput()
    {
        var datasetId = await SeedDatasetAsync();
        var item = await SeedItemAsync(datasetId);
        var (_, model) = await SeedModelAsync();
        var chat = new RecognizeStubChatClient
        {
            Throw = new ModelOutputException("bad envelope"),
        };

        var result = await CreateController(new RecognizeStubAudioClient(), new RecognizeStubChatFactory(chat))
            .Recognize(datasetId.ToString(), item.Id.ToString(),
                new TrainingRecognizeRequest(model.Id.ToString()));

        Assert.Equal("invalid model output", ErrorOf(result, StatusCodes.Status502BadGateway));
    }

    [Fact]
    public async Task Recognize_TransportFailure_Returns502ModelServiceUnavailable()
    {
        var datasetId = await SeedDatasetAsync();
        var item = await SeedItemAsync(datasetId);
        var (_, model) = await SeedModelAsync();
        var chat = new RecognizeStubChatClient { Throw = new HttpRequestException("model down") };

        var result = await CreateController(new RecognizeStubAudioClient(), new RecognizeStubChatFactory(chat))
            .Recognize(datasetId.ToString(), item.Id.ToString(),
                new TrainingRecognizeRequest(model.Id.ToString()));

        Assert.Equal("model service unavailable", ErrorOf(result, StatusCodes.Status502BadGateway));
    }

    [Fact]
    public async Task Recognize_Timeout_Returns502ModelServiceUnavailable()
    {
        var datasetId = await SeedDatasetAsync();
        var item = await SeedItemAsync(datasetId);
        var (_, model) = await SeedModelAsync();
        var chat = new RecognizeStubChatClient { Throw = new TaskCanceledException("timeout") };

        var result = await CreateController(new RecognizeStubAudioClient(), new RecognizeStubChatFactory(chat))
            .Recognize(datasetId.ToString(), item.Id.ToString(),
                new TrainingRecognizeRequest(model.Id.ToString()));

        Assert.Equal("model service unavailable", ErrorOf(result, StatusCodes.Status502BadGateway));
    }

    [Fact]
    public async Task Recognize_UnknownProviderType_Returns400UnsupportedProviderType()
    {
        var datasetId = await SeedDatasetAsync();
        var item = await SeedItemAsync(datasetId);
        var (_, model) = await SeedModelAsync(type: "gemini");
        var factory = new RecognizeStubChatFactory(new RecognizeStubChatClient());

        var result = await CreateController(new RecognizeStubAudioClient(), factory)
            .Recognize(datasetId.ToString(), item.Id.ToString(),
                new TrainingRecognizeRequest(model.Id.ToString()));

        Assert.Equal("unsupported provider type", ErrorOf(result, StatusCodes.Status400BadRequest));
    }

    // ---- 404 / 禁用 ----

    [Fact]
    public async Task Recognize_UnknownModel_Returns404ModelNotFound()
    {
        var datasetId = await SeedDatasetAsync();
        var item = await SeedItemAsync(datasetId);
        var controller = CreateController(
            new RecognizeStubAudioClient(), new RecognizeStubChatFactory(new RecognizeStubChatClient()));

        var unknown = await controller.Recognize(datasetId.ToString(), item.Id.ToString(),
            new TrainingRecognizeRequest(ObjectId.GenerateNewId().ToString()));
        Assert.Equal("model not found", ErrorOf(unknown, StatusCodes.Status404NotFound));

        var invalid = await controller.Recognize(datasetId.ToString(), item.Id.ToString(),
            new TrainingRecognizeRequest("not-an-objectid"));
        Assert.Equal("model not found", ErrorOf(invalid, StatusCodes.Status404NotFound));
    }

    [Fact]
    public async Task Recognize_MissingProvider_Returns404ModelNotFound()
    {
        var datasetId = await SeedDatasetAsync();
        var item = await SeedItemAsync(datasetId);
        var now = DateTime.UtcNow;
        var model = await Models.InsertAsync(new TrainingModelRecord
        {
            ProviderId = ObjectId.GenerateNewId(), Model = "orphan", Enabled = true,
            CreatedAt = now, UpdatedAt = now,
        });

        var result = await CreateController(
                new RecognizeStubAudioClient(), new RecognizeStubChatFactory(new RecognizeStubChatClient()))
            .Recognize(datasetId.ToString(), item.Id.ToString(),
                new TrainingRecognizeRequest(model.Id.ToString()));

        Assert.Equal("model not found", ErrorOf(result, StatusCodes.Status404NotFound));
    }

    [Fact]
    public async Task Recognize_DisabledModel_Returns400ModelDisabled()
    {
        var datasetId = await SeedDatasetAsync();
        var item = await SeedItemAsync(datasetId);
        var (_, model) = await SeedModelAsync(enabled: false);

        var result = await CreateController(
                new RecognizeStubAudioClient(), new RecognizeStubChatFactory(new RecognizeStubChatClient()))
            .Recognize(datasetId.ToString(), item.Id.ToString(),
                new TrainingRecognizeRequest(model.Id.ToString()));

        Assert.Equal("model disabled", ErrorOf(result, StatusCodes.Status400BadRequest));
    }

    [Fact]
    public async Task Recognize_UnknownOrInvalidDataset_Returns404DatasetNotFound()
    {
        var controller = CreateController(
            new RecognizeStubAudioClient(), new RecognizeStubChatFactory(new RecognizeStubChatClient()));
        var modelId = ObjectId.GenerateNewId().ToString();

        var unknown = await controller.Recognize(
            ObjectId.GenerateNewId().ToString(), ObjectId.GenerateNewId().ToString(),
            new TrainingRecognizeRequest(modelId));
        Assert.Equal("dataset not found", ErrorOf(unknown, StatusCodes.Status404NotFound));

        var invalid = await controller.Recognize(
            "not-an-objectid", ObjectId.GenerateNewId().ToString(),
            new TrainingRecognizeRequest(modelId));
        Assert.Equal("dataset not found", ErrorOf(invalid, StatusCodes.Status404NotFound));
    }

    [Fact]
    public async Task Recognize_UnknownItem_Returns404ItemNotFound()
    {
        var datasetId = await SeedDatasetAsync();
        var (_, model) = await SeedModelAsync();
        var controller = CreateController(
            new RecognizeStubAudioClient(), new RecognizeStubChatFactory(new RecognizeStubChatClient()));

        var result = await controller.Recognize(datasetId.ToString(),
            ObjectId.GenerateNewId().ToString(), new TrainingRecognizeRequest(model.Id.ToString()));

        Assert.Equal("item not found", ErrorOf(result, StatusCodes.Status404NotFound));
    }
}
