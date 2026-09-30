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
using Z.Vtuber.Training.Synthesis;
using Z.Vtuber.Training.Vectors;

namespace Z.Vtuber.Tests;

/// <summary>
/// 音频生成端点（控制器直测 + 真 Mongo + 假 embedding/向量库/音频/TTS/模型）：
/// 200 <c>audio/wav</c> + 命中头、400/404/409/502 错误矩阵、vectors/rebuild。
/// </summary>
public class TrainingSynthesizeApiTests(MongoDbFixture fixture, MilvusFixture milvus)
    : IClassFixture<MongoDbFixture>, IClassFixture<MilvusFixture>, IAsyncLifetime
{
    public Task InitializeAsync() => fixture.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private TrainingDatasetRepository Datasets => new(fixture.Database);

    private TrainingDatasetItemRepository Items => new(fixture.Database);

    private TrainingProviderRepository Providers => new(fixture.Database);

    private TrainingModelRepository Models => new(fixture.Database);

    private const string EmotionJson =
        """{"emotionControlWeight":5,"emotionDescription":"开心","happy":0.9,"angry":0,"sad":0,"afraid":0,"disgusted":0,"melancholic":0,"surprised":0,"calm":0}""";

    private sealed class ApiStubChatClient : IChatModelClient
    {
        public Task<string> CompleteJsonAsync(
            TrainingProviderRecord provider, TrainingModelRecord model, ChatPrompt prompt, CancellationToken ct = default) =>
            Task.FromResult(EmotionJson);
    }

    private sealed class ApiStubChatFactory : IChatModelClientFactory
    {
        public IChatModelClient Create(string providerType) => new ApiStubChatClient();
    }

    private TrainingSynthesizeController Create(
        IVectorStore store, FakeEmbeddingClient embedding, StubAudioClient audio,
        StubTtsClient tts, TtsOptions? ttsOptions = null, SynthesizeOptions? synthesizeOptions = null)
    {
        var synthesize = new SynthesizeService(
            Items, Providers, new ApiStubChatFactory(), audio, store, embedding, tts,
            synthesizeOptions ?? new SynthesizeOptions(),
            NullLogger<SynthesizeService>.Instance);
        var vectors = new VectorSyncService(Items, store, embedding, NullLogger<VectorSyncService>.Instance);
        var controller = new TrainingSynthesizeController(
            Datasets, Models, synthesize, vectors,
            Microsoft.Extensions.Options.Options.Create(ttsOptions ?? new TtsOptions()).Value,
            synthesizeOptions ?? new SynthesizeOptions(),
            NullLogger<TrainingSynthesizeController>.Instance);
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
        controller.HttpContext.Response.Body = new MemoryStream();
        return controller;
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

    private async Task<TrainingDatasetItemRecord> SeedItemAsync(ObjectId datasetId, string text)
    {
        var now = DateTime.UtcNow;
        var item = new TrainingDatasetItemRecord
        {
            DatasetId = datasetId,
            AudioType = "transcript",
            Source = "task",
            AudioUri = $"b/live/{Guid.NewGuid():N}/recordings/{Guid.NewGuid():N}.flv",
            AudioKey = $"k-{Guid.NewGuid():N}",
            StartMs = 0,
            EndMs = 1000,
            Text = text,
            CreatedAt = now,
            UpdatedAt = now,
        };
        await Items.InsertManyIgnoreDuplicatesAsync([item]);
        return item;
    }

    private async Task<TrainingModelRecord> SeedModelAsync(bool enabled = true)
    {
        var now = DateTime.UtcNow;
        var provider = await Providers.InsertAsync(new TrainingProviderRecord
        {
            Name = $"p-{Guid.NewGuid():N}", Type = "openai", Endpoint = "http://llm", ApiKey = "k",
            CreatedAt = now, UpdatedAt = now,
        });
        return await Models.InsertAsync(new TrainingModelRecord
        {
            ProviderId = provider.Id, Model = "gpt", Enabled = enabled, CreatedAt = now, UpdatedAt = now,
        });
    }

    private static string ErrorOf(IActionResult result, int statusCode)
    {
        var objectResult = Assert.IsAssignableFrom<ObjectResult>(result);
        Assert.Equal(statusCode, objectResult.StatusCode);
        return Assert.IsType<ErrorResponse>(objectResult.Value).Error;
    }

    private static string? Header(Microsoft.AspNetCore.Mvc.ControllerBase controller, string name) =>
        controller.Response.Headers.TryGetValue(name, out var value) ? value.ToString() : null;

    private (InMemoryVectorStore Store, StubAudioClient Audio, StubTtsClient Tts) Arrange(
        ObjectId datasetId, TrainingDatasetItemRecord speaker)
    {
        var store = new InMemoryVectorStore();
        store.SeedText(datasetId, speaker.Id, speaker.AudioKey, [1f, 0f, 0f]);
        var audio = new StubAudioClient();
        audio.Clips[speaker.AudioUri] = new AudioClip([1, 2, 3], "audio/mpeg");
        return (store, audio, new StubTtsClient { Bytes = [7, 7] });
    }

    [Fact]
    public async Task Synthesize_Success_ReturnsWavAndHitHeaders()
    {
        var datasetId = await SeedDatasetAsync();
        var speaker = await SeedItemAsync(datasetId, "你好");
        var model = await SeedModelAsync();
        var (store, audio, tts) = Arrange(datasetId, speaker);
        var controller = Create(store, new FakeEmbeddingClient(), audio, tts);

        var result = await controller.Synthesize(
            datasetId.ToString(), new TrainingSynthesizeRequest("要合成的文本", null, model.Id.ToString()));

        var file = Assert.IsType<FileContentResult>(result);
        Assert.Equal("audio/wav", file.ContentType);
        Assert.Equal([7, 7], file.FileContents);
        var request = Assert.Single(tts.Requests);
        Assert.Equal("要合成的文本", request.Text);
        Assert.Equal("ZH", request.Lang);
        Assert.Equal([1, 2, 3], request.Speaker.Bytes);
        Assert.Equal(speaker.AudioKey, Header(controller, "X-Speaker-Audio-Key"));
    }

    [Fact]
    public async Task Synthesize_SetsHitHeaders()
    {
        var datasetId = await SeedDatasetAsync();
        var speaker = await SeedItemAsync(datasetId, "你好");
        var model = await SeedModelAsync();
        var (store, audio, tts) = Arrange(datasetId, speaker);
        var controller = Create(store, new FakeEmbeddingClient(), audio, tts);

        await controller.Synthesize(datasetId.ToString(), new TrainingSynthesizeRequest("文本", null, model.Id.ToString()));

        Assert.Equal(speaker.AudioKey, Header(controller, "X-Speaker-Audio-Key"));
        Assert.Null(Header(controller, "X-Emo-Audio-Key"));
    }

    [Fact]
    public async Task Synthesize_EmptyText_Returns400()
    {
        var datasetId = await SeedDatasetAsync();

        var result = await Create(new InMemoryVectorStore(), new FakeEmbeddingClient(), new StubAudioClient(), new StubTtsClient())
            .Synthesize(datasetId.ToString(), new TrainingSynthesizeRequest("   ", null, null));

        Assert.Equal("text is required", ErrorOf(result, StatusCodes.Status400BadRequest));
    }

    [Fact]
    public async Task Synthesize_InvalidLang_Returns400()
    {
        var datasetId = await SeedDatasetAsync();

        var result = await Create(new InMemoryVectorStore(), new FakeEmbeddingClient(), new StubAudioClient(), new StubTtsClient())
            .Synthesize(datasetId.ToString(), new TrainingSynthesizeRequest("文本", "FR", null));

        Assert.Equal("invalid lang", ErrorOf(result, StatusCodes.Status400BadRequest));
    }

    [Fact]
    public async Task Synthesize_UnknownDataset_Returns404()
    {
        var result = await Create(new InMemoryVectorStore(), new FakeEmbeddingClient(), new StubAudioClient(), new StubTtsClient())
            .Synthesize(ObjectId.GenerateNewId().ToString(), new TrainingSynthesizeRequest("文本", null, null));

        Assert.Equal("dataset not found", ErrorOf(result, StatusCodes.Status404NotFound));
    }

    [Fact]
    public async Task Synthesize_UnknownModel_Returns404_AndDisabledReturns400()
    {
        var datasetId = await SeedDatasetAsync();
        var disabled = await SeedModelAsync(enabled: false);
        var controller = Create(new InMemoryVectorStore(), new FakeEmbeddingClient(), new StubAudioClient(), new StubTtsClient());

        var unknown = await controller.Synthesize(
            datasetId.ToString(), new TrainingSynthesizeRequest("文本", null, ObjectId.GenerateNewId().ToString()));
        Assert.Equal("model not found", ErrorOf(unknown, StatusCodes.Status404NotFound));

        var disabledResult = await controller.Synthesize(
            datasetId.ToString(), new TrainingSynthesizeRequest("文本", null, disabled.Id.ToString()));
        Assert.Equal("model disabled", ErrorOf(disabledResult, StatusCodes.Status400BadRequest));
    }

    [Fact]
    public async Task Synthesize_NoSpeakerReference_Returns409()
    {
        var datasetId = await SeedDatasetAsync();
        var model = await SeedModelAsync();

        var result = await Create(new InMemoryVectorStore(), new FakeEmbeddingClient(), new StubAudioClient(), new StubTtsClient())
            .Synthesize(datasetId.ToString(), new TrainingSynthesizeRequest("文本", null, model.Id.ToString()));

        Assert.Equal("no speaker reference", ErrorOf(result, StatusCodes.Status409Conflict));
    }

    [Fact]
    public async Task Synthesize_EmbeddingUnavailable_Returns502()
    {
        var datasetId = await SeedDatasetAsync();
        var speaker = await SeedItemAsync(datasetId, "你好");
        var model = await SeedModelAsync();
        var (store, audio, tts) = Arrange(datasetId, speaker);

        var result = await Create(store, new FakeEmbeddingClient { Throw = new EmbeddingUnavailableException("down") }, audio, tts)
            .Synthesize(datasetId.ToString(), new TrainingSynthesizeRequest("文本", null, model.Id.ToString()));

        Assert.Equal("embedding service unavailable", ErrorOf(result, StatusCodes.Status502BadGateway));
    }

    [Fact]
    public async Task Synthesize_AudioUnavailable_Returns502()
    {
        var datasetId = await SeedDatasetAsync();
        var speaker = await SeedItemAsync(datasetId, "你好");
        var model = await SeedModelAsync();
        var (store, audio, tts) = Arrange(datasetId, speaker);
        audio.FailingUris.Add(speaker.AudioUri);

        var result = await Create(store, new FakeEmbeddingClient(), audio, tts)
            .Synthesize(datasetId.ToString(), new TrainingSynthesizeRequest("文本", null, model.Id.ToString()));

        Assert.Equal("audio unavailable", ErrorOf(result, StatusCodes.Status502BadGateway));
    }

    [Fact]
    public async Task Synthesize_TtsUnavailable_Returns502()
    {
        var datasetId = await SeedDatasetAsync();
        var speaker = await SeedItemAsync(datasetId, "你好");
        var model = await SeedModelAsync();
        var (store, audio, tts) = Arrange(datasetId, speaker);
        tts.Throw = new TtsUnavailableException("down");

        var result = await Create(store, new FakeEmbeddingClient(), audio, tts)
            .Synthesize(datasetId.ToString(), new TrainingSynthesizeRequest("文本", null, model.Id.ToString()));

        Assert.Equal("tts service unavailable", ErrorOf(result, StatusCodes.Status502BadGateway));
    }

    [Fact]
    public async Task RebuildVectors_ReturnsUpdated()
    {
        var datasetId = await SeedDatasetAsync();
        await SeedItemAsync(datasetId, "文本一");
        await SeedItemAsync(datasetId, "");
        var store = new InMemoryVectorStore();

        var result = await Create(store, new FakeEmbeddingClient(), new StubAudioClient(), new StubTtsClient())
            .RebuildVectors(datasetId.ToString());

        var payload = Assert.IsType<TrainingVectorsRebuildResultDto>(
            Assert.IsAssignableFrom<ObjectResult>(result.Result).Value);
        Assert.Equal(1, payload.Updated);
        Assert.True(store.EnsureCalled);
    }

    [Fact]
    public async Task RebuildVectors_UnknownDataset_Returns404()
    {
        var result = await Create(new InMemoryVectorStore(), new FakeEmbeddingClient(), new StubAudioClient(), new StubTtsClient())
            .RebuildVectors(ObjectId.GenerateNewId().ToString());

        var objectResult = Assert.IsAssignableFrom<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status404NotFound, objectResult.StatusCode);
        Assert.Equal("dataset not found", Assert.IsType<ErrorResponse>(objectResult.Value).Error);
    }

    [Fact]
    public async Task RebuildVectors_EmbeddingUnavailable_Returns502()
    {
        var datasetId = await SeedDatasetAsync();
        await SeedItemAsync(datasetId, "文本");

        var result = await Create(
                new InMemoryVectorStore(),
                new FakeEmbeddingClient { Throw = new EmbeddingUnavailableException("down") },
                new StubAudioClient(), new StubTtsClient())
            .RebuildVectors(datasetId.ToString());

        var objectResult = Assert.IsAssignableFrom<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status502BadGateway, objectResult.StatusCode);
        Assert.Equal("embedding service unavailable", Assert.IsType<ErrorResponse>(objectResult.Value).Error);
    }

    // ===== 真 Milvus 链路（Testcontainers milvusdb/milvus）：rebuild 重建 + 检索 top-1 命中 speaker =====

    [Fact]
    public async Task Synthesize_RealMilvus_RebuildThenTop1Search_PicksSpeaker()
    {
        var datasetId = await SeedDatasetAsync();
        var target = await SeedItemAsync(datasetId, "target");
        var other = await SeedItemAsync(datasetId, "other");
        var model = await SeedModelAsync();

        var store = milvus.CreateStore(dimensions: 3);
        await store.EnsureCollectionsAsync();

        // 文本→向量：target/query → [1,0,0]，other → [0,1,0]（真检索须命中 target）
        var embedding = new FakeEmbeddingClient
        {
            Provider = t => t.Contains("target") ? [1f, 0f, 0f] : [0f, 1f, 0f],
        };

        var audio = new StubAudioClient();
        audio.Clips[target.AudioUri] = new AudioClip([1, 2, 3], "audio/mpeg");
        audio.Clips[other.AudioUri] = new AudioClip([4, 5, 6], "audio/mpeg");
        var tts = new StubTtsClient { Bytes = [7, 7] };

        // Mongo → embedding → 真 Milvus 重建
        var sync = new VectorSyncService(Items, store, embedding, NullLogger<VectorSyncService>.Instance);
        await sync.RebuildAsync(datasetId);

        // 同一真向量库跑合成：query 向量 [1,0,0] 检索 top-1 命中 target（而非 other）
        var controller = Create(store, embedding, audio, tts);
        var result = await controller.Synthesize(
            datasetId.ToString(), new TrainingSynthesizeRequest("target query", null, model.Id.ToString()));

        var file = Assert.IsType<FileContentResult>(result);
        Assert.Equal("audio/wav", file.ContentType);
        Assert.Equal([7, 7], file.FileContents);
        Assert.Equal(target.AudioKey, Header(controller, "X-Speaker-Audio-Key"));
    }
}
