using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using Z.Vtuber.Common.Persistence;
using Z.Vtuber.Recording.Audio;
using Z.Vtuber.Recording.Configuration;
using Z.Vtuber.Recording.Live;
using Z.Vtuber.Recording.Recording;

namespace Z.Vtuber.Tests;

public class RecordingSessionTests(MongoDbFixture fixture) : IClassFixture<MongoDbFixture>, IAsyncLifetime
{
    public Task InitializeAsync() => fixture.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private RecordingStatusRepository Repository => new(fixture.Database);

    private RecordingRepository Recordings => new(fixture.Database);

    private static RecordingOptions Options(string dir) => new()
    {
        StorageDir = dir,
        Av1 = new RecordingOptions.Av1Options { Enabled = true },
        FencingIntervalMilliseconds = 50,
    };

    private RecordingSession CreateSession(
        string dir,
        FakeRecordingLockProvider locks,
        FakeBiliLiveApi bili,
        FakeRecordingProcessFactory processes,
        AudioStreamHub audio,
        RecordingOptions options,
        IRecordingEventPublisher? events = null,
        ILogger<RecordingSession>? logger = null)
    {
        var publisher = events ?? new FakeRecordingEventPublisher();
        var orphanFinalizer = new OrphanRecordingFinalizer(
            new RecordingRepository(fixture.Database),
            publisher,
            options,
            NullLogger<OrphanRecordingFinalizer>.Instance);
        return new(
            locks,
            bili,
            new FakeStreamBitrateProbe(),
            new FakeRecordingCookieClient(),
            processes,
            audio,
            new RecordingStatusReporter(new RecordingStatusRepository(fixture.Database)),
            new RecordingRepository(fixture.Database),
            publisher,
            orphanFinalizer,
            options,
            logger ?? NullLogger<RecordingSession>.Instance);
    }

    private static string NewStorageDir() =>
        Path.Combine(Path.GetTempPath(), "z-vtuber-rec-tests", Guid.NewGuid().ToString("N"));

    private async Task<string?> WaitForStateAsync(long roomId, string state, TimeSpan? timeout = null)
    {
        var repository = Repository;
        string? current = null;
        try
        {
            await TestWait.UntilAsync(
                async () =>
                {
                    current = (await repository.GetByRoomIdAsync(roomId))?.State;
                    return current == state;
                },
                timeout ?? TimeSpan.FromSeconds(5),
                TimeSpan.FromMilliseconds(25),
                $"state '{state}' should be reached");
        }
        catch (Xunit.Sdk.TrueException)
        {
            // 保持原语义：超时不抛出，返回最后一次观察到的状态，由调用方断言。
        }

        return current;
    }

    [Fact]
    public async Task LiveRoom_StartsRecording_ThenNotLiveWhenProcessExits()
    {
        var roomId = 700001L;
        const long mid = 123456L;
        var dir = NewStorageDir();
        var process = new FakeRecordingProcess();
        var processes = new FakeRecordingProcessFactory { ProcessFactory = _ => process };
        var publisher = new FakeRecordingEventPublisher();
        var session = CreateSession(
            dir,
            new FakeRecordingLockProvider(),
            new FakeBiliLiveApi(),
            processes,
            new AudioStreamHub(1024),
            Options(dir),
            publisher);

        var room = new LiveRoomInfo(roomId, "晚间歌回", true);
        var runTask = session.RunAsync(roomId, mid, "某主播", room, "cookie-id", "cookie", CancellationToken.None);

        Assert.Equal("recording", await WaitForStateAsync(roomId, "recording"));

        var record = await Repository.GetByRoomIdAsync(roomId);
        Assert.NotNull(record);
        Assert.Equal(Environment.MachineName, record!.HostName);
        Assert.Equal("晚间歌回", record.SessionTitle);
        Assert.All(record.Files, f => Assert.EndsWith(".flv", f));
        Assert.Equal(2, record.Files.Count);
        Assert.Contains(record.Files, f => f.StartsWith("[RAW]"));
        Assert.Contains(record.Files, f => f.StartsWith("[AV1]"));

        // 开始：归档一条 recordings + 发布 recording-started
        var started = Assert.Single(publisher.Started);
        Assert.Equal(roomId, started.RoomId);
        Assert.Equal(mid, started.Mid);
        Assert.Equal("晚间歌回", started.Title);
        Assert.Contains($"/api/{roomId}/stream/audio", started.LiveAudioUrl);

        var archived = await Recordings.GetByIdAsync(ObjectId.Parse(started.RecordingId));
        Assert.NotNull(archived);
        Assert.Equal(roomId, archived!.RoomId);
        Assert.Equal(mid, archived.Mid);
        Assert.Equal("晚间歌回", archived.Title);
        Assert.Equal(2, archived.Files.Count);
        Assert.Null(archived.EndedAt);

        var command = Assert.Single(processes.StartedCommands);
        Assert.Contains("-headers", command.Arguments);

        process.Exit(0);
        await runTask;

        Assert.Equal("not_live", (await Repository.GetByRoomIdAsync(roomId))!.State);

        // 正常结束：回写 EndedAt + 发布 recording-finished
        var finished = Assert.Single(publisher.Finished);
        Assert.Equal(started.RecordingId, finished.RecordingId);
        Assert.Equal(roomId, finished.RoomId);
        Assert.Equal(mid, finished.Mid);
        Assert.Equal(2, finished.Files.Length);
        Assert.Equal(2, finished.FileUrls.Length);
        Assert.All(finished.FileUrls, u => Assert.Contains($"/api/{roomId}/recordings/", u));
        Assert.Contains(finished.FileUrls, u => u.EndsWith(Uri.EscapeDataString(finished.Files[0])));

        var ended = await Recordings.GetByIdAsync(ObjectId.Parse(started.RecordingId));
        Assert.NotNull(ended!.EndedAt);
    }

    [Fact]
    public async Task LockAcquireFails_SkipsWithoutStartingProcess()
    {
        var roomId = 700002L;
        var dir = NewStorageDir();
        var processes = new FakeRecordingProcessFactory();
        var publisher = new FakeRecordingEventPublisher();
        var session = CreateSession(
            dir,
            new FakeRecordingLockProvider { AcquireResult = false },
            new FakeBiliLiveApi(),
            processes,
            new AudioStreamHub(1024),
            Options(dir),
            publisher);

        await session.RunAsync(roomId, 1, "主播", new LiveRoomInfo(roomId, "title", true), "cookie-id", "cookie", CancellationToken.None);

        Assert.Empty(processes.StartedCommands);
        Assert.Null(await Repository.GetByRoomIdAsync(roomId));
        Assert.Empty(publisher.Started);
        Assert.Empty(publisher.Finished);
    }

    [Fact]
    public async Task FfmpegNonZeroExit_ReportsError()
    {
        var roomId = 700003L;
        var dir = NewStorageDir();
        var process = new FakeRecordingProcess();
        var processes = new FakeRecordingProcessFactory { ProcessFactory = _ => process };
        var publisher = new FakeRecordingEventPublisher();
        var session = CreateSession(
            dir,
            new FakeRecordingLockProvider(),
            new FakeBiliLiveApi(),
            processes,
            new AudioStreamHub(1024),
            Options(dir),
            publisher);

        var runTask = session.RunAsync(roomId, 1, "主播", new LiveRoomInfo(roomId, "title", true), "cookie-id", "cookie", CancellationToken.None);
        await WaitForStateAsync(roomId, "recording");

        process.Exit(1);
        await runTask;

        var record = await Repository.GetByRoomIdAsync(roomId);
        Assert.Equal("error", record!.State);
        Assert.Contains("1", record.Error);
        // 非正常结束不发布 finished（started 已由开始阶段发布）
        Assert.Empty(publisher.Finished);
    }

    [Fact]
    public async Task NoStream_ReportsError_WithoutStartingProcess()
    {
        var roomId = 700004L;
        var dir = NewStorageDir();
        var processes = new FakeRecordingProcessFactory();
        var bili = new FakeBiliLiveApi { StreamsProvider = _ => [] };
        var publisher = new FakeRecordingEventPublisher();
        var session = CreateSession(
            dir,
            new FakeRecordingLockProvider(),
            bili,
            processes,
            new AudioStreamHub(1024),
            Options(dir),
            publisher);

        await session.RunAsync(roomId, 1, "主播", new LiveRoomInfo(roomId, "title", true), "cookie-id", "cookie", CancellationToken.None);

        Assert.Empty(processes.StartedCommands);
        var record = await Repository.GetByRoomIdAsync(roomId);
        Assert.Equal("error", record!.State);
        Assert.Contains("stream", record.Error);
        Assert.Empty(publisher.Started);
    }

    [Fact]
    public async Task FencingLost_KillsProcessImmediately_AndDoesNotPublishFinished()
    {
        var roomId = 700005L;
        var dir = NewStorageDir();
        var process = new FakeRecordingProcess();
        var processes = new FakeRecordingProcessFactory { ProcessFactory = _ => process };
        var publisher = new FakeRecordingEventPublisher();
        var locks = new FakeRecordingLockProvider
        {
            OwnershipCheck = _ => Task.FromResult(false),
        };
        var session = CreateSession(
            dir,
            locks,
            new FakeBiliLiveApi(),
            processes,
            new AudioStreamHub(1024),
            Options(dir),
            publisher);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await session.RunAsync(roomId, 1, "主播", new LiveRoomInfo(roomId, "title", true), "cookie-id", "cookie", cts.Token);

        Assert.True(process.Killed);
        // 失锁后不覆盖状态，保留此前的 recording（以新持有者上报为准）
        Assert.Equal("recording", (await Repository.GetByRoomIdAsync(roomId))!.State);
        Assert.True(locks.LastHandle!.Released);
        // started 已发布（场次确实开始了），但 finished 不发布
        Assert.Single(publisher.Started);
        Assert.Empty(publisher.Finished);
    }

    [Fact]
    public async Task Shutdown_FinalizesRecording_AndPublishesFinished()
    {
        var roomId = 700007L;
        var dir = NewStorageDir();
        var process = new FakeRecordingProcess();
        var processes = new FakeRecordingProcessFactory { ProcessFactory = _ => process };
        var publisher = new FakeRecordingEventPublisher();
        var session = CreateSession(
            dir,
            new FakeRecordingLockProvider(),
            new FakeBiliLiveApi(),
            processes,
            new AudioStreamHub(1024),
            Options(dir),
            publisher);

        using var cts = new CancellationTokenSource();
        var runTask = session.RunAsync(roomId, 1, "主播", new LiveRoomInfo(roomId, "title", true), "cookie-id", "cookie", cts.Token);
        await WaitForStateAsync(roomId, "recording");

        await cts.CancelAsync();
        await runTask;

        // 停机/取消订阅：旧场次必须回写 EndedAt 并发布 finished（否则孤儿 recordings 永远无法 enqueue）
        Assert.Single(publisher.Started);
        var finished = Assert.Single(publisher.Finished);
        Assert.Equal(roomId, finished.RoomId);
        var archived = Assert.Single(await Recordings.ListAsync(roomId, 10, null));
        Assert.NotNull(archived.EndedAt);
    }

    [Fact]
    public async Task PublishFailure_OnlyLogs_AndDoesNotAbortRecording()
    {
        var roomId = 700008L;
        var dir = NewStorageDir();
        var process = new FakeRecordingProcess();
        var processes = new FakeRecordingProcessFactory { ProcessFactory = _ => process };
        var publisher = new FakeRecordingEventPublisher { ThrowOnPublish = true };
        var logger = new ListLogger<RecordingSession>();
        var session = CreateSession(
            dir,
            new FakeRecordingLockProvider(),
            new FakeBiliLiveApi(),
            processes,
            new AudioStreamHub(1024),
            Options(dir),
            publisher,
            logger);

        var runTask = session.RunAsync(roomId, 42, "主播", new LiveRoomInfo(roomId, "title", true), "cookie-id", "cookie", CancellationToken.None);
        await WaitForStateAsync(roomId, "recording");

        process.Exit(0);
        await runTask;

        // 发布全部失败：录制仍正常归档并结束
        Assert.Equal("not_live", (await Repository.GetByRoomIdAsync(roomId))!.State);
        var archived = await Recordings.ListAsync(roomId, 10, null);
        var record = Assert.Single(archived);
        Assert.Equal(roomId, record.RoomId);
        Assert.Equal(42, record.Mid);
        Assert.NotNull(record.EndedAt);
        Assert.True(logger.HasWarning);
    }

    [Fact]
    public async Task Av1Disabled_ProducesOnlyRawFile()
    {
        var roomId = 700006L;
        var dir = NewStorageDir();
        var process = new FakeRecordingProcess();
        var processes = new FakeRecordingProcessFactory { ProcessFactory = _ => process };
        var options = new RecordingOptions
        {
            StorageDir = dir,
            Av1 = new RecordingOptions.Av1Options { Enabled = false },
            FencingIntervalMilliseconds = 50,
        };
        var session = CreateSession(dir, new FakeRecordingLockProvider(), new FakeBiliLiveApi(), processes, new AudioStreamHub(1024), options);

        var runTask = session.RunAsync(roomId, 1, "主播", new LiveRoomInfo(roomId, "title", true), "cookie-id", "cookie", CancellationToken.None);
        await WaitForStateAsync(roomId, "recording");

        var record = await Repository.GetByRoomIdAsync(roomId);
        Assert.Single(record!.Files);
        Assert.StartsWith("[RAW]", record.Files[0]);

        process.Exit(0);
        await runTask;
    }
}
