using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using Z.Vtuber.Common.Persistence;
using Z.Vtuber.Common.Persistence.Entities;
using Z.Vtuber.Recording.Configuration;
using Z.Vtuber.Recording.Recording;

namespace Z.Vtuber.Tests;

public class OrphanRecordingFinalizerTests(MongoDbFixture fixture) : IClassFixture<MongoDbFixture>, IAsyncLifetime
{
    public Task InitializeAsync() => fixture.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private RecordingRepository Recordings => new(fixture.Database);

    private async Task<ObjectId> InsertOpenAsync(
        long roomId, string title = "场次", DateTime? startedAt = null, IReadOnlyList<string>? files = null) =>
        await Recordings.InsertAsync(new RecordingRecord
        {
            RoomId = roomId,
            Mid = roomId + 1,
            Title = title,
            Files = [.. files ?? ["[RAW]a.flv"]],
            StartedAt = startedAt ?? DateTime.UtcNow.AddMinutes(-30),
            CreatedAt = DateTime.UtcNow.AddMinutes(-30),
        });

    private OrphanRecordingFinalizer CreateFinalizer(
        string storageDir, FakeRecordingEventPublisher publisher) =>
        new(Recordings, publisher,
            new RecordingOptions { StorageDir = storageDir, Av1 = new RecordingOptions.Av1Options() },
            NullLogger<OrphanRecordingFinalizer>.Instance);

    private static string NewStorageDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "z-vtuber-orphan-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public async Task FinalizeAllOpenAsync_ClosesOpenRecordings_AndIsIdempotent()
    {
        var publisher = new FakeRecordingEventPublisher();
        var finalizer = CreateFinalizer(NewStorageDir(), publisher);
        var first = await InsertOpenAsync(900001, "场次一");
        var second = await InsertOpenAsync(900002, "场次二");

        await finalizer.FinalizeAllOpenAsync();
        await finalizer.FinalizeAllOpenAsync();

        Assert.NotNull((await Recordings.GetByIdAsync(first))!.EndedAt);
        Assert.NotNull((await Recordings.GetByIdAsync(second))!.EndedAt);
        Assert.Equal(2, publisher.Finished.Count);
    }

    [Fact]
    public async Task FinalizeRoomAsync_OnlyAffectsThatRoom()
    {
        var publisher = new FakeRecordingEventPublisher();
        var finalizer = CreateFinalizer(NewStorageDir(), publisher);
        var target = await InsertOpenAsync(900003);
        var other = await InsertOpenAsync(900004);

        await finalizer.FinalizeRoomAsync(900003);

        Assert.NotNull((await Recordings.GetByIdAsync(target))!.EndedAt);
        Assert.Null((await Recordings.GetByIdAsync(other))!.EndedAt);
        Assert.Single(publisher.Finished);
        Assert.Equal(900003, publisher.Finished[0].RoomId);
    }

    [Fact]
    public async Task FinalizeAsync_ClosesSpecificRecording()
    {
        var publisher = new FakeRecordingEventPublisher();
        var finalizer = CreateFinalizer(NewStorageDir(), publisher);
        var recordingId = await InsertOpenAsync(900005);

        await finalizer.FinalizeAsync(recordingId);

        Assert.NotNull((await Recordings.GetByIdAsync(recordingId))!.EndedAt);
        Assert.Single(publisher.Finished);
    }

    [Fact]
    public async Task FinalizeAllOpenAsync_UsesFileMtime_AsEndedAt()
    {
        var storageDir = NewStorageDir();
        const long roomId = 900006L;
        const string fileName = "[RAW][900006][主播]2026-09-16-08-00-00-场次.flv";
        var roomDir = Path.Combine(storageDir, roomId.ToString());
        Directory.CreateDirectory(roomDir);
        var path = Path.Combine(roomDir, fileName);
        await File.WriteAllTextAsync(path, "flv");
        var mtime = File.GetLastWriteTimeUtc(path);

        var publisher = new FakeRecordingEventPublisher();
        var finalizer = CreateFinalizer(storageDir, publisher);
        var recordingId = await InsertOpenAsync(roomId, files: [fileName]);

        await finalizer.FinalizeAllOpenAsync();

        var endedAt = (await Recordings.GetByIdAsync(recordingId))!.EndedAt;
        Assert.NotNull(endedAt);
        // BSON 毫秒精度
        Assert.Equal(mtime.Ticks - (mtime.Ticks % TimeSpan.TicksPerMillisecond),
            endedAt!.Value.Ticks - (endedAt.Value.Ticks % TimeSpan.TicksPerMillisecond));
    }
}
