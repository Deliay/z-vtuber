using Microsoft.Extensions.Logging.Abstractions;
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
        RecordingOptions options) =>
        new(
            locks,
            bili,
            processes,
            audio,
            new RecordingStatusReporter(new RecordingStatusRepository(fixture.Database)),
            options,
            NullLogger<RecordingSession>.Instance);

    private static string NewStorageDir() =>
        Path.Combine(Path.GetTempPath(), "z-vtuber-rec-tests", Guid.NewGuid().ToString("N"));

    private async Task<string?> WaitForStateAsync(long roomId, string state, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(5));
        var repository = Repository;
        while (DateTime.UtcNow < deadline)
        {
            var record = await repository.GetByRoomIdAsync(roomId);
            if (record?.State == state)
            {
                return record.State;
            }

            await Task.Delay(25);
        }

        return (await repository.GetByRoomIdAsync(roomId))?.State;
    }

    [Fact]
    public async Task LiveRoom_StartsRecording_ThenNotLiveWhenProcessExits()
    {
        var roomId = 700001L;
        var dir = NewStorageDir();
        var process = new FakeRecordingProcess();
        var processes = new FakeRecordingProcessFactory { ProcessFactory = _ => process };
        var session = CreateSession(
            dir,
            new FakeRecordingLockProvider(),
            new FakeBiliLiveApi(),
            processes,
            new AudioStreamHub(1024),
            Options(dir));

        var room = new LiveRoomInfo(roomId, "晚间歌回", true);
        var runTask = session.RunAsync(roomId, "某主播", room, "cookie", CancellationToken.None);

        Assert.Equal("recording", await WaitForStateAsync(roomId, "recording"));

        var record = await Repository.GetByRoomIdAsync(roomId);
        Assert.NotNull(record);
        Assert.Equal(Environment.MachineName, record!.HostName);
        Assert.Equal("晚间歌回", record.SessionTitle);
        Assert.All(record.Files, f => Assert.EndsWith(".flv", f));
        Assert.Equal(2, record.Files.Count);
        Assert.Contains(record.Files, f => f.StartsWith("[RAW]"));
        Assert.Contains(record.Files, f => f.StartsWith("[AV1]"));

        var command = Assert.Single(processes.StartedCommands);
        Assert.Contains("-headers", command.Arguments);

        process.Exit(0);
        await runTask;

        Assert.Equal("not_live", (await Repository.GetByRoomIdAsync(roomId))!.State);
    }

    [Fact]
    public async Task LockAcquireFails_SkipsWithoutStartingProcess()
    {
        var roomId = 700002L;
        var dir = NewStorageDir();
        var processes = new FakeRecordingProcessFactory();
        var session = CreateSession(
            dir,
            new FakeRecordingLockProvider { AcquireResult = false },
            new FakeBiliLiveApi(),
            processes,
            new AudioStreamHub(1024),
            Options(dir));

        await session.RunAsync(roomId, "主播", new LiveRoomInfo(roomId, "title", true), "cookie", CancellationToken.None);

        Assert.Empty(processes.StartedCommands);
        Assert.Null(await Repository.GetByRoomIdAsync(roomId));
    }

    [Fact]
    public async Task FfmpegNonZeroExit_ReportsError()
    {
        var roomId = 700003L;
        var dir = NewStorageDir();
        var process = new FakeRecordingProcess();
        var processes = new FakeRecordingProcessFactory { ProcessFactory = _ => process };
        var session = CreateSession(
            dir,
            new FakeRecordingLockProvider(),
            new FakeBiliLiveApi(),
            processes,
            new AudioStreamHub(1024),
            Options(dir));

        var runTask = session.RunAsync(roomId, "主播", new LiveRoomInfo(roomId, "title", true), "cookie", CancellationToken.None);
        await WaitForStateAsync(roomId, "recording");

        process.Exit(1);
        await runTask;

        var record = await Repository.GetByRoomIdAsync(roomId);
        Assert.Equal("error", record!.State);
        Assert.Contains("1", record.Error);
    }

    [Fact]
    public async Task NoStream_ReportsError_WithoutStartingProcess()
    {
        var roomId = 700004L;
        var dir = NewStorageDir();
        var processes = new FakeRecordingProcessFactory();
        var bili = new FakeBiliLiveApi { StreamsProvider = _ => [] };
        var session = CreateSession(
            dir,
            new FakeRecordingLockProvider(),
            bili,
            processes,
            new AudioStreamHub(1024),
            Options(dir));

        await session.RunAsync(roomId, "主播", new LiveRoomInfo(roomId, "title", true), "cookie", CancellationToken.None);

        Assert.Empty(processes.StartedCommands);
        var record = await Repository.GetByRoomIdAsync(roomId);
        Assert.Equal("error", record!.State);
        Assert.Contains("stream", record.Error);
    }

    [Fact]
    public async Task FencingLost_KillsProcessImmediately()
    {
        var roomId = 700005L;
        var dir = NewStorageDir();
        var process = new FakeRecordingProcess();
        var processes = new FakeRecordingProcessFactory { ProcessFactory = _ => process };
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
            Options(dir));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await session.RunAsync(roomId, "主播", new LiveRoomInfo(roomId, "title", true), "cookie", cts.Token);

        Assert.True(process.Killed);
        Assert.Equal("not_live", (await Repository.GetByRoomIdAsync(roomId))!.State);
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

        var runTask = session.RunAsync(roomId, "主播", new LiveRoomInfo(roomId, "title", true), "cookie", CancellationToken.None);
        await WaitForStateAsync(roomId, "recording");

        var record = await Repository.GetByRoomIdAsync(roomId);
        Assert.Single(record!.Files);
        Assert.StartsWith("[RAW]", record.Files[0]);

        process.Exit(0);
        await runTask;
    }
}
