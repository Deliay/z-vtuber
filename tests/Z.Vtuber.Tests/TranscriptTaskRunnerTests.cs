using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using Z.Vtuber.Common.Persistence;
using Z.Vtuber.Common.Persistence.Entities;
using Z.Vtuber.RecordingTranscript.Consumer.Asr;
using Z.Vtuber.RecordingTranscript.Consumer.Audio;
using Z.Vtuber.RecordingTranscript.Consumer.Configuration;
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
            Enabled = true,
            IsDefault = true,
            MaxConcurrency = maxConcurrency,
            CreatedAt = DateTime.UtcNow,
        });

    private async Task<ObjectId> InsertRecordingAsync(long roomId, DateTime? startedAt = null, DateTime? endedAt = null) =>
        await Recordings.InsertAsync(new RecordingRecord
        {
            RoomId = roomId,
            Mid = roomId + 1,
            Title = "title",
            Files = ["[RAW]a.flv"],
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
        long roomId = 100) =>
        await Tasks.InsertAsync(new TranscriptTaskRecord
        {
            RecordingId = recordingId,
            RoomId = roomId,
            File = "[RAW]a.flv",
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
        ITranscriptSegmentPublisher publisher) =>
        new(Tasks, Models, Recordings, recordingClient, audioFactory, asrFactory, persister, publisher,
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
    public async Task TransactionFailure_LeavesNoHalfState()
    {
        var modelId = await InsertModelAsync();
        var recordingId = await InsertRecordingAsync(101);
        var taskId = await InsertTaskAsync(recordingId, modelId, mode: TranscriptTaskModes.File, position: 0);

        var persister = CreatePersister();
        persister.AfterSegmentInsertHook = _ => throw new InvalidOperationException("boom");

        var asrFactory = new FakeStreamingAsrServiceFactory
        {
            Provider = _ => new FakeStreamingAsrService([new StreamingAsrSegment(0, 500, "a")]),
        };
        var runner = CreateRunner(
            new FakeRecordingServiceClient { LiveAvailable = false },
            new FakeAudioSourceFactory(), asrFactory, persister, new FakeTranscriptSegmentPublisher());

        await runner.RunAsync(await Tasks.GetByIdAsync(taskId));

        var task = await Tasks.GetByIdAsync(taskId);
        Assert.Equal(TranscriptTaskStatus.Failed, task!.Status);
        Assert.Equal(0, task.Position);
        Assert.Empty(await Segments.ListByTaskAsync(taskId));
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
        var taskId = await InsertTaskAsync(
            recordingId, modelId, mode: TranscriptTaskModes.Live, position: 8000);

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
        var recordingId = await InsertRecordingAsync(104);
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
    public async Task Scanner_RespectsModelMaxConcurrency()
    {
        var modelId = await InsertModelAsync(maxConcurrency: 1);
        var recordingId = await InsertRecordingAsync(106);
        var first = await InsertTaskAsync(recordingId, modelId, roomId: 106);
        var secondRecording = await InsertRecordingAsync(106);
        var second = await InsertTaskAsync(secondRecording, modelId, roomId: 106);

        var fakeRunner = new FakeTranscriptTaskRunner { WaitForGate = true };
        var lockProvider = new FakeTranscriptTaskLockProvider();
        var scanner = new TranscriptTaskScanner(
            Tasks, Models, lockProvider, fakeRunner,
            new TranscriptConsumerOptions { ScanBatchSize = 10 },
            NullLogger<TranscriptTaskScanner>.Instance);

        await scanner.ScanOnceAsync();

        Assert.Equal(TranscriptTaskStatus.Running, (await Tasks.GetByIdAsync(first))!.Status);
        Assert.Equal(TranscriptTaskStatus.Pending, (await Tasks.GetByIdAsync(second))!.Status);

        try
        {
            fakeRunner.Gates[0].SetResult();
            await Task.Delay(200);
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
