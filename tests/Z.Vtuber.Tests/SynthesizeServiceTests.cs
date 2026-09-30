using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using Z.Vtuber.Common.Persistence;
using Z.Vtuber.Common.Persistence.Entities;
using Z.Vtuber.Training.Ai;
using Z.Vtuber.Training.Clients;
using Z.Vtuber.Training.Configuration;
using Z.Vtuber.Training.Contracts;
using Z.Vtuber.Training.Synthesis;
using Z.Vtuber.Training.Vectors;

namespace Z.Vtuber.Tests;

/// <summary>
/// 合成编排契约：音色文本向量 top-1 → speaker 取数（次优兜底、全失败 502、无候选 409）、
/// 情感向量 best-effort（降级中性）、emo_* 取自命中条目情绪、embedding/TTS 失败传播。
/// </summary>
public class SynthesizeServiceTests(MongoDbFixture fixture) : IClassFixture<MongoDbFixture>, IAsyncLifetime
{
    public Task InitializeAsync() => fixture.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private TrainingDatasetItemRepository Items => new(fixture.Database);

    private TrainingProviderRepository Providers => new(fixture.Database);

    private const string EmotionJson =
        """{"emotionControlWeight":5,"emotionDescription":"开心","happy":0.9,"angry":0,"sad":0,"afraid":0,"disgusted":0,"melancholic":0,"surprised":0,"calm":0}""";

    private sealed class StubChatClient : IChatModelClient
    {
        public string Raw { get; set; } = EmotionJson;

        public Exception? Throw { get; set; }

        public int Calls { get; private set; }

        public Task<string> CompleteJsonAsync(
            TrainingProviderRecord provider, TrainingModelRecord model, ChatPrompt prompt, CancellationToken ct = default)
        {
            Calls++;
            return Throw is not null ? Task.FromException<string>(Throw) : Task.FromResult(Raw);
        }
    }

    private sealed class StubChatFactory(IChatModelClient client) : IChatModelClientFactory
    {
        public IChatModelClient Create(string providerType) => client;
    }

    private async Task<(TrainingProviderRecord Provider, TrainingModelRecord Model)> SeedProviderAsync()
    {
        var now = DateTime.UtcNow;
        var provider = await Providers.InsertAsync(new TrainingProviderRecord
        {
            Name = $"p-{Guid.NewGuid():N}", Type = "openai", Endpoint = "http://llm", ApiKey = "k",
            CreatedAt = now, UpdatedAt = now,
        });
        return (provider, new TrainingModelRecord
        {
            ProviderId = provider.Id, Model = "gpt", Enabled = true, CreatedAt = now, UpdatedAt = now,
        });
    }

    private async Task<TrainingDatasetItemRecord> SeedItemAsync(
        ObjectId datasetId, string text, string? audioUri = null, EmotionResult? emotion = null)
    {
        var now = DateTime.UtcNow;
        var uri = audioUri ?? $"b/live/{Guid.NewGuid():N}/recordings/{Guid.NewGuid():N}.flv";
        var item = new TrainingDatasetItemRecord
        {
            DatasetId = datasetId,
            AudioType = "transcript",
            Source = "task",
            AudioUri = uri,
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

    private SynthesizeService Create(
        InMemoryVectorStore store, FakeEmbeddingClient embedding, StubAudioClient audio,
        StubTtsClient tts, IChatModelClientFactory factory) =>
        new(
            Items,
            Providers,
            factory,
            audio,
            store,
            embedding,
            tts,
            new SynthesizeOptions(),
            NullLogger<SynthesizeService>.Instance);

    [Fact]
    public async Task Success_ReturnsAudioAndKeys_WithEmotionFromMatchedItem()
    {
        var datasetId = ObjectId.GenerateNewId();
        var speaker = await SeedItemAsync(datasetId, "你好");
        var emoItem = await SeedItemAsync(
            datasetId,
            "生气",
            emotion: new EmotionResult(8, "生气", 0, 0.6, 0, 0, 0, 0, 0, 0));

        var store = new InMemoryVectorStore();
        store.SeedText(datasetId, speaker.Id, speaker.AudioKey, [0.1f, 0.2f, 0.3f]);
        store.SeedEmotion(datasetId, emoItem.Id, emoItem.AudioKey, [0f, 0.5f, 0f, 0f, 0f, 0f, 0f, 0f]);
        var audio = new StubAudioClient();
        audio.Clips[speaker.AudioUri] = new AudioClip([1, 1], "audio/mpeg");
        audio.Clips[emoItem.AudioUri] = new AudioClip([2, 2], "audio/mpeg");
        var tts = new StubTtsClient { Bytes = [9, 9, 9] };
        var (_, model) = await SeedProviderAsync();

        var result = await Create(store, new FakeEmbeddingClient(), audio, tts, new StubChatFactory(new StubChatClient()))
            .SynthesizeAsync(datasetId, "你好", "ZH", model);

        Assert.Equal([9, 9, 9], result.Audio);
        Assert.Equal(speaker.AudioKey, result.SpeakerAudioKey);
        Assert.Equal(emoItem.AudioKey, result.EmoAudioKey);

        var request = Assert.Single(tts.Requests);
        Assert.Equal("你好", request.Text);
        Assert.Equal("ZH", request.Lang);
        Assert.NotNull(request.Emotion);
        Assert.Equal(0.6, request.Emotion!.Emotion.Angry, 3);
    }

    [Fact]
    public async Task NoSpeakerCandidates_ThrowsNoSpeakerReference()
    {
        var datasetId = ObjectId.GenerateNewId();
        var (_, model) = await SeedProviderAsync();

        await Assert.ThrowsAsync<NoSpeakerReferenceException>(() => Create(
                new InMemoryVectorStore(), new FakeEmbeddingClient(), new StubAudioClient(), new StubTtsClient(),
                new StubChatFactory(new StubChatClient()))
            .SynthesizeAsync(datasetId, "你好", "ZH", model));
    }

    [Fact]
    public async Task SpeakerFetch_FallsBackToNextCandidate()
    {
        var datasetId = ObjectId.GenerateNewId();
        var first = await SeedItemAsync(datasetId, "first");
        var second = await SeedItemAsync(datasetId, "second");
        var store = new InMemoryVectorStore();
        store.SeedText(datasetId, first.Id, first.AudioKey, [1f, 0f]);
        store.SeedText(datasetId, second.Id, second.AudioKey, [0.9f, 0f]);
        var audio = new StubAudioClient();
        audio.FailingUris.Add(first.AudioUri);
        audio.Clips[second.AudioUri] = new AudioClip([2], "audio/mpeg");
        var (_, model) = await SeedProviderAsync();

        var result = await Create(store, new FakeEmbeddingClient(), audio, new StubTtsClient(), new StubChatFactory(new StubChatClient()))
            .SynthesizeAsync(datasetId, "你好", "ZH", model);

        Assert.Equal(second.AudioKey, result.SpeakerAudioKey);
        Assert.Equal([first.AudioUri, second.AudioUri], audio.RequestedUris);
    }

    [Fact]
    public async Task SpeakerFetch_AllCandidatesFail_ThrowsAudioUnavailable()
    {
        var datasetId = ObjectId.GenerateNewId();
        var item = await SeedItemAsync(datasetId, "only");
        var store = new InMemoryVectorStore();
        store.SeedText(datasetId, item.Id, item.AudioKey, [1f, 0f]);
        var audio = new StubAudioClient();
        audio.FailingUris.Add(item.AudioUri);
        var (_, model) = await SeedProviderAsync();

        await Assert.ThrowsAsync<AudioUnavailableException>(() => Create(
                store, new FakeEmbeddingClient(), audio, new StubTtsClient(), new StubChatFactory(new StubChatClient()))
            .SynthesizeAsync(datasetId, "你好", "ZH", model));
    }

    [Fact]
    public async Task EmotionModelFailure_DegradesToNeutral()
    {
        var datasetId = ObjectId.GenerateNewId();
        var speaker = await SeedItemAsync(datasetId, "你好");
        var store = new InMemoryVectorStore();
        store.SeedText(datasetId, speaker.Id, speaker.AudioKey, [1f, 0f]);
        var audio = new StubAudioClient();
        audio.Clips[speaker.AudioUri] = new AudioClip([1], "audio/mpeg");
        var tts = new StubTtsClient();
        var (_, model) = await SeedProviderAsync();

        var result = await Create(
                store, new FakeEmbeddingClient(), audio, tts,
                new StubChatFactory(new StubChatClient { Throw = new HttpRequestException("model down") }))
            .SynthesizeAsync(datasetId, "你好", "ZH", model);

        Assert.Null(result.EmoAudioKey);
        Assert.Null(Assert.Single(tts.Requests).Emotion);
    }

    [Fact]
    public async Task NullEmotionModel_DegradesToNeutral_WithoutChatCall()
    {
        var datasetId = ObjectId.GenerateNewId();
        var speaker = await SeedItemAsync(datasetId, "你好");
        var store = new InMemoryVectorStore();
        store.SeedText(datasetId, speaker.Id, speaker.AudioKey, [1f, 0f]);
        var audio = new StubAudioClient();
        audio.Clips[speaker.AudioUri] = new AudioClip([1], "audio/mpeg");
        var tts = new StubTtsClient();
        var chat = new StubChatClient();

        var result = await Create(store, new FakeEmbeddingClient(), audio, tts, new StubChatFactory(chat))
            .SynthesizeAsync(datasetId, "你好", "ZH", null);

        Assert.Null(result.EmoAudioKey);
        Assert.Equal(0, chat.Calls);
        Assert.Null(Assert.Single(tts.Requests).Emotion);
    }

    [Fact]
    public async Task EmbeddingFailure_Propagates()
    {
        var datasetId = ObjectId.GenerateNewId();
        var (_, model) = await SeedProviderAsync();

        await Assert.ThrowsAsync<EmbeddingUnavailableException>(() => Create(
                new InMemoryVectorStore(),
                new FakeEmbeddingClient { Throw = new EmbeddingUnavailableException("down") },
                new StubAudioClient(), new StubTtsClient(), new StubChatFactory(new StubChatClient()))
            .SynthesizeAsync(datasetId, "你好", "ZH", model));
    }

    [Fact]
    public async Task VectorStoreFailure_Propagates()
    {
        var datasetId = ObjectId.GenerateNewId();
        var store = new InMemoryVectorStore { SearchTextThrow = new VectorStoreUnavailableException("down") };
        var (_, model) = await SeedProviderAsync();

        await Assert.ThrowsAsync<VectorStoreUnavailableException>(() => Create(
                store, new FakeEmbeddingClient(), new StubAudioClient(), new StubTtsClient(),
                new StubChatFactory(new StubChatClient()))
            .SynthesizeAsync(datasetId, "你好", "ZH", model));
    }

    [Fact]
    public async Task TtsFailure_Propagates()
    {
        var datasetId = ObjectId.GenerateNewId();
        var speaker = await SeedItemAsync(datasetId, "你好");
        var store = new InMemoryVectorStore();
        store.SeedText(datasetId, speaker.Id, speaker.AudioKey, [1f, 0f]);
        var audio = new StubAudioClient();
        audio.Clips[speaker.AudioUri] = new AudioClip([1], "audio/mpeg");
        var tts = new StubTtsClient { Throw = new TtsUnavailableException("down") };
        var (_, model) = await SeedProviderAsync();

        await Assert.ThrowsAsync<TtsUnavailableException>(() => Create(
                store, new FakeEmbeddingClient(), audio, tts, new StubChatFactory(new StubChatClient()))
            .SynthesizeAsync(datasetId, "你好", "ZH", model));
    }
}
