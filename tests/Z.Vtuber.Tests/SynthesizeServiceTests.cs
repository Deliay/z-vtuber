using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using Z.Vtuber.Common.Persistence;
using Z.Vtuber.Common.Persistence.Entities;
using Z.Vtuber.Training.Ai;
using Z.Vtuber.Training.Clients;
using Z.Vtuber.Training.Configuration;
using Z.Vtuber.Training.Contracts;
using Z.Vtuber.Training.Synthesis;

namespace Z.Vtuber.Tests;

/// <summary>
/// 合成编排契约：音色参考按输入文本与条目 Text 的字符相似度（归一化编辑距离）匹配 → speaker 取数
/// （次优兜底、全失败 502、无候选 409）、参考转写随参考音频传递、
/// 情绪参考三模式（llm 内存 L2 top-1 / dataset 命中条目自身情绪 / none）、降级中性、语音模型失败传播。
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

    private static SpeechSynthesisModel Speech(StubSpeechClient client) =>
        new(
            client,
            new TrainingProviderRecord { Type = "openai", Endpoint = "http://llm", ApiKey = "k" },
            new TrainingModelRecord { Model = "speech" });

    private SynthesizeService Create(
        StubAudioClient audio, StubSpeechClient speech, IChatModelClientFactory factory,
        SynthesizeOptions? options = null, AiOptions? aiOptions = null) =>
        new(
            Items,
            SegmentRepository,
            Providers,
            factory,
            audio,
            options ?? new SynthesizeOptions(),
            aiOptions ?? new AiOptions(),
            NullLogger<SynthesizeService>.Instance);

    [Fact]
    public async Task Success_ReturnsAudioAndKeys_WithEmotionFromLlmMatch()
    {
        var datasetId = ObjectId.GenerateNewId();
        var speaker = await SeedItemAsync(datasetId, "你好");
        var emoItem = await SeedItemAsync(
            datasetId,
            "生气",
            emotion: new EmotionResult(8, "生气", 0, 0.6, 0, 0, 0, 0, 0, 0));

        var audio = new StubAudioClient();
        audio.Clips[speaker.AudioUri] = new AudioClip([1, 1], "audio/mpeg");
        audio.Clips[emoItem.AudioUri] = new AudioClip([2, 2], "audio/mpeg");
        var speech = new StubSpeechClient { Bytes = [9, 9, 9] };
        var (_, model) = await SeedProviderAsync();

        var result = await Create(audio, speech, new StubChatFactory(new StubChatClient()))
            .SynthesizeAsync(datasetId, "你好", "ZH", model, Speech(speech), EmotionMode.Llm);

        Assert.Equal([9, 9, 9], result.Audio);
        Assert.Equal(speaker.AudioKey, Assert.Single(result.SpeakerAudioKeys));
        Assert.Equal(emoItem.AudioKey, result.EmoAudioKey);

        var request = Assert.Single(speech.Requests);
        Assert.Equal("你好", request.Text);
        Assert.Equal("ZH", request.Lang);
        Assert.Equal("你好", request.ReferenceTranscript);
        Assert.Contains("生气", request.EmotionInstruction);
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
        var speech = new StubSpeechClient();

        var result = await Create(audio, speech, new StubChatFactory(new StubChatClient()))
            .SynthesizeAsync(datasetId, "今天天气真好", "ZH", model, Speech(speech));

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
        var speech = new StubSpeechClient();

        var result = await Create(audio, speech, new StubChatFactory(new StubChatClient()))
            .SynthesizeAsync(datasetId, "任何文本", "ZH", model, Speech(speech));

        Assert.Equal(usable.AudioKey, Assert.Single(result.SpeakerAudioKeys));
        Assert.Equal([usable.AudioUri], audio.RequestedUris);
    }

    [Fact]
    public async Task NoSpeakerCandidates_ThrowsNoSpeakerReference()
    {
        var datasetId = ObjectId.GenerateNewId();
        var (_, model) = await SeedProviderAsync();
        var speech = new StubSpeechClient();

        await Assert.ThrowsAsync<NoSpeakerReferenceException>(() => Create(
                new StubAudioClient(), speech, new StubChatFactory(new StubChatClient()))
            .SynthesizeAsync(datasetId, "你好", "ZH", model, Speech(speech)));
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
        var speech = new StubSpeechClient();

        var result = await Create(audio, speech, new StubChatFactory(new StubChatClient()))
            .SynthesizeAsync(datasetId, "first", "ZH", model, Speech(speech));

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
        var speech = new StubSpeechClient();

        await Assert.ThrowsAsync<AudioUnavailableException>(() => Create(
                audio, speech, new StubChatFactory(new StubChatClient()))
            .SynthesizeAsync(datasetId, "only", "ZH", model, Speech(speech)));
    }

    [Fact]
    public async Task EmotionModelFailure_DegradesToNeutral()
    {
        var datasetId = ObjectId.GenerateNewId();
        var speaker = await SeedItemAsync(datasetId, "你好");
        var audio = new StubAudioClient();
        audio.Clips[speaker.AudioUri] = new AudioClip([1], "audio/mpeg");
        var speech = new StubSpeechClient();
        var (_, model) = await SeedProviderAsync();

        var result = await Create(
                audio, speech,
                new StubChatFactory(new StubChatClient { Throw = new HttpRequestException("model down") }))
            .SynthesizeAsync(datasetId, "你好", "ZH", model, Speech(speech), EmotionMode.Llm);

        Assert.Null(result.EmoAudioKey);
        Assert.Null(Assert.Single(speech.Requests).EmotionInstruction);
    }

    [Fact]
    public async Task LlmMode_NoAnnotatedItems_DegradesToNeutral()
    {
        var datasetId = ObjectId.GenerateNewId();
        var speaker = await SeedItemAsync(datasetId, "你好");
        var audio = new StubAudioClient();
        audio.Clips[speaker.AudioUri] = new AudioClip([1], "audio/mpeg");
        var speech = new StubSpeechClient();
        var chat = new StubChatClient();
        var (_, model) = await SeedProviderAsync();

        var result = await Create(audio, speech, new StubChatFactory(chat))
            .SynthesizeAsync(datasetId, "你好", "ZH", model, Speech(speech), EmotionMode.Llm);

        Assert.Equal(1, chat.Calls);
        Assert.Null(result.EmoAudioKey);
        Assert.Null(Assert.Single(speech.Requests).EmotionInstruction);
    }

    [Fact]
    public async Task NullEmotionModel_DegradesToNeutral_WithoutChatCall()
    {
        var datasetId = ObjectId.GenerateNewId();
        var speaker = await SeedItemAsync(datasetId, "你好");
        var audio = new StubAudioClient();
        audio.Clips[speaker.AudioUri] = new AudioClip([1], "audio/mpeg");
        var speech = new StubSpeechClient();
        var chat = new StubChatClient();

        var result = await Create(audio, speech, new StubChatFactory(chat))
            .SynthesizeAsync(datasetId, "你好", "ZH", null, Speech(speech), EmotionMode.Llm);

        Assert.Null(result.EmoAudioKey);
        Assert.Equal(0, chat.Calls);
        Assert.Null(Assert.Single(speech.Requests).EmotionInstruction);
    }

    [Fact]
    public async Task DatasetMode_UsesMatchedItemEmotion_WithoutChatCall()
    {
        var datasetId = ObjectId.GenerateNewId();
        var speaker = await SeedItemAsync(
            datasetId, "你好", emotion: new EmotionResult(4, "平静", 0, 0, 0, 0, 0, 0, 0, 0.7));

        var audio = new StubAudioClient();
        audio.Clips[speaker.AudioUri] = new AudioClip([5, 5], "audio/mpeg");
        var speech = new StubSpeechClient();
        var chat = new StubChatClient();

        var result = await Create(audio, speech, new StubChatFactory(chat))
            .SynthesizeAsync(datasetId, "你好", "ZH", null, Speech(speech), EmotionMode.Dataset);

        Assert.Equal(speaker.AudioKey, Assert.Single(result.SpeakerAudioKeys));
        Assert.Equal(speaker.AudioKey, result.EmoAudioKey);
        Assert.Equal(0, chat.Calls);
        Assert.Contains("平静", Assert.Single(speech.Requests).EmotionInstruction);
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
        var speech = new StubSpeechClient();
        var chat = new StubChatClient();

        var result = await Create(audio, speech, new StubChatFactory(chat))
            .SynthesizeAsync(datasetId, "top", "ZH", null, Speech(speech), EmotionMode.Dataset);

        Assert.Equal(top.AudioKey, Assert.Single(result.SpeakerAudioKeys));
        Assert.Equal(second.AudioKey, result.EmoAudioKey);
        Assert.Equal(0, chat.Calls);
    }

    [Fact]
    public async Task DatasetMode_EmotionFromAnnotatedCandidate_EvenWhenItsAudioUnavailable()
    {
        var datasetId = ObjectId.GenerateNewId();
        var top = await SeedItemAsync(
            datasetId, "top", emotion: new EmotionResult(1, "开心", 0.5, 0, 0, 0, 0, 0, 0, 0));
        var second = await SeedItemAsync(
            datasetId, "second", emotion: new EmotionResult(2, "平静", 0, 0, 0, 0, 0, 0, 0, 0.6));

        var audio = new StubAudioClient();
        audio.FailingUris.Add(top.AudioUri);
        audio.Clips[second.AudioUri] = new AudioClip([2], "audio/mpeg");
        var speech = new StubSpeechClient();

        var result = await Create(audio, speech, new StubChatFactory(new StubChatClient()))
            .SynthesizeAsync(datasetId, "top", "ZH", null, Speech(speech), EmotionMode.Dataset);

        // 音色取首个音频可取者（second）；情绪取候选顺序中首个带标注者（top，不再要求其音频可取）。
        Assert.Equal(second.AudioKey, Assert.Single(result.SpeakerAudioKeys));
        Assert.Equal(top.AudioKey, result.EmoAudioKey);
    }

    [Fact]
    public async Task DatasetMode_NoEmotionAnywhere_DegradesToNeutral()
    {
        var datasetId = ObjectId.GenerateNewId();
        var item = await SeedItemAsync(datasetId, "only");
        var audio = new StubAudioClient();
        audio.Clips[item.AudioUri] = new AudioClip([1], "audio/mpeg");
        var speech = new StubSpeechClient();
        var chat = new StubChatClient();

        var result = await Create(audio, speech, new StubChatFactory(chat))
            .SynthesizeAsync(datasetId, "only", "ZH", null, Speech(speech), EmotionMode.Dataset);

        Assert.Null(result.EmoAudioKey);
        Assert.Equal(0, chat.Calls);
        Assert.Null(Assert.Single(speech.Requests).EmotionInstruction);
    }

    [Fact]
    public async Task DefaultMode_IsNone_NoChatCall_NoEmotion()
    {
        var datasetId = ObjectId.GenerateNewId();
        var speaker = await SeedItemAsync(datasetId, "你好");
        var emoItem = await SeedItemAsync(
            datasetId, "生气", emotion: new EmotionResult(8, "生气", 0, 0.6, 0, 0, 0, 0, 0, 0));

        var audio = new StubAudioClient();
        audio.Clips[speaker.AudioUri] = new AudioClip([1], "audio/mpeg");
        audio.Clips[emoItem.AudioUri] = new AudioClip([2], "audio/mpeg");
        var speech = new StubSpeechClient();
        var chat = new StubChatClient();
        var (_, model) = await SeedProviderAsync();

        // 未传 emotionMode → 默认 None：不调用大模型、不传任何情绪参考。
        var result = await Create(audio, speech, new StubChatFactory(chat))
            .SynthesizeAsync(datasetId, "你好", "ZH", model, Speech(speech));

        Assert.Equal(speaker.AudioKey, Assert.Single(result.SpeakerAudioKeys));
        Assert.Null(result.EmoAudioKey);
        Assert.Equal(0, chat.Calls);
        Assert.Null(Assert.Single(speech.Requests).EmotionInstruction);
    }

    [Fact]
    public async Task LlmMode_UsesChatModel()
    {
        var datasetId = ObjectId.GenerateNewId();
        var speaker = await SeedItemAsync(datasetId, "你好");
        var emoItem = await SeedItemAsync(
            datasetId, "生气", emotion: new EmotionResult(8, "生气", 0, 0.6, 0, 0, 0, 0, 0, 0));

        var audio = new StubAudioClient();
        audio.Clips[speaker.AudioUri] = new AudioClip([1], "audio/mpeg");
        audio.Clips[emoItem.AudioUri] = new AudioClip([2], "audio/mpeg");
        var speech = new StubSpeechClient();
        var chat = new StubChatClient();
        var (_, model) = await SeedProviderAsync();

        var result = await Create(audio, speech, new StubChatFactory(chat))
            .SynthesizeAsync(datasetId, "你好", "ZH", model, Speech(speech), EmotionMode.Llm);

        Assert.Equal(1, chat.Calls);
        Assert.Equal(emoItem.AudioKey, result.EmoAudioKey);
    }

    [Fact]
    public async Task SpeechModelFailure_Propagates()
    {
        var datasetId = ObjectId.GenerateNewId();
        var speaker = await SeedItemAsync(datasetId, "你好");
        var audio = new StubAudioClient();
        audio.Clips[speaker.AudioUri] = new AudioClip([1], "audio/mpeg");
        var speech = new StubSpeechClient { Throw = new SpeechModelUnavailableException("down") };
        var (_, model) = await SeedProviderAsync();

        await Assert.ThrowsAsync<SpeechModelUnavailableException>(() => Create(
                audio, speech, new StubChatFactory(new StubChatClient()))
            .SynthesizeAsync(datasetId, "你好", "ZH", model, Speech(speech)));
    }

    // ===== 拼音短句匹配路径（segments 已重建）=====

    [Fact]
    public async Task PinyinMatch_SamePronounce_MatchesByPinyinAndMerges()
    {
        var datasetId = ObjectId.GenerateNewId();
        var item = await SeedItemAsync(datasetId, "今天的天气", audioUri: "b/live/100/recordings/a.flv");
        await SegmentSync.SyncItemAsync(item);
        var audio = new StubAudioClient();
        var speech = new StubSpeechClient();
        var (_, model) = await SeedProviderAsync();

        // 「今天得天气」与「今天的天气」拼音相同（得/的共同音节 de）→ 命中并走合并。
        var result = await Create(audio, speech, new StubChatFactory(new StubChatClient()))
            .SynthesizeAsync(datasetId, "今天得天气", "ZH", model, Speech(speech));

        Assert.Equal(item.AudioKey, Assert.Single(result.SpeakerAudioKeys));
        var merge = Assert.Single(audio.MergeRequests);
        Assert.Equal("a.flv", Assert.Single(merge).FileName);
        Assert.Equal("今天的天气", Assert.Single(speech.Requests).ReferenceTranscript);
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
        var speech = new StubSpeechClient();
        var (_, model) = await SeedProviderAsync();

        var result = await Create(audio, speech, new StubChatFactory(new StubChatClient()))
            .SynthesizeAsync(datasetId, "你好，世界。", "ZH", model, Speech(speech));

        Assert.Equal(new[] { first.AudioKey, second.AudioKey }, result.SpeakerAudioKeys);
        var merge = Assert.Single(audio.MergeRequests);
        Assert.Equal(new[] { "a.flv", "b.flv" }, merge.Select(m => m.FileName));
        Assert.Equal("你好，世界", Assert.Single(speech.Requests).ReferenceTranscript);
    }

    [Fact]
    public async Task PinyinMatch_DedupsSameItemMatchedByMultipleSentences()
    {
        var datasetId = ObjectId.GenerateNewId();
        var item = await SeedItemAsync(datasetId, "你好，世界", audioUri: "b/live/100/recordings/a.flv");
        await SegmentSync.SyncItemAsync(item);
        var audio = new StubAudioClient();
        var speech = new StubSpeechClient();
        var (_, model) = await SeedProviderAsync();

        var result = await Create(audio, speech, new StubChatFactory(new StubChatClient()))
            .SynthesizeAsync(datasetId, "你好，世界。", "ZH", model, Speech(speech));

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
        var speech = new StubSpeechClient();
        var (_, model) = await SeedProviderAsync();
        var options = new SynthesizeOptions { MergedReferenceLimit = 1 };

        var result = await Create(audio, speech, new StubChatFactory(new StubChatClient()), options)
            .SynthesizeAsync(datasetId, "你好，世界。", "ZH", model, Speech(speech));

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
        var speech = new StubSpeechClient();
        var (_, model) = await SeedProviderAsync();

        var result = await Create(audio, speech, new StubChatFactory(new StubChatClient()))
            .SynthesizeAsync(datasetId, "你好，世界。", "ZH", model, Speech(speech));

        Assert.Equal(second.AudioKey, Assert.Single(result.SpeakerAudioKeys));
        Assert.Single(audio.MergeRequests);
        Assert.Equal("世界", Assert.Single(speech.Requests).ReferenceTranscript);
    }

    [Fact]
    public async Task PinyinMatch_MergeFailsAndAllSingleFail_ThrowsAudioUnavailable()
    {
        var datasetId = ObjectId.GenerateNewId();
        var item = await SeedItemAsync(datasetId, "你好", audioUri: "b/live/100/recordings/a.flv");
        await SegmentSync.SyncItemAsync(item);
        var audio = new StubAudioClient { MergeThrow = new AudioUnavailableException("merge down") };
        var speech = new StubSpeechClient();
        var (_, model) = await SeedProviderAsync();

        await Assert.ThrowsAsync<AudioUnavailableException>(() => Create(
                audio, speech, new StubChatFactory(new StubChatClient()))
            .SynthesizeAsync(datasetId, "你好", "ZH", model, Speech(speech)));
    }

    [Fact]
    public async Task PinyinMatch_DatasetMode_UsesMatchedItemEmotionWithoutClipFetch()
    {
        var datasetId = ObjectId.GenerateNewId();
        var item = await SeedItemAsync(
            datasetId,
            "你好",
            audioUri: "b/live/100/recordings/a.flv",
            emotion: new EmotionResult(3, "平静", 0, 0, 0, 0, 0, 0, 0, 0.7));
        await SegmentSync.SyncItemAsync(item);
        var audio = new StubAudioClient();
        var speech = new StubSpeechClient();
        var chat = new StubChatClient();
        var (_, model) = await SeedProviderAsync();

        var result = await Create(audio, speech, new StubChatFactory(chat))
            .SynthesizeAsync(datasetId, "你好", "ZH", model, Speech(speech), EmotionMode.Dataset);

        Assert.Equal(item.AudioKey, Assert.Single(result.SpeakerAudioKeys));
        Assert.Equal(item.AudioKey, result.EmoAudioKey);
        Assert.Equal(0, chat.Calls);
        Assert.Contains("平静", Assert.Single(speech.Requests).EmotionInstruction);
    }

    [Fact]
    public async Task PinyinMatch_NoneMode_SkipsEmotion_NoChatCall()
    {
        var datasetId = ObjectId.GenerateNewId();
        var item = await SeedItemAsync(
            datasetId,
            "你好",
            audioUri: "b/live/100/recordings/a.flv",
            emotion: new EmotionResult(5, "开心", 0.9, 0, 0, 0, 0, 0, 0, 0));
        await SegmentSync.SyncItemAsync(item);
        var audio = new StubAudioClient();
        var speech = new StubSpeechClient();
        var chat = new StubChatClient();
        var (_, model) = await SeedProviderAsync();

        // 拼音路径 + none：完全跳过情绪参考与模型解析，不传情绪。
        var result = await Create(audio, speech, new StubChatFactory(chat))
            .SynthesizeAsync(datasetId, "你好", "ZH", model, Speech(speech), EmotionMode.None);

        Assert.Equal(item.AudioKey, Assert.Single(result.SpeakerAudioKeys));
        Assert.Null(result.EmoAudioKey);
        Assert.Equal(0, chat.Calls);
        Assert.Null(Assert.Single(speech.Requests).EmotionInstruction);
        Assert.Single(audio.MergeRequests);
    }

    [Fact]
    public async Task PinyinMatch_LlmMode_UsesChatModelForEmotion()
    {
        var datasetId = ObjectId.GenerateNewId();
        var speaker = await SeedItemAsync(datasetId, "你好", audioUri: "b/live/100/recordings/a.flv");
        var emoItem = await SeedItemAsync(
            datasetId, "生气", emotion: new EmotionResult(8, "生气", 0, 0.6, 0, 0, 0, 0, 0, 0));
        await SegmentSync.SyncItemAsync(speaker);

        var audio = new StubAudioClient();
        audio.Clips[emoItem.AudioUri] = new AudioClip([2, 2], "audio/mpeg");
        var speech = new StubSpeechClient();
        var chat = new StubChatClient();
        var (_, model) = await SeedProviderAsync();

        // 拼音路径 + llm：音色走拼接，情绪经大模型生成向量 + 内存 L2 top-1。
        var result = await Create(audio, speech, new StubChatFactory(chat))
            .SynthesizeAsync(datasetId, "你好", "ZH", model, Speech(speech), EmotionMode.Llm);

        Assert.Equal(speaker.AudioKey, Assert.Single(result.SpeakerAudioKeys));
        Assert.Equal(emoItem.AudioKey, result.EmoAudioKey);
        Assert.Equal(1, chat.Calls);
        Assert.Contains("生气", Assert.Single(speech.Requests).EmotionInstruction);
    }

    [Fact]
    public async Task PinyinMatch_EqualScore_TieBreaksByItemIdAscending_Deterministically()
    {
        var datasetId = ObjectId.GenerateNewId();
        // 两条候选拼音均为 ni hao、与输入「你好」同分（1.0）：同分按 itemId 升序稳定取先。
        var a = await SeedItemAsync(datasetId, "你好", audioUri: "b/live/100/recordings/a.flv");
        var b = await SeedItemAsync(datasetId, "你好", audioUri: "b/live/100/recordings/b.flv");
        await SegmentSync.SyncItemAsync(a);
        await SegmentSync.SyncItemAsync(b);
        var expected = a.Id < b.Id ? a : b;

        var audio = new StubAudioClient();
        var speech = new StubSpeechClient();
        var (_, model) = await SeedProviderAsync();
        var service = Create(audio, speech, new StubChatFactory(new StubChatClient()));

        // 同一数据集 + 同一文本重复调用：命中集合与拼接顺序必须一致。
        for (var i = 0; i < 5; i++)
        {
            var result = await service.SynthesizeAsync(datasetId, "你好", "ZH", model, Speech(speech));
            Assert.Equal([expected.AudioKey], result.SpeakerAudioKeys);
        }
    }

    [Fact]
    public async Task PinyinMatch_EqualScoreSameItem_TieBreaksBySegmentIndex_Deterministic()
    {
        var datasetId = ObjectId.GenerateNewId();
        // 单条目含两条同音短句（SegmentIndex 0/1，同分）：同分且同 itemId 时按 SegmentIndex 升序稳定取先。
        var item = await SeedItemAsync(datasetId, "你好，你好", audioUri: "b/live/100/recordings/a.flv");
        await SegmentSync.SyncItemAsync(item);

        var audio = new StubAudioClient();
        var speech = new StubSpeechClient();
        var (_, model) = await SeedProviderAsync();
        var service = Create(audio, speech, new StubChatFactory(new StubChatClient()));

        for (var i = 0; i < 5; i++)
        {
            var result = await service.SynthesizeAsync(datasetId, "你好", "ZH", model, Speech(speech));
            Assert.Equal([item.AudioKey], result.SpeakerAudioKeys);
        }
    }

    [Fact]
    public async Task PinyinMatch_TruncatesByTotalDurationLimit()
    {
        var datasetId = ObjectId.GenerateNewId();
        var first = await SeedItemAsync(datasetId, "你好", audioUri: "b/live/100/recordings/a.flv");
        var second = await SeedItemAsync(datasetId, "世界", audioUri: "b/live/100/recordings/b.flv");
        var third = await SeedItemAsync(datasetId, "再见", audioUri: "b/live/100/recordings/c.flv");
        await SegmentSync.SyncItemAsync(first);
        await SegmentSync.SyncItemAsync(second);
        await SegmentSync.SyncItemAsync(third);

        var audio = new StubAudioClient();
        var speech = new StubSpeechClient();
        var (_, model) = await SeedProviderAsync();
        // 每条 1000ms（SeedItemAsync EndMs=1000）；总时长上限 2500ms → 仅拼接前 2 条。
        var options = new SynthesizeOptions { MergedReferenceMaxTotalDurationMs = 2_500 };

        var result = await Create(audio, speech, new StubChatFactory(new StubChatClient()), options)
            .SynthesizeAsync(datasetId, "你好，世界，再见。", "ZH", model, Speech(speech));

        Assert.Equal([first.AudioKey, second.AudioKey], result.SpeakerAudioKeys);
        var merge = Assert.Single(audio.MergeRequests);
        Assert.Equal(["a.flv", "b.flv"], merge.Select(m => m.FileName));
    }

    [Fact]
    public void TieBreak_PrefersLowerItemId_ThenLowerSegmentIndex()
    {
        var low = new TrainingDatasetItemSegmentRecord
        {
            ItemId = ObjectId.Parse("000000000000000000000001"), SegmentIndex = 5,
        };
        var high = new TrainingDatasetItemSegmentRecord
        {
            ItemId = ObjectId.Parse("000000000000000000000002"), SegmentIndex = 0,
        };
        var sameItemEarlier = new TrainingDatasetItemSegmentRecord
        {
            ItemId = low.ItemId, SegmentIndex = 4,
        };

        Assert.True(SynthesizeService.IsBetterTieBreak(low, high));
        Assert.False(SynthesizeService.IsBetterTieBreak(high, low));
        Assert.True(SynthesizeService.IsBetterTieBreak(sameItemEarlier, low));
        Assert.False(SynthesizeService.IsBetterTieBreak(low, sameItemEarlier));
    }
}
