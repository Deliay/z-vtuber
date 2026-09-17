using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using Z.Vtuber.Common.Persistence;
using Z.Vtuber.Common.Persistence.Entities;
using Z.Vtuber.RecordingTranscript.Consumer.Asr;
using Z.Vtuber.RecordingTranscript.Consumer.Audio;
using Z.Vtuber.RecordingTranscript.Consumer.Configuration;
using Z.Vtuber.RecordingTranscript.Consumer.Context;
using Z.Vtuber.RecordingTranscript.Consumer.Enqueue;
using Z.Vtuber.RecordingTranscript.Consumer.Services;
using Z.Vtuber.RecordingTranscript.Consumer.Tasks;

namespace Z.Vtuber.Tests;

public class TranscriptTaskRunnerTests(MongoDbFixture mongo) : IClassFixture<MongoDbFixture>, IAsyncLifetime
{
    public Task InitializeAsync() => mongo.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private TranscriptTaskRepository Tasks => new(mongo.Database);

    private TranscriptSegmentRepository Segments => new(mongo.Database);

    private TranscriptModelRepository Models => new(mongo.Database);

    private RecordingRepository Recordings => new(mongo.Database);

    private async Task<ObjectId> InsertModelAsync(int maxConcurrency = 4) =>
        await Models.InsertAsync(new TranscriptModelRecord
        {
            Name = "model",
            Provider = "ali-fun-asr",
            Type = "streaming",
            ApiKey = "key",
            Model = "fun-asr-realtime",
            Enabled = true,
            MaxConcurrency = maxConcurrency,
            CreatedAt = DateTime.UtcNow,
        });

    private async Task<ObjectId> InsertRecordingAsync(
        long roomId,
        DateTime? startedAt = null,
        DateTime? endedAt = null,
        IReadOnlyList<string>? files = null) =>
        await Recordings.InsertAsync(new RecordingRecord
        {
            RoomId = roomId,
            Mid = roomId + 1,
            Title = "title",
            Files = [.. files ?? ["[RAW]a.flv"]],
            StartedAt = startedAt ?? DateTime.UtcNow.AddMinutes(-1),
            EndedAt = endedAt,
            CreatedAt = DateTime.UtcNow.AddMinutes(-1),
        });

    private async Task<ObjectId> InsertTaskAsync(
        ObjectId recordingId,
        ObjectId modelId,
        string mode = TranscriptTaskModes.File,
        long position = 0,
        string status = TranscriptTaskStatus.Pending,
        long roomId = 100,
        string file = "[RAW]a.flv") =>
        await Tasks.InsertAsync(new TranscriptTaskRecord
        {
            RecordingId = recordingId,
            RoomId = roomId,
            File = file,
            ModelId = modelId,
            Type = TranscriptTaskTypes.Recording,
            Mode = mode,
            Source = TranscriptTaskSources.Auto,
            Position = position,
            Status = status,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });

    private TranscriptTaskRunner CreateRunner(
        IRecordingServiceClient recordingClient,
        IAudioSourceFactory audioFactory,
        IStreamingAsrServiceFactory asrFactory,
        ITranscriptSegmentPersister persister,
        ITranscriptSegmentPublisher publisher,
        ITranscriptContextBuilder? contextBuilder = null) =>
        new(Tasks, Models, Recordings, recordingClient, audioFactory, asrFactory,
            contextBuilder ?? new FakeTranscriptContextBuilder(), persister, publisher,
            NullLogger<TranscriptTaskRunner>.Instance);

    private MongoTranscriptSegmentPersister CreatePersister() => new(mongo.Database);

    [Fact]
    public async Task FileMode_ResumesFromPosition_PersistsAndCompletes()
    {
        var modelId = await InsertModelAsync();
        var recordingId = await InsertRecordingAsync(100);
        var taskId = await InsertTaskAsync(recordingId, modelId, mode: TranscriptTaskModes.File, position: 2000);
        await Segments.InsertAsync(new TranscriptSegmentRecord
        {
            TaskId = taskId, StartTime = 1000, EndTime = 2000, Text = "pre-existing",
        });

        var asrFactory = new FakeStreamingAsrServiceFactory
        {
            Provider = _ => new FakeStreamingAsrService(
            [
                new StreamingAsrSegment(2000, 2500, "a"),
                new StreamingAsrSegment(2500, 3000, "b"),
            ]),
        };
        var audioFactory = new FakeAudioSourceFactory();
        var publisher = new FakeTranscriptSegmentPublisher();
        var recordingClient = new FakeRecordingServiceClient { LiveAvailable = false };
        var runner = CreateRunner(recordingClient, audioFactory, asrFactory, CreatePersister(), publisher);

        await runner.RunAsync(await Tasks.GetByIdAsync(taskId));

        var task = await Tasks.GetByIdAsync(taskId);
        Assert.Equal(TranscriptTaskStatus.Done, task!.Status);
        Assert.Equal(3000, task.Position);
        Assert.Equal(2000, asrFactory.Offsets[0]);
        var fileCall = Assert.Single(audioFactory.FileCalls);
        Assert.Equal(2000, fileCall.Position);
        Assert.Contains("[RAW]a.flv", fileCall.Url);

        var segments = await Segments.ListByTaskAsync(taskId);
        Assert.Equal(new[] { 1000L, 2000L, 2500L }, segments.Select(s => s.StartTime).ToArray());
        Assert.Equal(2, publisher.Published.Count);
    }

    [Fact]
    public async Task FileMode_BuildsContextAndPassesModelToAsr()
    {
        var modelId = await InsertModelAsync();
        var recordingId = await InsertRecordingAsync(130);
        var taskId = await InsertTaskAsync(recordingId, modelId, mode: TranscriptTaskModes.File);

        var contextBuilder = new FakeTranscriptContextBuilder { Result = ["主播名", "舰长A"] };
        var asrFactory = new FakeStreamingAsrServiceFactory
        {
            Provider = _ => new FakeStreamingAsrService([new StreamingAsrSegment(0, 500, "a")]),
        };
        var runner = CreateRunner(
            new FakeRecordingServiceClient { LiveAvailable = false },
            new FakeAudioSourceFactory(), asrFactory, CreatePersister(), new FakeTranscriptSegmentPublisher(),
            contextBuilder);

        await runner.RunAsync(await Tasks.GetByIdAsync(taskId));

        Assert.Single(contextBuilder.Built);
        Assert.Equal(new[] { "主播名", "舰长A" }, asrFactory.Contexts[0]);
        Assert.Equal("fun-asr-realtime", asrFactory.Models[0].Model);
    }

    [Fact]
    public async Task SegmentWrittenButPositionNotAdvanced_RecoversByIdempotency()
    {
        var modelId = await InsertModelAsync();
        var recordingId = await InsertRecordingAsync(101);
        var taskId = await InsertTaskAsync(recordingId, modelId, mode: TranscriptTaskModes.File, position: 0);

        // 模拟「segment 已写入但 position 未推进」的崩溃缝隙（顺序写、非事务）：
        // 重跑时同句撞 (TaskId, StartTime) 唯一索引应幂等忽略，position 仍推进、不重复发布。
        await Segments.InsertAsync(new TranscriptSegmentRecord
        {
            TaskId = taskId, StartTime = 0, EndTime = 500, Text = "a",
        });

        var publisher = new FakeTranscriptSegmentPublisher();
        var asrFactory = new FakeStreamingAsrServiceFactory
        {
            Provider = _ => new FakeStreamingAsrService([new StreamingAsrSegment(0, 500, "a")]),
        };
        var runner = CreateRunner(
            new FakeRecordingServiceClient { LiveAvailable = false },
            new FakeAudioSourceFactory(), asrFactory, CreatePersister(), publisher);

        await runner.RunAsync(await Tasks.GetByIdAsync(taskId));

        var task = await Tasks.GetByIdAsync(taskId);
        Assert.Equal(TranscriptTaskStatus.Done, task!.Status);
        Assert.Equal(500, task.Position);
        Assert.Single(await Segments.ListByTaskAsync(taskId));
        Assert.Empty(publisher.Published);
    }

    [Fact]
    public async Task AsrException_MarksFailedWithReason()
    {
        var modelId = await InsertModelAsync();
        var recordingId = await InsertRecordingAsync(102);
        var taskId = await InsertTaskAsync(recordingId, modelId, mode: TranscriptTaskModes.File);

        var asrFactory = new FakeStreamingAsrServiceFactory
        {
            Provider = _ => new FakeStreamingAsrService([], new InvalidOperationException("asr boom")),
        };
        var runner = CreateRunner(
            new FakeRecordingServiceClient { LiveAvailable = false },
            new FakeAudioSourceFactory(), asrFactory, CreatePersister(), new FakeTranscriptSegmentPublisher());

        await runner.RunAsync(await Tasks.GetByIdAsync(taskId));

        var task = await Tasks.GetByIdAsync(taskId);
        Assert.Equal(TranscriptTaskStatus.Failed, task!.Status);
        Assert.Contains("asr boom", task.Error);
    }

    [Fact]
    public async Task LiveMode_StreamAvailable_ContinuesFromElapsedOffset()
    {
        var modelId = await InsertModelAsync();
        var startedAt = DateTime.UtcNow.AddSeconds(-10);
        var recordingId = await InsertRecordingAsync(103, startedAt: startedAt);
        // 只有 Position==0 的 live 任务才允许接实时流
        var taskId = await InsertTaskAsync(
            recordingId, modelId, mode: TranscriptTaskModes.Live, position: 0);

        var recordingClient = new FakeRecordingServiceClient { LiveAvailable = true };
        var asrFactory = new FakeStreamingAsrServiceFactory
        {
            Provider = _ => new FakeStreamingAsrService([new StreamingAsrSegment(0, 500, "live")]),
        };
        var audioFactory = new FakeAudioSourceFactory();
        var runner = CreateRunner(
            recordingClient, audioFactory, asrFactory, CreatePersister(), new FakeTranscriptSegmentPublisher());

        await runner.RunAsync(await Tasks.GetByIdAsync(taskId));

        Assert.True(asrFactory.Offsets[0] >= 9000, $"offset was {asrFactory.Offsets[0]}");
        Assert.Single(audioFactory.LiveCalls);
        Assert.Equal(TranscriptTaskStatus.Done, (await Tasks.GetByIdAsync(taskId))!.Status);
    }

    [Fact]
    public async Task LiveMode_StreamUnavailable_SwitchesToFileFromPosition()
    {
        var modelId = await InsertModelAsync();
        // live 已有进度：需等场次结束（文件就绪）后转 file 从 position 补尾
        var recordingId = await InsertRecordingAsync(104, endedAt: DateTime.UtcNow);
        var taskId = await InsertTaskAsync(recordingId, modelId, mode: TranscriptTaskModes.Live, position: 5000);

        var recordingClient = new FakeRecordingServiceClient { LiveAvailable = false };
        var asrFactory = new FakeStreamingAsrServiceFactory
        {
            Provider = _ => new FakeStreamingAsrService([new StreamingAsrSegment(5000, 6000, "tail")]),
        };
        var audioFactory = new FakeAudioSourceFactory();
        var runner = CreateRunner(
            recordingClient, audioFactory, asrFactory, CreatePersister(), new FakeTranscriptSegmentPublisher());

        await runner.RunAsync(await Tasks.GetByIdAsync(taskId));

        var fileCall = Assert.Single(audioFactory.FileCalls);
        Assert.Equal(5000, fileCall.Position);
        Assert.Empty(audioFactory.LiveCalls);
        Assert.Equal(TranscriptTaskStatus.Done, (await Tasks.GetByIdAsync(taskId))!.Status);
    }

    [Fact]
    public async Task LiveTaskWithProgress_DoesNotReattachLive_WaitsForFileReady()
    {
        var modelId = await InsertModelAsync();
        // 仍在直播（EndedAt=null）且已有进度：不得再接实时流（会永久丢失 Position→现在 的音频）
        var recordingId = await InsertRecordingAsync(120, endedAt: null);
        var taskId = await InsertTaskAsync(
            recordingId, modelId, mode: TranscriptTaskModes.Live, position: 5000);
        var audioFactory = new FakeAudioSourceFactory();
        var runner = CreateRunner(
            new FakeRecordingServiceClient { LiveAvailable = true },
            audioFactory, new FakeStreamingAsrServiceFactory(), CreatePersister(), new FakeTranscriptSegmentPublisher());

        await runner.RunAsync(await Tasks.GetByIdAsync(taskId));

        Assert.Empty(audioFactory.LiveCalls);
        Assert.Empty(audioFactory.FileCalls);
        Assert.Equal(TranscriptTaskStatus.Pending, (await Tasks.GetByIdAsync(taskId))!.Status);
    }

    [Fact]
    public async Task LiveTaskWithProgress_AfterRecordingEnded_ResumesFromFilePosition()
    {
        var modelId = await InsertModelAsync();
        var recordingId = await InsertRecordingAsync(121, endedAt: DateTime.UtcNow);
        var taskId = await InsertTaskAsync(
            recordingId, modelId, mode: TranscriptTaskModes.Live, position: 5000);
        var asrFactory = new FakeStreamingAsrServiceFactory
        {
            Provider = _ => new FakeStreamingAsrService([new StreamingAsrSegment(5000, 6000, "tail")]),
        };
        var audioFactory = new FakeAudioSourceFactory();
        var runner = CreateRunner(
            new FakeRecordingServiceClient { LiveAvailable = false },
            audioFactory, asrFactory, CreatePersister(), new FakeTranscriptSegmentPublisher());

        await runner.RunAsync(await Tasks.GetByIdAsync(taskId));

        Assert.Empty(audioFactory.LiveCalls);
        var fileCall = Assert.Single(audioFactory.FileCalls);
        Assert.Equal(5000, fileCall.Position);
        Assert.Equal(TranscriptTaskStatus.Done, (await Tasks.GetByIdAsync(taskId))!.Status);
    }

    [Fact]
    public async Task LiveTaskEmptyFile_AfterRecordingEnded_ResolvesRealFileAndCompletes()
    {
        var modelId = await InsertModelAsync();
        // 开播时 Files 为空 → task.File=""；结束后以运行时 recording.Files 重新选取
        var recordingId = await InsertRecordingAsync(122, endedAt: DateTime.UtcNow, files: ["[RAW]real.flv"]);
        var taskId = await InsertTaskAsync(
            recordingId, modelId, mode: TranscriptTaskModes.Live, position: 0, file: "");
        var asrFactory = new FakeStreamingAsrServiceFactory
        {
            Provider = _ => new FakeStreamingAsrService([new StreamingAsrSegment(0, 500, "head")]),
        };
        var audioFactory = new FakeAudioSourceFactory();
        var runner = CreateRunner(
            new FakeRecordingServiceClient { LiveAvailable = false },
            audioFactory, asrFactory, CreatePersister(), new FakeTranscriptSegmentPublisher());

        await runner.RunAsync(await Tasks.GetByIdAsync(taskId));

        var fileCall = Assert.Single(audioFactory.FileCalls);
        Assert.Contains("real.flv", fileCall.Url);
        Assert.Equal(TranscriptTaskStatus.Done, (await Tasks.GetByIdAsync(taskId))!.Status);
        Assert.Equal("[RAW]real.flv", (await Tasks.GetByIdAsync(taskId))!.File);
    }

    [Fact]
    public async Task LiveTaskEmptyFile_RecordingNotReady_DefersWithoutEmptyRequest()
    {
        var modelId = await InsertModelAsync();
        // 文件未就绪（Files 为空且未结束）：不得携空文件名请求、不得 DONE/FAILED，保持可重试
        var recordingId = await InsertRecordingAsync(123, endedAt: null, files: []);
        var taskId = await InsertTaskAsync(
            recordingId, modelId, mode: TranscriptTaskModes.Live, position: 0, file: "");
        var audioFactory = new FakeAudioSourceFactory();
        var runner = CreateRunner(
            new FakeRecordingServiceClient { LiveAvailable = false },
            audioFactory, new FakeStreamingAsrServiceFactory(), CreatePersister(), new FakeTranscriptSegmentPublisher());

        await runner.RunAsync(await Tasks.GetByIdAsync(taskId));

        Assert.Empty(audioFactory.FileCalls);
        Assert.Empty(audioFactory.LiveCalls);
        Assert.Equal(TranscriptTaskStatus.Pending, (await Tasks.GetByIdAsync(taskId))!.Status);
    }

    [Fact]
    public async Task Fencing_LostOwnership_StopsBeforePersist()
    {
        var modelId = await InsertModelAsync();
        var recordingId = await InsertRecordingAsync(105);
        var taskId = await InsertTaskAsync(recordingId, modelId, mode: TranscriptTaskModes.File);
        var task = (await Tasks.GetByIdAsync(taskId))!;

        var handle = new FakeTranscriptTaskLock { TaskId = taskId, OwnershipResult = false };
        var asrFactory = new FakeStreamingAsrServiceFactory
        {
            Provider = _ => new FakeStreamingAsrService([new StreamingAsrSegment(0, 500, "a")]),
        };
        var runner = CreateRunner(
            new FakeRecordingServiceClient { LiveAvailable = false },
            new FakeAudioSourceFactory(), asrFactory, CreatePersister(), new FakeTranscriptSegmentPublisher());

        await runner.RunAsync(task, handle);

        Assert.True(handle.Released);
        Assert.Equal(1, handle.RenewalStarted);
        Assert.Empty(await Segments.ListByTaskAsync(taskId));
        Assert.Equal(TranscriptTaskStatus.Running, (await Tasks.GetByIdAsync(taskId))!.Status);
    }

    [Fact]
    public async Task AudioSourceFailure_MarksFailed_NotSilentDone()
    {
        var modelId = await InsertModelAsync();
        var recordingId = await InsertRecordingAsync(107);
        var taskId = await InsertTaskAsync(recordingId, modelId, mode: TranscriptTaskModes.File);

        // ffmpeg 非 0 退出（如录播文件缺失/不可读）：stdout 立即 EOF、ASR 无片段，但不得静默 DONE
        var audioFactory = new FakeAudioSourceFactory { ExitCode = 1 };
        var asrFactory = new FakeStreamingAsrServiceFactory
        {
            Provider = _ => new FakeStreamingAsrService([]),
        };
        var runner = CreateRunner(
            new FakeRecordingServiceClient { LiveAvailable = false },
            audioFactory, asrFactory, CreatePersister(), new FakeTranscriptSegmentPublisher());

        await runner.RunAsync(await Tasks.GetByIdAsync(taskId));

        var task = await Tasks.GetByIdAsync(taskId);
        Assert.Equal(TranscriptTaskStatus.Failed, task!.Status);
        Assert.Contains("exited with code 1", task.Error);
    }

    [Fact]
    public async Task LiveProbeUnknown_MarksFailed_DoesNotReadPossiblyRecordingFile()
    {
        var modelId = await InsertModelAsync();
        var recordingId = await InsertRecordingAsync(108);
        // 探测路径仅对 Position==0 的 live 任务生效
        var taskId = await InsertTaskAsync(
            recordingId, modelId, mode: TranscriptTaskModes.Live, position: 0);

        var recordingClient = new FakeRecordingServiceClient { ProbeResult = LiveAudioProbe.Unknown };
        var audioFactory = new FakeAudioSourceFactory();
        var asrFactory = new FakeStreamingAsrServiceFactory
        {
            Provider = _ => new FakeStreamingAsrService([]),
        };
        var runner = CreateRunner(
            recordingClient, audioFactory, asrFactory, CreatePersister(), new FakeTranscriptSegmentPublisher());

        await runner.RunAsync(await Tasks.GetByIdAsync(taskId));

        var task = await Tasks.GetByIdAsync(taskId);
        Assert.Equal(TranscriptTaskStatus.Failed, task!.Status);
        // 传输异常不得转 file：仍可能直播中，读文件会提前 EOF 丢后半场
        Assert.Empty(audioFactory.FileCalls);
        Assert.Empty(audioFactory.LiveCalls);
    }

    [Fact]
    public async Task Fencing_LostOwnership_DoesNotWriteTerminalStatus()
    {
        var modelId = await InsertModelAsync();
        var recordingId = await InsertRecordingAsync(109);
        var taskId = await InsertTaskAsync(recordingId, modelId, mode: TranscriptTaskModes.File);
        var task = (await Tasks.GetByIdAsync(taskId))!;

        // 无片段、exit 0：仅在终态写前 fencing；失锁则不得写 DONE（保留 RUNNING 交新持有者）
        var handle = new FakeTranscriptTaskLock { TaskId = taskId, OwnershipResult = false };
        var asrFactory = new FakeStreamingAsrServiceFactory
        {
            Provider = _ => new FakeStreamingAsrService([]),
        };
        var runner = CreateRunner(
            new FakeRecordingServiceClient { LiveAvailable = false },
            new FakeAudioSourceFactory(), asrFactory, CreatePersister(), new FakeTranscriptSegmentPublisher());

        await runner.RunAsync(task, handle);

        Assert.Equal(TranscriptTaskStatus.Running, (await Tasks.GetByIdAsync(taskId))!.Status);
    }

    [Fact]
    public async Task Scanner_AllowsTwoConcurrentTasks_WhenMaxConcurrencyIsTwo()
    {
        var modelId = await InsertModelAsync(maxConcurrency: 2);
        var recording1 = await InsertRecordingAsync(110);
        var first = await InsertTaskAsync(recording1, modelId, roomId: 110);
        var recording2 = await InsertRecordingAsync(111);
        var second = await InsertTaskAsync(recording2, modelId, roomId: 111);
        var recording3 = await InsertRecordingAsync(112);
        var third = await InsertTaskAsync(recording3, modelId, roomId: 112);

        var fakeRunner = new FakeTranscriptTaskRunner { WaitForGate = true };
        var lockProvider = new FakeTranscriptTaskLockProvider();
        var scanner = new TranscriptTaskScanner(
            Tasks, Models, lockProvider, fakeRunner,
            new TranscriptConsumerOptions { ScanBatchSize = 10 },
            NullLogger<TranscriptTaskScanner>.Instance);

        try
        {
            await scanner.ScanOnceAsync();

            // 修复前：置 RUNNING 后未释放内存预留导致同一任务双重计数，max=2 实际只跑 1 个
            Assert.Equal(TranscriptTaskStatus.Running, (await Tasks.GetByIdAsync(first))!.Status);
            Assert.Equal(TranscriptTaskStatus.Running, (await Tasks.GetByIdAsync(second))!.Status);
            Assert.Equal(TranscriptTaskStatus.Pending, (await Tasks.GetByIdAsync(third))!.Status);
        }
        finally
        {
            foreach (var gate in fakeRunner.Gates)
            {
                gate.TrySetResult();
            }
        }
    }

    [Fact]
    public async Task Scanner_RespectsModelMaxConcurrency()
    {
        var modelId = await InsertModelAsync(maxConcurrency: 1);
        var baseStart = DateTime.UtcNow.AddMinutes(-2);
        var recordingId = await InsertRecordingAsync(106, startedAt: baseStart);
        var first = await InsertTaskAsync(recordingId, modelId, roomId: 106);
        // 同房间两场需不同 StartedAt（recordings 唯一索引 RoomId+StartedAt 幂等键）
        var secondRecording = await InsertRecordingAsync(106, startedAt: baseStart.AddMinutes(1));
        var second = await InsertTaskAsync(secondRecording, modelId, roomId: 106);

        var fakeRunner = new FakeTranscriptTaskRunner { WaitForGate = true };
        var lockProvider = new FakeTranscriptTaskLockProvider();
        var scanner = new TranscriptTaskScanner(
            Tasks, Models, lockProvider, fakeRunner,
            new TranscriptConsumerOptions { ScanBatchSize = 10 },
            NullLogger<TranscriptTaskScanner>.Instance);

        try
        {
            await scanner.ScanOnceAsync();

            // max=1：第二个任务在第一个 RUNNING 期间应保持 PENDING（不依赖线程池调度时序）
            Assert.Equal(TranscriptTaskStatus.Running, (await Tasks.GetByIdAsync(first))!.Status);
            Assert.Equal(TranscriptTaskStatus.Pending, (await Tasks.GetByIdAsync(second))!.Status);

            // 释放第一个任务的并发额度后，第二个任务方可被接管
            await Tasks.SetStatusAsync(first, TranscriptTaskStatus.Done);
            await scanner.ScanOnceAsync();
            Assert.Equal(TranscriptTaskStatus.Running, (await Tasks.GetByIdAsync(second))!.Status);
        }
        finally
        {
            foreach (var gate in fakeRunner.Gates)
            {
                gate.TrySetResult();
            }
        }
    }
}
