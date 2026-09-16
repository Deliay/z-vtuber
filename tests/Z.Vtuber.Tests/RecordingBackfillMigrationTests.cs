using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using Z.Vtuber.Common.Persistence;
using Z.Vtuber.Common.Persistence.Entities;
using Z.Vtuber.Recording.Configuration;
using Z.Vtuber.Recording.Recording;

namespace Z.Vtuber.Tests;

public class RecordingBackfillMigrationTests(MongoDbFixture fixture) : IClassFixture<MongoDbFixture>, IAsyncLifetime
{
    public Task InitializeAsync() => fixture.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private RecordingRepository Recordings => new(fixture.Database);

    private string NewStorageDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "z-vtuber-backfill-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private string WriteFlv(string storageDir, long roomId, string fileName)
    {
        var roomDir = Path.Combine(storageDir, roomId.ToString());
        Directory.CreateDirectory(roomDir);
        var path = Path.Combine(roomDir, fileName);
        File.WriteAllText(path, "flv");
        return path;
    }

    private static string FileName(long roomId, string encode, string title) =>
        $"[{encode}][{roomId}][某主播]2026-09-16-08-00-00-{title}.flv";

    // BSON 日期仅精确到毫秒，比较前截断文件创建时间的亚毫秒部分。
    private static DateTime TruncateToMs(DateTime value) =>
        new(value.Ticks - (value.Ticks % TimeSpan.TicksPerMillisecond), value.Kind);

    private static DateTime CreatedAtUtc(string path)
    {
        var created = File.GetCreationTimeUtc(path);
        return created == default || created.Year < 2000 ? File.GetLastWriteTimeUtc(path) : created;
    }

    private RecordingBackfillMigration CreateMigration(string storageDir, ListLogger<RecordingBackfillMigration>? logger = null) =>
        new(fixture.Database,
            new RecordingOptions { StorageDir = storageDir, Av1 = new RecordingOptions.Av1Options() },
            logger ?? new ListLogger<RecordingBackfillMigration>());

    [Fact]
    public async Task SessionWindow_AggregatesRawAndAv1IntoOneRecording()
    {
        const long roomId = 810001L;
        var storageDir = NewStorageDir();
        var rawPath = WriteFlv(storageDir, roomId, FileName(roomId, "RAW", "晚间歌回"));
        var av1Path = WriteFlv(storageDir, roomId, FileName(roomId, "AV1", "晚间歌回"));

        var anchor = CreatedAtUtc(rawPath);
        var start = anchor.AddMinutes(-30);
        await new LiveSessionRepository(fixture.Database).UpsertAsync(new LiveSessionRecord
        {
            LiveId = "live-810001",
            RoomId = roomId,
            Title = "场次标题",
            StartDate = start,
            StopDate = anchor.AddMinutes(30),
            FetchedAt = start,
        });

        await CreateMigration(storageDir).RunAsync();

        var recordings = await Recordings.ListAsync(roomId, 10, null);
        var record = Assert.Single(recordings);
        Assert.Equal(roomId, record.RoomId);
        Assert.Equal(TruncateToMs(start), record.StartedAt);
        Assert.Equal("晚间歌回", record.Title);
        Assert.Equal(2, record.Files.Count);
        Assert.Contains(Path.GetFileName(rawPath), record.Files);
        Assert.Contains(Path.GetFileName(av1Path), record.Files);
        Assert.Equal(TruncateToMs(anchor.AddMinutes(30)), record.EndedAt);
    }

    [Fact]
    public async Task NoSession_FallsBackToLiveStatus()
    {
        const long roomId = 810002L;
        var storageDir = NewStorageDir();
        var path = WriteFlv(storageDir, roomId, FileName(roomId, "RAW", "翻唱"));
        var created = CreatedAtUtc(path);

        var status = new LiveStatusRecord
        {
            Id = ObjectId.GenerateNewId(),
            RoomId = roomId,
            Title = "状态兜底标题",
            LiveStatus = true,
            CreatedAt = created,
        };
        await new LiveStatusRepository(fixture.Database).InsertAsync(status);

        await CreateMigration(storageDir).RunAsync();

        var record = Assert.Single(await Recordings.ListAsync(roomId, 10, null));
        Assert.Equal(TruncateToMs(created), record.StartedAt);
        Assert.Equal("翻唱", record.Title);
        Assert.Single(record.Files);
    }

    [Fact]
    public async Task SessionOutsideWindow_FallsBackToLiveStatus()
    {
        const long roomId = 810005L;
        var storageDir = NewStorageDir();
        var path = WriteFlv(storageDir, roomId, FileName(roomId, "RAW", "窗口外"));
        var created = CreatedAtUtc(path);

        // 旧场次已结束且不覆盖文件创建时间：不得回退取「最近一场」，应落到 live_status 兜底
        await new LiveSessionRepository(fixture.Database).UpsertAsync(new LiveSessionRecord
        {
            LiveId = "old-810005",
            RoomId = roomId,
            Title = "旧场次",
            StartDate = created.AddHours(-5),
            StopDate = created.AddHours(-4),
            IsFinish = true,
            FetchedAt = created.AddHours(-5),
        });
        await new LiveStatusRepository(fixture.Database).InsertAsync(new LiveStatusRecord
        {
            Id = ObjectId.GenerateNewId(),
            RoomId = roomId,
            Title = "状态兜底",
            LiveStatus = true,
            CreatedAt = created,
        });

        await CreateMigration(storageDir).RunAsync();

        var record = Assert.Single(await Recordings.ListAsync(roomId, 10, null));
        Assert.Equal(TruncateToMs(created), record.StartedAt);
        Assert.Equal("窗口外", record.Title);
    }

    [Fact]
    public async Task StaleOpenSession_DoesNotSwallowFarFutureFile_FallsBackToStatus()
    {
        const long roomId = 810006L;
        var storageDir = NewStorageDir();
        var path = WriteFlv(storageDir, roomId, FileName(roomId, "RAW", "远期"));
        var created = CreatedAtUtc(path);

        // StopDate=null 的陈旧场次（10 天前）：不得吞并文件
        await new LiveSessionRepository(fixture.Database).UpsertAsync(new LiveSessionRecord
        {
            LiveId = "stale-810006",
            RoomId = roomId,
            Title = "陈旧未结束",
            StartDate = created.AddDays(-10),
            StopDate = null,
            IsFinish = false,
            FetchedAt = created.AddDays(-10),
        });
        await new LiveStatusRepository(fixture.Database).InsertAsync(new LiveStatusRecord
        {
            Id = ObjectId.GenerateNewId(),
            RoomId = roomId,
            Title = "状态兜底",
            LiveStatus = true,
            CreatedAt = created,
        });

        await CreateMigration(storageDir).RunAsync();

        var record = Assert.Single(await Recordings.ListAsync(roomId, 10, null));
        Assert.Equal(TruncateToMs(created), record.StartedAt);
    }

    [Fact]
    public async Task ReRun_IsIdempotent()
    {
        const long roomId = 810003L;
        var storageDir = NewStorageDir();
        var path = WriteFlv(storageDir, roomId, FileName(roomId, "RAW", "歌回"));
        var created = CreatedAtUtc(path);
        await new LiveSessionRepository(fixture.Database).UpsertAsync(new LiveSessionRecord
        {
            LiveId = "live-810003",
            RoomId = roomId,
            Title = "t",
            StartDate = created.AddMinutes(-10),
            StopDate = created.AddMinutes(10),
            FetchedAt = created,
        });

        var migration = CreateMigration(storageDir);
        await migration.RunAsync();
        await migration.RunAsync();

        Assert.Single(await Recordings.ListAsync(roomId, 10, null));
    }

    [Fact]
    public async Task UnmatchedFile_LogsWarningAndDoesNotBlock()
    {
        const long roomId = 810004L;
        var storageDir = NewStorageDir();
        WriteFlv(storageDir, roomId, FileName(roomId, "RAW", "无场次"));
        var logger = new ListLogger<RecordingBackfillMigration>();

        await CreateMigration(storageDir, logger).RunAsync();

        Assert.Empty(await Recordings.ListAsync(roomId, 10, null));
        Assert.True(logger.HasWarning);
    }

    [Fact]
    public void TryParseSessionName_ExtractsAnchorAndTitle_IgnoresTwelveHourClock()
    {
        var parsed = RecordingBackfillMigration.TryParseSessionName(
            "[RAW][123][某主播]2026-09-16-08-00-00-晚间歌回.flv",
            out var anchor,
            out var title);

        Assert.True(parsed);
        Assert.Equal("某主播", anchor);
        Assert.Equal("晚间歌回", title);
    }
}
