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
using Z.Vtuber.Training.FeaturePoints;
using Z.Vtuber.Training.Synthesis;

namespace Z.Vtuber.Tests;

/// <summary>
/// 音频生成端点（控制器直测 + 真 Mongo + 假音频/语音模型/情绪模型）：
/// 200 <c>audio/wav</c> + 命中头、400/404/409/502 错误矩阵、语音模型功能点解析。
/// </summary>
public class TrainingSynthesizeApiTests(MongoDbFixture fixture)
    : IClassFixture<MongoDbFixture>, IAsyncLifetime
{
    public Task InitializeAsync() => fixture.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private TrainingDatasetRepository Datasets => new(fixture.Database);

    private TrainingDatasetItemRepository Items => new(fixture.Database);

    private TrainingDatasetItemSegmentRepository SegmentRepository => new(fixture.Database);

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

    /// <summary>语音客户端工厂替身：openai 返回固定替身；其他类型按真实派发语义抛不支持（400）。</summary>
    private sealed class ApiStubSpeechFactory(ISpeechModelClient client) : ISpeechModelClientFactory
    {
        public ISpeechModelClient Create(string providerType) =>
            providerType?.Trim().ToLowerInvariant() == "openai"
                ? client
                : throw new ModelAudioOutputNotSupportedException();
    }

    private TrainingSynthesizeController Create(
        StubAudioClient audio, StubSpeechClient speech,
        SpeechOptions? speechOptions = null, SynthesizeOptions? synthesizeOptions = null)
    {
        var synthesize = new SynthesizeService(
            Items, SegmentRepository, Providers, new ApiStubChatFactory(), audio,
            synthesizeOptions ?? new SynthesizeOptions(),
            new AiOptions(),
            NullLogger<SynthesizeService>.Instance);
        var resolver = new TrainingModelResolver(
            new TrainingFeaturePointRepository(fixture.Database), Models, Providers);
        var controller = new TrainingSynthesizeController(
            Datasets, Models, Providers, synthesize,
            speechOptions ?? new SpeechOptions(),
            new ApiStubSpeechFactory(speech),
            resolver,
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

    private async Task<TrainingDatasetItemRecord> SeedItemAsync(
        ObjectId datasetId, string text, string? audioUri = null, EmotionResult? emotion = null)
    {
        var now = DateTime.UtcNow;
        var item = new TrainingDatasetItemRecord
        {
            DatasetId = datasetId,
            AudioType = "transcript",
            Source = "task",
            AudioUri = audioUri ?? $"b/live/{Guid.NewGuid():N}/recordings/{Guid.NewGuid():N}.flv",
            AudioKey = $"k-{Guid.NewGuid():N}",
            StartMs = 0,
            EndMs = 1000,
            Text = text,
            Extensions = emotion is null
                ? null
                : new BsonDocument { { "emotion", TrainingMapping.ToEmotionDocument(emotion) } },
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

    /// <summary>登记一个「输入 audio,text / 输出 audio」的可用语音模型（功能点 speech-synthesize 默认候选）。</summary>
    private async Task<TrainingModelRecord> SeedSpeechModelAsync(
        bool enabled = true, string providerType = "openai")
    {
        var now = DateTime.UtcNow;
        var provider = await Providers.InsertAsync(new TrainingProviderRecord
        {
            Name = $"speech-{Guid.NewGuid():N}", Type = providerType, Endpoint = "http://llm", ApiKey = "k",
            CreatedAt = now, UpdatedAt = now,
        });
        return await Models.InsertAsync(new TrainingModelRecord
        {
            ProviderId = provider.Id, Model = "speech", Enabled = enabled,
            InputCapabilities = ["audio", "text"],
            OutputCapabilities = ["audio"],
            CreatedAt = now, UpdatedAt = now,
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

    private (StubAudioClient Audio, StubSpeechClient Speech) Arrange(TrainingDatasetItemRecord speaker)
    {
        var audio = new StubAudioClient();
        audio.Clips[speaker.AudioUri] = new AudioClip([1, 2, 3], "audio/mpeg");
        return (audio, new StubSpeechClient { Bytes = [7, 7] });
    }

    [Fact]
    public async Task Synthesize_Success_ReturnsWavAndHitHeaders()
    {
        var datasetId = await SeedDatasetAsync();
        var speaker = await SeedItemAsync(datasetId, "你好");
        var model = await SeedModelAsync();
        await SeedSpeechModelAsync();
        var (audio, speech) = Arrange(speaker);
        var controller = Create(audio, speech);

        var result = await controller.Synthesize(
            datasetId.ToString(), new TrainingSynthesizeRequest("要合成的文本", null, model.Id.ToString()), "llm");

        var file = Assert.IsType<FileContentResult>(result);
        Assert.Equal("audio/wav", file.ContentType);
        Assert.Equal([7, 7], file.FileContents);
        var request = Assert.Single(speech.Requests);
        Assert.Equal("要合成的文本", request.Text);
        Assert.Equal("ZH", request.Lang);
        Assert.Equal([1, 2, 3], request.ReferenceAudio);
        Assert.Equal(speaker.AudioKey, Header(controller, "X-Speaker-Audio-Key"));
    }

    [Fact]
    public async Task Synthesize_SetsHitHeaders()
    {
        var datasetId = await SeedDatasetAsync();
        var speaker = await SeedItemAsync(datasetId, "你好");
        var model = await SeedModelAsync();
        await SeedSpeechModelAsync();
        var (audio, speech) = Arrange(speaker);
        var controller = Create(audio, speech);

        await controller.Synthesize(
            datasetId.ToString(), new TrainingSynthesizeRequest("文本", null, model.Id.ToString()), "llm");

        Assert.Equal(speaker.AudioKey, Header(controller, "X-Speaker-Audio-Key"));
        Assert.Null(Header(controller, "X-Emo-Audio-Key"));
    }

    [Fact]
    public async Task Synthesize_EmptyText_Returns400()
    {
        var datasetId = await SeedDatasetAsync();

        var result = await Create(new StubAudioClient(), new StubSpeechClient())
            .Synthesize(datasetId.ToString(), new TrainingSynthesizeRequest("   ", null, null));

        Assert.Equal("text is required", ErrorOf(result, StatusCodes.Status400BadRequest));
    }

    [Fact]
    public async Task Synthesize_InvalidLang_Returns400()
    {
        var datasetId = await SeedDatasetAsync();

        var result = await Create(new StubAudioClient(), new StubSpeechClient())
            .Synthesize(datasetId.ToString(), new TrainingSynthesizeRequest("文本", "FR", null));

        Assert.Equal("invalid lang", ErrorOf(result, StatusCodes.Status400BadRequest));
    }

    [Fact]
    public async Task Synthesize_UnknownDataset_Returns404()
    {
        var result = await Create(new StubAudioClient(), new StubSpeechClient())
            .Synthesize(ObjectId.GenerateNewId().ToString(), new TrainingSynthesizeRequest("文本", null, null));

        Assert.Equal("dataset not found", ErrorOf(result, StatusCodes.Status404NotFound));
    }

    [Fact]
    public async Task Synthesize_UnknownModel_Returns404_AndDisabledReturns400()
    {
        var datasetId = await SeedDatasetAsync();
        var disabled = await SeedModelAsync(enabled: false);
        var controller = Create(new StubAudioClient(), new StubSpeechClient());

        var unknown = await controller.Synthesize(
            datasetId.ToString(), new TrainingSynthesizeRequest("文本", null, ObjectId.GenerateNewId().ToString()), "llm");
        Assert.Equal("model not found", ErrorOf(unknown, StatusCodes.Status404NotFound));

        var disabledResult = await controller.Synthesize(
            datasetId.ToString(), new TrainingSynthesizeRequest("文本", null, disabled.Id.ToString()), "llm");
        Assert.Equal("model disabled", ErrorOf(disabledResult, StatusCodes.Status400BadRequest));
    }

    [Fact]
    public async Task Synthesize_NoSpeechModelAvailable_Returns409()
    {
        var datasetId = await SeedDatasetAsync();
        await SeedItemAsync(datasetId, "你好");
        // 未登记任何「输入 audio,text / 输出 audio」可用模型。

        var result = await Create(new StubAudioClient(), new StubSpeechClient())
            .Synthesize(datasetId.ToString(), new TrainingSynthesizeRequest("文本", null, null));

        Assert.Equal("no speech model available", ErrorOf(result, StatusCodes.Status409Conflict));
    }

    [Fact]
    public async Task Synthesize_ModelWithoutAudioOutput_Returns400()
    {
        var datasetId = await SeedDatasetAsync();
        await SeedItemAsync(datasetId, "你好");
        await SeedSpeechModelAsync(providerType: "claude");

        var result = await Create(new StubAudioClient(), new StubSpeechClient())
            .Synthesize(datasetId.ToString(), new TrainingSynthesizeRequest("文本", null, null));

        Assert.Equal("model does not support audio output", ErrorOf(result, StatusCodes.Status400BadRequest));
    }

    [Fact]
    public async Task Synthesize_NoSpeakerReference_Returns409()
    {
        var datasetId = await SeedDatasetAsync();
        await SeedSpeechModelAsync();

        var result = await Create(new StubAudioClient(), new StubSpeechClient())
            .Synthesize(datasetId.ToString(), new TrainingSynthesizeRequest("文本", null, null));

        Assert.Equal("no speaker reference", ErrorOf(result, StatusCodes.Status409Conflict));
    }

    [Fact]
    public async Task Synthesize_PicksMostCharacterSimilarSpeaker()
    {
        var datasetId = await SeedDatasetAsync();
        var similar = await SeedItemAsync(datasetId, "今天天气很好");
        var different = await SeedItemAsync(datasetId, "完全无关的长文本内容");
        await SeedSpeechModelAsync();
        var audio = new StubAudioClient();
        audio.Clips[similar.AudioUri] = new AudioClip([1], "audio/mpeg");
        audio.Clips[different.AudioUri] = new AudioClip([2], "audio/mpeg");
        var controller = Create(audio, new StubSpeechClient { Bytes = [7, 7] });

        var result = await controller.Synthesize(
            datasetId.ToString(), new TrainingSynthesizeRequest("今天天气真好", null, null));

        Assert.IsType<FileContentResult>(result);
        Assert.Equal(similar.AudioKey, Header(controller, "X-Speaker-Audio-Key"));
    }

    [Fact]
    public async Task Synthesize_AudioUnavailable_Returns502()
    {
        var datasetId = await SeedDatasetAsync();
        var speaker = await SeedItemAsync(datasetId, "你好");
        await SeedSpeechModelAsync();
        var (audio, speech) = Arrange(speaker);
        audio.FailingUris.Add(speaker.AudioUri);

        var result = await Create(audio, speech)
            .Synthesize(datasetId.ToString(), new TrainingSynthesizeRequest("文本", null, null));

        Assert.Equal("audio unavailable", ErrorOf(result, StatusCodes.Status502BadGateway));
    }

    [Fact]
    public async Task Synthesize_SpeechModelUnavailable_Returns502()
    {
        var datasetId = await SeedDatasetAsync();
        var speaker = await SeedItemAsync(datasetId, "你好");
        await SeedSpeechModelAsync();
        var (audio, speech) = Arrange(speaker);
        speech.Throw = new SpeechModelUnavailableException("down");

        var result = await Create(audio, speech)
            .Synthesize(datasetId.ToString(), new TrainingSynthesizeRequest("文本", null, null));

        Assert.Equal("model service unavailable", ErrorOf(result, StatusCodes.Status502BadGateway));
    }

    [Fact]
    public async Task Synthesize_InvalidModelOutput_Returns502()
    {
        var datasetId = await SeedDatasetAsync();
        var speaker = await SeedItemAsync(datasetId, "你好");
        await SeedSpeechModelAsync();
        var (audio, speech) = Arrange(speaker);
        speech.Throw = new ModelOutputException("no audio");

        var result = await Create(audio, speech)
            .Synthesize(datasetId.ToString(), new TrainingSynthesizeRequest("文本", null, null));

        Assert.Equal("invalid model output", ErrorOf(result, StatusCodes.Status502BadGateway));
    }

    [Fact]
    public async Task Synthesize_DatasetMode_SkipsEmotionModelResolution_AndUsesMatchedItemEmotion()
    {
        var datasetId = await SeedDatasetAsync();
        var item = await SeedItemAsync(
            datasetId, "你好", emotion: new EmotionResult(5, "开心", 0.9, 0, 0, 0, 0, 0, 0, 0));
        await SeedSpeechModelAsync();
        var audio = new StubAudioClient();
        audio.Clips[item.AudioUri] = new AudioClip([1, 2, 3], "audio/mpeg");
        var speech = new StubSpeechClient { Bytes = [7, 7] };
        var controller = Create(audio, speech);

        // dataset 模式 + 非法 modelId：跳过情绪模型解析，不应返回 404 model not found
        var result = await controller.Synthesize(
            datasetId.ToString(),
            new TrainingSynthesizeRequest("文本", null, ObjectId.GenerateNewId().ToString()),
            "dataset");

        var file = Assert.IsType<FileContentResult>(result);
        Assert.Equal("audio/wav", file.ContentType);
        Assert.Equal([7, 7], file.FileContents);
        Assert.Equal(item.AudioKey, Header(controller, "X-Speaker-Audio-Key"));
        Assert.Equal(item.AudioKey, Header(controller, "X-Emo-Audio-Key"));
        Assert.Contains("开心", Assert.Single(speech.Requests).EmotionInstruction);
    }

    [Fact]
    public async Task Synthesize_InvalidEmotionMode_Returns400()
    {
        var datasetId = await SeedDatasetAsync();

        var result = await Create(new StubAudioClient(), new StubSpeechClient())
            .Synthesize(datasetId.ToString(), new TrainingSynthesizeRequest("文本", null, null), "bogus");

        Assert.Equal("invalid emotion mode", ErrorOf(result, StatusCodes.Status400BadRequest));
    }

    [Fact]
    public async Task Synthesize_DatasetMode_NoEmotion_DegradesToNeutral()
    {
        var datasetId = await SeedDatasetAsync();
        var item = await SeedItemAsync(datasetId, "你好");
        await SeedSpeechModelAsync();
        var audio = new StubAudioClient();
        audio.Clips[item.AudioUri] = new AudioClip([1, 2, 3], "audio/mpeg");
        var speech = new StubSpeechClient { Bytes = [7, 7] };
        var controller = Create(audio, speech);

        var result = await controller.Synthesize(
            datasetId.ToString(), new TrainingSynthesizeRequest("文本", null, null), "dataset");

        Assert.IsType<FileContentResult>(result);
        Assert.Null(Header(controller, "X-Emo-Audio-Key"));
        Assert.Null(Assert.Single(speech.Requests).EmotionInstruction);
    }

    [Fact]
    public async Task Synthesize_DefaultEmotionMode_IsNone_SkipsEmotionModelResolution_NoEmotion()
    {
        var datasetId = await SeedDatasetAsync();
        var item = await SeedItemAsync(
            datasetId, "你好", emotion: new EmotionResult(5, "开心", 0.9, 0, 0, 0, 0, 0, 0, 0));
        await SeedSpeechModelAsync();
        var audio = new StubAudioClient();
        audio.Clips[item.AudioUri] = new AudioClip([1, 2, 3], "audio/mpeg");
        var speech = new StubSpeechClient { Bytes = [7, 7] };
        var controller = Create(audio, speech);

        // 不传 emotionMode（默认 none）+ 非法 modelId：跳过情绪模型解析（不 404），且不传情绪参考。
        var result = await controller.Synthesize(
            datasetId.ToString(), new TrainingSynthesizeRequest("文本", null, ObjectId.GenerateNewId().ToString()));

        var file = Assert.IsType<FileContentResult>(result);
        Assert.Equal([7, 7], file.FileContents);
        Assert.Null(Header(controller, "X-Emo-Audio-Key"));
        Assert.Null(Assert.Single(speech.Requests).EmotionInstruction);
    }

    [Fact]
    public async Task Synthesize_NoneMode_SkipsEmotionModelResolution_NoEmotion()
    {
        var datasetId = await SeedDatasetAsync();
        var item = await SeedItemAsync(
            datasetId, "你好", emotion: new EmotionResult(5, "开心", 0.9, 0, 0, 0, 0, 0, 0, 0));
        await SeedSpeechModelAsync();
        var audio = new StubAudioClient();
        audio.Clips[item.AudioUri] = new AudioClip([1, 2, 3], "audio/mpeg");
        var speech = new StubSpeechClient { Bytes = [7, 7] };
        var controller = Create(audio, speech);

        var result = await controller.Synthesize(
            datasetId.ToString(),
            new TrainingSynthesizeRequest("文本", null, ObjectId.GenerateNewId().ToString()),
            "none");

        var file = Assert.IsType<FileContentResult>(result);
        Assert.Equal([7, 7], file.FileContents);
        Assert.Null(Header(controller, "X-Emo-Audio-Key"));
        Assert.Null(Assert.Single(speech.Requests).EmotionInstruction);
    }

    [Fact]
    public async Task Synthesize_LlmMode_UsesModelEmotion()
    {
        var datasetId = await SeedDatasetAsync();
        var speaker = await SeedItemAsync(datasetId, "你好");
        var emoItem = await SeedItemAsync(
            datasetId, "生气", emotion: new EmotionResult(8, "生气", 0, 0.9, 0, 0, 0, 0, 0, 0));
        var model = await SeedModelAsync();
        await SeedSpeechModelAsync();

        var audio = new StubAudioClient();
        audio.Clips[speaker.AudioUri] = new AudioClip([1, 2, 3], "audio/mpeg");
        audio.Clips[emoItem.AudioUri] = new AudioClip([4, 5, 6], "audio/mpeg");
        var controller = Create(audio, new StubSpeechClient { Bytes = [7, 7] });

        var result = await controller.Synthesize(
            datasetId.ToString(), new TrainingSynthesizeRequest("文本", null, model.Id.ToString()), "llm");

        Assert.IsType<FileContentResult>(result);
        Assert.Equal(speaker.AudioKey, Header(controller, "X-Speaker-Audio-Key"));
        Assert.Equal(emoItem.AudioKey, Header(controller, "X-Emo-Audio-Key"));
    }

    [Fact]
    public async Task Synthesize_NoSpeechModel_TakesPrecedenceOverNoSpeakerReference()
    {
        var datasetId = await SeedDatasetAsync();
        // 既无语音模型又无参考条目：按校验顺序先返回 409 no speech model available。

        var result = await Create(new StubAudioClient(), new StubSpeechClient())
            .Synthesize(datasetId.ToString(), new TrainingSynthesizeRequest("文本", null, null));

        Assert.Equal("no speech model available", ErrorOf(result, StatusCodes.Status409Conflict));
    }

    // ===== 拼音短句匹配：segments/rebuild 后 synthesize 多命中拼接（假 recording/假语音模型 + 真 Mongo）=====

    [Fact]
    public async Task Synthesize_PinyinSegmentsRebuild_EndToEnd_MergesMultipleHits()
    {
        var datasetId = await SeedDatasetAsync();
        var first = await SeedItemAsync(datasetId, "你好", audioUri: "b/live/100/recordings/a.flv");
        var second = await SeedItemAsync(datasetId, "世界", audioUri: "b/live/100/recordings/b.flv");

        var segmentSync = new SegmentSyncService(
            Items, SegmentRepository, NullLogger<SegmentSyncService>.Instance);
        await segmentSync.RebuildAsync(datasetId);
        await SeedSpeechModelAsync();

        var audio = new StubAudioClient();
        var speech = new StubSpeechClient { Bytes = [7, 7] };
        var controller = Create(audio, speech);

        var result = await controller.Synthesize(
            datasetId.ToString(), new TrainingSynthesizeRequest("你好，世界。", null, null));

        var file = Assert.IsType<FileContentResult>(result);
        Assert.Equal([7, 7], file.FileContents);
        Assert.Equal($"{first.AudioKey},{second.AudioKey}", Header(controller, "X-Speaker-Audio-Key"));
        Assert.Single(audio.MergeRequests);
        Assert.Equal("你好，世界", Assert.Single(speech.Requests).ReferenceTranscript);
    }
}
