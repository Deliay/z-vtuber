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
/// 合成编排契约：音色参考按输入文本与条目 Text 的字符相似度（归一化编辑距离）匹配 → speaker 取数
/// （次优兜底、全失败 502、无候选 409）、情感向量 best-effort（降级中性）、emo_* 取自命中条目情绪、TTS 失败传播。
/// </summary>
public class SynthesizeServiceTests(MongoDbFixture fixture) : IClassFixture<MongoDbFixture>, IAsyncLifetime
{
    public Task InitializeAsync() => fixture.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private TrainingDatasetItemRepository Items => new(fixture.Database);

    private TrainingDatasetItemSegmentRepository SegmentRepository => new(fixture.Database);

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

    private SegmentSyncService SegmentSync =>
        new(Items, SegmentRepository, NullLogger<SegmentSyncService>.Instance);

    private SynthesizeService Create(
        InMemoryVectorStore store, StubAudioClient audio,
        StubTtsClient tts, IChatModelClientFactory factory, SynthesizeOptions? options = null,
        AiOptions? aiOptions = null) =>
        new(
            Items,
            SegmentRepository,
            Providers,
            factory,
            audio,
            store,
            tts,
            options ?? new SynthesizeOptions(),
            aiOptions ?? new AiOptions(),
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
        store.SeedEmotion(datasetId, emoItem.Id, emoItem.AudioKey, [0f, 0.5f, 0f, 0f, 0f, 0f, 0f, 0f]);
        var audio = new StubAudioClient();
        audio.Clips[speaker.AudioUri] = new AudioClip([1, 1], "audio/mpeg");
        audio.Clips[emoItem.AudioUri] = new AudioClip([2, 2], "audio/mpeg");
        var tts = new StubTtsClient { Bytes = [9, 9, 9] };
        var (_, model) = await SeedProviderAsync();

        var result = await Create(store, audio, tts, new StubChatFactory(new StubChatClient()))
            .SynthesizeAsync(datasetId, "你好", "ZH", model, EmotionMode.Llm);

        Assert.Equal([9, 9, 9], result.Audio);
        Assert.Equal(speaker.AudioKey, Assert.Single(result.SpeakerAudioKeys));
        Assert.Equal(emoItem.AudioKey, result.EmoAudioKey);

        var request = Assert.Single(tts.Requests);
        Assert.Equal("你好", request.Text);
        Assert.Equal("ZH", request.Lang);
        Assert.NotNull(request.Emotion);
        Assert.Equal(0.6, request.Emotion!.Emotion.Angry, 3);
    }

    [Fact]
    public async Task SpeakerMatching_UsesCharacterSimilarity_ConsideringContentAndLength()
    {
        var datasetId = ObjectId.GenerateNewId();
        var similar = await SeedItemAsync(datasetId, "今天天气很好");
        var different = await SeedItemAsync(datasetId, "完全无关的长文本内容");
        var audio = new StubAudioClient();
        audio.Clips[similar.AudioUri] = new AudioClip([1], "audio/mpeg");
        audio.Clips[different.AudioUri] = new AudioClip([2], "audio/mpeg");
        var (_, model) = await SeedProviderAsync();

        var result = await Create(
                new InMemoryVectorStore(), audio, new StubTtsClient(), new StubChatFactory(new StubChatClient()))
            .SynthesizeAsync(datasetId, "今天天气真好", "ZH", model);

        Assert.Equal(similar.AudioKey, Assert.Single(result.SpeakerAudioKeys));
    }

    [Fact]
    public async Task SpeakerMatching_EmptyTextItems_AreSkipped()
    {
        var datasetId = ObjectId.GenerateNewId();
        var empty = await SeedItemAsync(datasetId, "   ");
        var usable = await SeedItemAsync(datasetId, "可用文本");
        var audio = new StubAudioClient();
        audio.Clips[empty.AudioUri] = new AudioClip([1], "audio/mpeg");
        audio.Clips[usable.AudioUri] = new AudioClip([2], "audio/mpeg");
        var (_, model) = await SeedProviderAsync();

        var result = await Create(
                new InMemoryVectorStore(), audio, new StubTtsClient(), new StubChatFactory(new StubChatClient()))
            .SynthesizeAsync(datasetId, "任何文本", "ZH", model);

        Assert.Equal(usable.AudioKey, Assert.Single(result.SpeakerAudioKeys));
        Assert.Equal([usable.AudioUri], audio.RequestedUris);
    }

    [Fact]
    public async Task NoSpeakerCandidates_ThrowsNoSpeakerReference()
    {
        var datasetId = ObjectId.GenerateNewId();
        var (_, model) = await SeedProviderAsync();

        await Assert.ThrowsAsync<NoSpeakerReferenceException>(() => Create(
                new InMemoryVectorStore(), new StubAudioClient(), new StubTtsClient(),
                new StubChatFactory(new StubChatClient()))
            .SynthesizeAsync(datasetId, "你好", "ZH", model));
    }

    [Fact]
    public async Task SpeakerFetch_FallsBackToNextCandidate()
    {
        var datasetId = ObjectId.GenerateNewId();
        var first = await SeedItemAsync(datasetId, "first");
        var second = await SeedItemAsync(datasetId, "second");
        var audio = new StubAudioClient();
        audio.FailingUris.Add(first.AudioUri);
        audio.Clips[second.AudioUri] = new AudioClip([2], "audio/mpeg");
        var (_, model) = await SeedProviderAsync();

        var result = await Create(
                new InMemoryVectorStore(), audio, new StubTtsClient(), new StubChatFactory(new StubChatClient()))
            .SynthesizeAsync(datasetId, "first", "ZH", model);

        Assert.Equal(second.AudioKey, Assert.Single(result.SpeakerAudioKeys));
        Assert.Equal([first.AudioUri, second.AudioUri], audio.RequestedUris);
    }

    [Fact]
    public async Task SpeakerFetch_AllCandidatesFail_ThrowsAudioUnavailable()
    {
        var datasetId = ObjectId.GenerateNewId();
        var item = await SeedItemAsync(datasetId, "only");
        var audio = new StubAudioClient();
        audio.FailingUris.Add(item.AudioUri);
        var (_, model) = await SeedProviderAsync();

        await Assert.ThrowsAsync<AudioUnavailableException>(() => Create(
                new InMemoryVectorStore(), audio, new StubTtsClient(), new StubChatFactory(new StubChatClient()))
            .SynthesizeAsync(datasetId, "only", "ZH", model));
    }

    [Fact]
    public async Task EmotionModelFailure_DegradesToNeutral()
    {
        var datasetId = ObjectId.GenerateNewId();
        var speaker = await SeedItemAsync(datasetId, "你好");
        var audio = new StubAudioClient();
        audio.Clips[speaker.AudioUri] = new AudioClip([1], "audio/mpeg");
        var tts = new StubTtsClient();
        var (_, model) = await SeedProviderAsync();

        var result = await Create(
                new InMemoryVectorStore(), audio, tts,
                new StubChatFactory(new StubChatClient { Throw = new HttpRequestException("model down") }))
            .SynthesizeAsync(datasetId, "你好", "ZH", model, EmotionMode.Llm);

        Assert.Null(result.EmoAudioKey);
        Assert.Null(Assert.Single(tts.Requests).Emotion);
    }

    [Fact]
    public async Task EmotionVectorStoreFailure_DegradesToNeutral()
    {
        var datasetId = ObjectId.GenerateNewId();
        var speaker = await SeedItemAsync(datasetId, "你好");
        var store = new InMemoryVectorStore { SearchEmotionThrow = new VectorStoreUnavailableException("down") };
        var audio = new StubAudioClient();
        audio.Clips[speaker.AudioUri] = new AudioClip([1], "audio/mpeg");
        var tts = new StubTtsClient();
        var (_, model) = await SeedProviderAsync();

        var result = await Create(store, audio, tts, new StubChatFactory(new StubChatClient()))
            .SynthesizeAsync(datasetId, "你好", "ZH", model, EmotionMode.Llm);

        Assert.Equal(speaker.AudioKey, Assert.Single(result.SpeakerAudioKeys));
        Assert.Null(result.EmoAudioKey);
        Assert.Null(Assert.Single(tts.Requests).Emotion);
    }

    [Fact]
    public async Task NullEmotionModel_DegradesToNeutral_WithoutChatCall()
    {
        var datasetId = ObjectId.GenerateNewId();
        var speaker = await SeedItemAsync(datasetId, "你好");
        var audio = new StubAudioClient();
        audio.Clips[speaker.AudioUri] = new AudioClip([1], "audio/mpeg");
        var tts = new StubTtsClient();
        var chat = new StubChatClient();

        var result = await Create(new InMemoryVectorStore(), audio, tts, new StubChatFactory(chat))
            .SynthesizeAsync(datasetId, "你好", "ZH", null, EmotionMode.Llm);

        Assert.Null(result.EmoAudioKey);
        Assert.Equal(0, chat.Calls);
        Assert.Null(Assert.Single(tts.Requests).Emotion);
    }

    [Fact]
    public async Task DatasetMode_UsesMatchedItemEmotion_WithoutChatCall()
    {
        var datasetId = ObjectId.GenerateNewId();
        var speaker = await SeedItemAsync(
            datasetId, "你好", emotion: new EmotionResult(4, "平静", 0, 0, 0, 0, 0, 0, 0, 0.7));

        var audio = new StubAudioClient();
        audio.Clips[speaker.AudioUri] = new AudioClip([5, 5], "audio/mpeg");
        var tts = new StubTtsClient();
        var chat = new StubChatClient();

        var result = await Create(new InMemoryVectorStore(), audio, tts, new StubChatFactory(chat))
            .SynthesizeAsync(datasetId, "你好", "ZH", null, EmotionMode.Dataset);

        Assert.Equal(speaker.AudioKey, Assert.Single(result.SpeakerAudioKeys));
        Assert.Equal(speaker.AudioKey, result.EmoAudioKey);
        Assert.Equal(0, chat.Calls);

        var request = Assert.Single(tts.Requests);
        Assert.NotNull(request.Emotion);
        Assert.Equal(0.7, request.Emotion!.Emotion.Calm, 3);
    }

    [Fact]
    public async Task DatasetMode_SpeakerWithoutEmotion_FindsNextCandidateWithEmotion()
    {
        var datasetId = ObjectId.GenerateNewId();
        var top = await SeedItemAsync(datasetId, "top");
        var second = await SeedItemAsync(
            datasetId, "second", emotion: new EmotionResult(2, "开心", 0.8, 0, 0, 0, 0, 0, 0, 0));

        var audio = new StubAudioClient();
        audio.Clips[top.AudioUri] = new AudioClip([1], "audio/mpeg");
        audio.Clips[second.AudioUri] = new AudioClip([2], "audio/mpeg");
        var tts = new StubTtsClient();
        var chat = new StubChatClient();

        var result = await Create(new InMemoryVectorStore(), audio, tts, new StubChatFactory(chat))
            .SynthesizeAsync(datasetId, "top", "ZH", null, EmotionMode.Dataset);

        Assert.Equal(top.AudioKey, Assert.Single(result.SpeakerAudioKeys));
        Assert.Equal(second.AudioKey, result.EmoAudioKey);
        Assert.Equal(0, chat.Calls);
    }

    [Fact]
    public async Task DatasetMode_UnavailableAudioSkipped_FindsNextCandidateWithEmotion()
    {
        var datasetId = ObjectId.GenerateNewId();
        var top = await SeedItemAsync(
            datasetId, "top", emotion: new EmotionResult(1, "开心", 0.5, 0, 0, 0, 0, 0, 0, 0));
        var second = await SeedItemAsync(
            datasetId, "second", emotion: new EmotionResult(2, "平静", 0, 0, 0, 0, 0, 0, 0, 0.6));

        var audio = new StubAudioClient();
        audio.FailingUris.Add(top.AudioUri);
        audio.Clips[second.AudioUri] = new AudioClip([2], "audio/mpeg");
        var tts = new StubTtsClient();

        var result = await Create(new InMemoryVectorStore(), audio, tts, new StubChatFactory(new StubChatClient()))
            .SynthesizeAsync(datasetId, "top", "ZH", null, EmotionMode.Dataset);

        Assert.Equal(second.AudioKey, Assert.Single(result.SpeakerAudioKeys));
        Assert.Equal(second.AudioKey, result.EmoAudioKey);
    }

    [Fact]
    public async Task DatasetMode_NoEmotionAnywhere_DegradesToNeutral()
    {
        var datasetId = ObjectId.GenerateNewId();
        var item = await SeedItemAsync(datasetId, "only");
        var audio = new StubAudioClient();
        audio.Clips[item.AudioUri] = new AudioClip([1], "audio/mpeg");
        var tts = new StubTtsClient();
        var chat = new StubChatClient();

        var result = await Create(new InMemoryVectorStore(), audio, tts, new StubChatFactory(chat))
            .SynthesizeAsync(datasetId, "only", "ZH", null, EmotionMode.Dataset);

        Assert.Null(result.EmoAudioKey);
        Assert.Equal(0, chat.Calls);
        Assert.Null(Assert.Single(tts.Requests).Emotion);
    }

    [Fact]
    public async Task DefaultMode_IsNone_NoChatCall_NoEmotion()
    {
        var datasetId = ObjectId.GenerateNewId();
        var speaker = await SeedItemAsync(datasetId, "你好");
        var emoItem = await SeedItemAsync(
            datasetId, "生气", emotion: new EmotionResult(8, "生气", 0, 0.6, 0, 0, 0, 0, 0, 0));

        var store = new InMemoryVectorStore();
        store.SeedEmotion(datasetId, emoItem.Id, emoItem.AudioKey, [0f, 0.5f, 0f, 0f, 0f, 0f, 0f, 0f]);
        var audio = new StubAudioClient();
        audio.Clips[speaker.AudioUri] = new AudioClip([1], "audio/mpeg");
        audio.Clips[emoItem.AudioUri] = new AudioClip([2], "audio/mpeg");
        var tts = new StubTtsClient();
        var chat = new StubChatClient();
        var (_, model) = await SeedProviderAsync();

        // 未传 emotionMode → 默认 None：不调用大模型、不传任何情绪参考。
        var result = await Create(store, audio, tts, new StubChatFactory(chat))
            .SynthesizeAsync(datasetId, "你好", "ZH", model);

        Assert.Equal(speaker.AudioKey, Assert.Single(result.SpeakerAudioKeys));
        Assert.Null(result.EmoAudioKey);
        Assert.Equal(0, chat.Calls);
        Assert.Null(Assert.Single(tts.Requests).Emotion);
    }

    [Fact]
    public async Task LlmMode_UsesChatModel()
    {
        var datasetId = ObjectId.GenerateNewId();
        var speaker = await SeedItemAsync(datasetId, "你好");
        var emoItem = await SeedItemAsync(
            datasetId, "生气", emotion: new EmotionResult(8, "生气", 0, 0.6, 0, 0, 0, 0, 0, 0));

        var store = new InMemoryVectorStore();
        store.SeedEmotion(datasetId, emoItem.Id, emoItem.AudioKey, [0f, 0.5f, 0f, 0f, 0f, 0f, 0f, 0f]);
        var audio = new StubAudioClient();
        audio.Clips[speaker.AudioUri] = new AudioClip([1], "audio/mpeg");
        audio.Clips[emoItem.AudioUri] = new AudioClip([2], "audio/mpeg");
        var tts = new StubTtsClient();
        var chat = new StubChatClient();
        var (_, model) = await SeedProviderAsync();

        var result = await Create(store, audio, tts, new StubChatFactory(chat))
            .SynthesizeAsync(datasetId, "你好", "ZH", model, EmotionMode.Llm);

        Assert.Equal(1, chat.Calls);
        Assert.Equal(emoItem.AudioKey, result.EmoAudioKey);
    }

    [Fact]
    public async Task TtsFailure_Propagates()
    {
        var datasetId = ObjectId.GenerateNewId();
        var speaker = await SeedItemAsync(datasetId, "你好");
        var audio = new StubAudioClient();
        audio.Clips[speaker.AudioUri] = new AudioClip([1], "audio/mpeg");
        var tts = new StubTtsClient { Throw = new TtsUnavailableException("down") };
        var (_, model) = await SeedProviderAsync();

        await Assert.ThrowsAsync<TtsUnavailableException>(() => Create(
                new InMemoryVectorStore(), audio, tts, new StubChatFactory(new StubChatClient()))
            .SynthesizeAsync(datasetId, "你好", "ZH", model));
    }

    // ===== 拼音短句匹配路径（segments 已重建）=====

    [Fact]
    public async Task PinyinMatch_SamePronounce_MatchesByPinyinAndMerges()
    {
        var datasetId = ObjectId.GenerateNewId();
        var item = await SeedItemAsync(datasetId, "今天的天气", audioUri: "b/live/100/recordings/a.flv");
        await SegmentSync.SyncItemAsync(item);
        var audio = new StubAudioClient();
        var tts = new StubTtsClient();
        var (_, model) = await SeedProviderAsync();

        // 「今天得天气」与「今天的天气」拼音相同（得/的共同音节 de）→ 命中并走合并。
        var result = await Create(new InMemoryVectorStore(), audio, tts, new StubChatFactory(new StubChatClient()))
            .SynthesizeAsync(datasetId, "今天得天气", "ZH", model);

        Assert.Equal(item.AudioKey, Assert.Single(result.SpeakerAudioKeys));
        var merge = Assert.Single(audio.MergeRequests);
        Assert.Equal("a.flv", Assert.Single(merge).FileName);
    }

    [Fact]
    public async Task PinyinMatch_MultiSentence_MergesDistinctHitsInInputOrder()
    {
        var datasetId = ObjectId.GenerateNewId();
        var first = await SeedItemAsync(datasetId, "你好", audioUri: "b/live/100/recordings/a.flv");
        var second = await SeedItemAsync(datasetId, "世界", audioUri: "b/live/100/recordings/b.flv");
        await SegmentSync.SyncItemAsync(first);
        await SegmentSync.SyncItemAsync(second);
        var audio = new StubAudioClient();
        var tts = new StubTtsClient();
        var (_, model) = await SeedProviderAsync();

        var result = await Create(new InMemoryVectorStore(), audio, tts, new StubChatFactory(new StubChatClient()))
            .SynthesizeAsync(datasetId, "你好，世界。", "ZH", model);

        Assert.Equal(new[] { first.AudioKey, second.AudioKey }, result.SpeakerAudioKeys);
        var merge = Assert.Single(audio.MergeRequests);
        Assert.Equal(new[] { "a.flv", "b.flv" }, merge.Select(m => m.FileName));
    }

    [Fact]
    public async Task PinyinMatch_DedupsSameItemMatchedByMultipleSentences()
    {
        var datasetId = ObjectId.GenerateNewId();
        var item = await SeedItemAsync(datasetId, "你好，世界", audioUri: "b/live/100/recordings/a.flv");
        await SegmentSync.SyncItemAsync(item);
        var audio = new StubAudioClient();
        var tts = new StubTtsClient();
        var (_, model) = await SeedProviderAsync();

        var result = await Create(new InMemoryVectorStore(), audio, tts, new StubChatFactory(new StubChatClient()))
            .SynthesizeAsync(datasetId, "你好，世界。", "ZH", model);

        Assert.Equal(item.AudioKey, Assert.Single(result.SpeakerAudioKeys));
        var merge = Assert.Single(audio.MergeRequests);
        Assert.Single(merge);
    }

    [Fact]
    public async Task PinyinMatch_TruncatesToMergedReferenceLimit()
    {
        var datasetId = ObjectId.GenerateNewId();
        var first = await SeedItemAsync(datasetId, "你好", audioUri: "b/live/100/recordings/a.flv");
        var second = await SeedItemAsync(datasetId, "世界", audioUri: "b/live/100/recordings/b.flv");
        await SegmentSync.SyncItemAsync(first);
        await SegmentSync.SyncItemAsync(second);
        var audio = new StubAudioClient();
        var tts = new StubTtsClient();
        var (_, model) = await SeedProviderAsync();
        var options = new SynthesizeOptions { MergedReferenceLimit = 1 };

        var result = await Create(new InMemoryVectorStore(), audio, tts, new StubChatFactory(new StubChatClient()), options)
            .SynthesizeAsync(datasetId, "你好，世界。", "ZH", model);

        Assert.Equal(first.AudioKey, Assert.Single(result.SpeakerAudioKeys));
        var merge = Assert.Single(audio.MergeRequests);
        Assert.Single(merge);
    }

    [Fact]
    public async Task PinyinMatch_MergeFails_FallsBackToFirstAvailableSingleClip()
    {
        var datasetId = ObjectId.GenerateNewId();
        var first = await SeedItemAsync(datasetId, "你好", audioUri: "b/live/100/recordings/a.flv");
        var second = await SeedItemAsync(datasetId, "世界", audioUri: "b/live/100/recordings/b.flv");
        await SegmentSync.SyncItemAsync(first);
        await SegmentSync.SyncItemAsync(second);
        var audio = new StubAudioClient
        {
            MergeThrow = new AudioUnavailableException("merge down"),
        };
        audio.FailingUris.Add(first.AudioUri);
        audio.Clips[second.AudioUri] = new AudioClip([4, 4], "audio/mpeg");
        var tts = new StubTtsClient();
        var (_, model) = await SeedProviderAsync();

        var result = await Create(new InMemoryVectorStore(), audio, tts, new StubChatFactory(new StubChatClient()))
            .SynthesizeAsync(datasetId, "你好，世界。", "ZH", model);

        Assert.Equal(second.AudioKey, Assert.Single(result.SpeakerAudioKeys));
        Assert.Single(audio.MergeRequests);
    }

    [Fact]
    public async Task PinyinMatch_MergeFailsAndAllSingleFail_ThrowsAudioUnavailable()
    {
        var datasetId = ObjectId.GenerateNewId();
        var item = await SeedItemAsync(datasetId, "你好", audioUri: "b/live/100/recordings/a.flv");
        await SegmentSync.SyncItemAsync(item);
        var audio = new StubAudioClient { MergeThrow = new AudioUnavailableException("merge down") };
        var tts = new StubTtsClient();
        var (_, model) = await SeedProviderAsync();

        await Assert.ThrowsAsync<AudioUnavailableException>(() => Create(
                new InMemoryVectorStore(), audio, tts, new StubChatFactory(new StubChatClient()))
            .SynthesizeAsync(datasetId, "你好", "ZH", model));
    }

    [Fact]
    public async Task PinyinMatch_DatasetMode_UsesMatchedItemEmotion()
    {
        var datasetId = ObjectId.GenerateNewId();
        var item = await SeedItemAsync(
            datasetId,
            "你好",
            audioUri: "b/live/100/recordings/a.flv",
            emotion: new EmotionResult(3, "平静", 0, 0, 0, 0, 0, 0, 0, 0.7));
        await SegmentSync.SyncItemAsync(item);
        var audio = new StubAudioClient();
        audio.Clips[item.AudioUri] = new AudioClip([4, 4], "audio/mpeg");
        var tts = new StubTtsClient();
        var chat = new StubChatClient();
        var (_, model) = await SeedProviderAsync();

        var result = await Create(new InMemoryVectorStore(), audio, tts, new StubChatFactory(chat))
            .SynthesizeAsync(datasetId, "你好", "ZH", model, EmotionMode.Dataset);

        Assert.Equal(item.AudioKey, Assert.Single(result.SpeakerAudioKeys));
        Assert.Equal(item.AudioKey, result.EmoAudioKey);
        Assert.Equal(0, chat.Calls);
        Assert.Equal(0.7, Assert.Single(tts.Requests).Emotion!.Emotion.Calm, 3);
    }
}
