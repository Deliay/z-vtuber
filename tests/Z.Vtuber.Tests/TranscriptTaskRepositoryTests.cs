using MongoDB.Bson;
using MongoDB.Driver;
using Z.Vtuber.Common.Persistence;
using Z.Vtuber.Common.Persistence.Entities;

namespace Z.Vtuber.Tests;

public class TranscriptTaskRepositoryTests(MongoDbFixture fixture) : IClassFixture<MongoDbFixture>, IAsyncLifetime
{
    public Task InitializeAsync() => fixture.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private TranscriptTaskRepository Tasks => new(fixture.Database);

    private TranscriptSegmentRepository Segments => new(fixture.Database);

    private TranscriptModelRepository Models => new(fixture.Database);

    private TranscriptAutoEnqueueRepository AutoEnqueue => new(fixture.Database);

    private static TranscriptTaskRecord TaskRecord(
        ObjectId recordingId, ObjectId modelId,
        string status = TranscriptTaskStatus.Pending, long roomId = 100)
    {
        var now = DateTime.UtcNow;
        return new TranscriptTaskRecord
        {
            RecordingId = recordingId,
            RoomId = roomId,
            File = "a.flv",
            ModelId = modelId,
            Type = TranscriptTaskTypes.Recording,
            Mode = TranscriptTaskModes.File,
            Source = TranscriptTaskSources.Manual,
            Status = status,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    [Fact]
    public async Task Insert_DuplicateRecordingAndModel_ThrowsDuplicateKey()
    {
        var repository = Tasks;
        var recordingId = ObjectId.GenerateNewId();
        var modelId = ObjectId.GenerateNewId();
        await repository.InsertAsync(TaskRecord(recordingId, modelId));

        var ex = await Assert.ThrowsAsync<MongoWriteException>(
            () => repository.InsertAsync(TaskRecord(recordingId, modelId)));

        Assert.Equal(ServerErrorCategory.DuplicateKey, ex.WriteError?.Category);
    }

    [Fact]
    public async Task Insert_SameRecordingDifferentModel_IsAllowed()
    {
        var repository = Tasks;
        var recordingId = ObjectId.GenerateNewId();
        await repository.InsertAsync(TaskRecord(recordingId, ObjectId.GenerateNewId()));
        await repository.InsertAsync(TaskRecord(recordingId, ObjectId.GenerateNewId()));

        var items = await repository.ListAsync(recordingId, null, null, 10);

        Assert.Equal(2, items.Count);
    }

    [Fact]
    public async Task GetByRecordingAndModel_ReturnsMatchingRecord()
    {
        var repository = Tasks;
        var recordingId = ObjectId.GenerateNewId();
        var modelId = ObjectId.GenerateNewId();
        var id = await repository.InsertAsync(TaskRecord(recordingId, modelId));

        var record = await repository.GetByRecordingAndModelAsync(recordingId, modelId);

        Assert.NotNull(record);
        Assert.Equal(id, record!.Id);
    }

    [Fact]
    public async Task ListScannable_ReturnsPendingAndRunning_InIdOrder_ExcludesTerminal()
    {
        var repository = Tasks;
        var modelId = ObjectId.GenerateNewId();
        var pending1 = await repository.InsertAsync(
            TaskRecord(ObjectId.GenerateNewId(), modelId, TranscriptTaskStatus.Pending));
        var done = await repository.InsertAsync(
            TaskRecord(ObjectId.GenerateNewId(), modelId, TranscriptTaskStatus.Done));
        var running = await repository.InsertAsync(
            TaskRecord(ObjectId.GenerateNewId(), modelId, TranscriptTaskStatus.Running));
        var pending2 = await repository.InsertAsync(
            TaskRecord(ObjectId.GenerateNewId(), modelId, TranscriptTaskStatus.Pending));
        var failed = await repository.InsertAsync(
            TaskRecord(ObjectId.GenerateNewId(), modelId, TranscriptTaskStatus.Failed));

        var scannable = await repository.ListScannableAsync(100);

        Assert.Equal(new[] { pending1, running, pending2 }, scannable.Select(t => t.Id).ToArray());
        Assert.DoesNotContain(done, scannable.Select(t => t.Id));
        Assert.DoesNotContain(failed, scannable.Select(t => t.Id));
    }

    [Fact]
    public async Task ListScannable_RespectsLimit()
    {
        var repository = Tasks;
        var modelId = ObjectId.GenerateNewId();
        for (var i = 0; i < 5; i++)
        {
            await repository.InsertAsync(TaskRecord(ObjectId.GenerateNewId(), modelId));
        }

        var scannable = await repository.ListScannableAsync(2);

        Assert.Equal(2, scannable.Count);
    }

    [Fact]
    public async Task SetStatus_TransitionsAndClearsErrorOnRetry()
    {
        var repository = Tasks;
        var id = await repository.InsertAsync(
            TaskRecord(ObjectId.GenerateNewId(), ObjectId.GenerateNewId()));

        await repository.SetStatusAsync(id, TranscriptTaskStatus.Running);
        var running = await repository.GetByIdAsync(id);
        Assert.Equal(TranscriptTaskStatus.Running, running!.Status);
        Assert.Null(running.Error);

        await repository.SetStatusAsync(id, TranscriptTaskStatus.Failed, "boom");
        var failed = await repository.GetByIdAsync(id);
        Assert.Equal(TranscriptTaskStatus.Failed, failed!.Status);
        Assert.Equal("boom", failed.Error);

        await repository.SetStatusAsync(id, TranscriptTaskStatus.Running);
        var retried = await repository.GetByIdAsync(id);
        Assert.Equal(TranscriptTaskStatus.Running, retried!.Status);
        Assert.Null(retried.Error);

        await repository.SetStatusAsync(id, TranscriptTaskStatus.Done);
        var done = await repository.GetByIdAsync(id);
        Assert.Equal(TranscriptTaskStatus.Done, done!.Status);
    }

    [Fact]
    public async Task UpdatePosition_AdvancesBreakpoint()
    {
        var repository = Tasks;
        var id = await repository.InsertAsync(
            TaskRecord(ObjectId.GenerateNewId(), ObjectId.GenerateNewId()));

        await repository.UpdatePositionAsync(id, 12345);
        await repository.UpdatePositionAsync(id, 67890);

        var record = await repository.GetByIdAsync(id);
        Assert.Equal(67890, record!.Position);
    }

    [Fact]
    public async Task CountByModelAndStatus_CountsOnlyMatching()
    {
        var repository = Tasks;
        var modelId = ObjectId.GenerateNewId();
        await repository.InsertAsync(TaskRecord(ObjectId.GenerateNewId(), modelId, TranscriptTaskStatus.Running));
        await repository.InsertAsync(TaskRecord(ObjectId.GenerateNewId(), modelId, TranscriptTaskStatus.Running));
        await repository.InsertAsync(TaskRecord(ObjectId.GenerateNewId(), modelId, TranscriptTaskStatus.Pending));
        await repository.InsertAsync(TaskRecord(ObjectId.GenerateNewId(), ObjectId.GenerateNewId(), TranscriptTaskStatus.Running));

        var count = await repository.CountByModelAndStatusAsync(modelId, TranscriptTaskStatus.Running);

        Assert.Equal(2, count);
    }

    [Fact]
    public async Task List_FiltersByRoomAndStatus_OrderedByCreatedAtDesc()
    {
        var repository = Tasks;
        var modelId = ObjectId.GenerateNewId();
        var baseTime = new DateTime(2026, 9, 16, 0, 0, 0, DateTimeKind.Utc);

        for (var i = 0; i < 3; i++)
        {
            var record = TaskRecord(ObjectId.GenerateNewId(), modelId, roomId: 200);
            record.CreatedAt = baseTime.AddMinutes(i);
            record.UpdatedAt = record.CreatedAt;
            await repository.InsertAsync(record);
        }

        var other = TaskRecord(ObjectId.GenerateNewId(), modelId, roomId: 300);
        other.CreatedAt = baseTime.AddMinutes(10);
        other.UpdatedAt = other.CreatedAt;
        await repository.InsertAsync(other);

        var items = await repository.ListAsync(null, 200, null, 10);

        Assert.Equal(3, items.Count);
        Assert.True(items[0].CreatedAt >= items[1].CreatedAt);
        Assert.True(items[1].CreatedAt >= items[2].CreatedAt);
    }

    [Fact]
    public async Task Segment_Insert_DuplicateTaskAndStartTime_ThrowsDuplicateKey()
    {
        var repository = Segments;
        var taskId = ObjectId.GenerateNewId();
        await repository.InsertAsync(new TranscriptSegmentRecord
        {
            TaskId = taskId,
            StartTime = 0,
            EndTime = 100,
            Text = "hello",
        });

        var ex = await Assert.ThrowsAsync<MongoWriteException>(() => repository.InsertAsync(new TranscriptSegmentRecord
        {
            TaskId = taskId,
            StartTime = 0,
            EndTime = 200,
            Text = "duplicate",
        }));

        Assert.Equal(ServerErrorCategory.DuplicateKey, ex.WriteError?.Category);
    }

    [Fact]
    public async Task Segment_ListByTask_OrderedByStartTime()
    {
        var repository = Segments;
        var taskId = ObjectId.GenerateNewId();
        var otherTaskId = ObjectId.GenerateNewId();

        await repository.InsertAsync(new TranscriptSegmentRecord { TaskId = taskId, StartTime = 300, EndTime = 400, Text = "c" });
        await repository.InsertAsync(new TranscriptSegmentRecord { TaskId = taskId, StartTime = 100, EndTime = 200, Text = "a" });
        await repository.InsertAsync(new TranscriptSegmentRecord { TaskId = taskId, StartTime = 200, EndTime = 300, Text = "b" });
        await repository.InsertAsync(new TranscriptSegmentRecord { TaskId = otherTaskId, StartTime = 50, EndTime = 60, Text = "x" });

        var segments = await repository.ListByTaskAsync(taskId);

        Assert.Equal(new[] { 100L, 200L, 300L }, segments.Select(s => s.StartTime).ToArray());
    }

    [Fact]
    public async Task Models_PartialUnique_PreventsTwoDefaults()
    {
        var repository = Models;
        await repository.InsertAsync(new TranscriptModelRecord
        {
            Name = "default-model",
            Provider = "ali-fun-asr",
            Type = "streaming",
            ApiKey = "k1",
            Enabled = true,
            IsDefault = true,
            CreatedAt = DateTime.UtcNow,
        });

        var ex = await Assert.ThrowsAsync<MongoWriteException>(() => repository.InsertAsync(new TranscriptModelRecord
        {
            Name = "second-default",
            Provider = "ali-fun-asr",
            Type = "streaming",
            ApiKey = "k2",
            Enabled = true,
            IsDefault = true,
            CreatedAt = DateTime.UtcNow,
        }));

        Assert.Equal(ServerErrorCategory.DuplicateKey, ex.WriteError?.Category);

        await repository.InsertAsync(new TranscriptModelRecord
        {
            Name = "non-default-1",
            Provider = "ali-fun-asr",
            Type = "streaming",
            ApiKey = "k3",
            Enabled = true,
            IsDefault = false,
            CreatedAt = DateTime.UtcNow,
        });
        await repository.InsertAsync(new TranscriptModelRecord
        {
            Name = "non-default-2",
            Provider = "ali-fun-asr",
            Type = "streaming",
            ApiKey = "k4",
            Enabled = true,
            IsDefault = false,
            CreatedAt = DateTime.UtcNow,
        });

        var all = await repository.ListAsync();
        Assert.Equal(3, all.Count);
        Assert.Single(all, m => m.IsDefault);
    }

    [Fact]
    public async Task Models_SetDefault_ClearsOtherDefaults()
    {
        var repository = Models;
        var first = await repository.InsertAsync(new TranscriptModelRecord
        {
            Name = "first",
            IsDefault = true,
            CreatedAt = DateTime.UtcNow,
        });
        var second = await repository.InsertAsync(new TranscriptModelRecord
        {
            Name = "second",
            IsDefault = false,
            CreatedAt = DateTime.UtcNow,
        });

        await repository.SetDefaultAsync(second);

        var all = await repository.ListAsync();
        Assert.Single(all, m => m.IsDefault);
        Assert.True(all.Single(m => m.Id == second).IsDefault);
        Assert.False(all.Single(m => m.Id == first).IsDefault);
        Assert.Equal(second, (await repository.GetDefaultAsync())!.Id);
    }

    [Fact]
    public async Task Models_Update_UpdatesOnlyProvidedFields()
    {
        var repository = Models;
        var id = await repository.InsertAsync(new TranscriptModelRecord
        {
            Name = "model",
            Provider = "ali-fun-asr",
            Type = "streaming",
            ApiKey = "secret",
            Enabled = true,
            MaxConcurrency = 1,
            CreatedAt = DateTime.UtcNow,
        });

        await repository.UpdateAsync(id, name: "renamed", enabled: false, maxConcurrency: 4);

        var record = await repository.GetByIdAsync(id);
        Assert.Equal("renamed", record!.Name);
        Assert.False(record.Enabled);
        Assert.Equal(4, record.MaxConcurrency);
        Assert.Equal("ali-fun-asr", record.Provider);
        Assert.Equal("secret", record.ApiKey);
    }

    [Fact]
    public async Task AutoEnqueue_Upsert_SameRoom_UpdatesExisting()
    {
        var repository = AutoEnqueue;

        await repository.UpsertAsync(100, 1, true);
        await repository.UpsertAsync(100, 1, false);

        var all = await repository.GetAllAsync();
        var record = Assert.Single(all, r => r.RoomId == 100);
        Assert.False(record.Enabled);
        Assert.Equal(1, record.Mid);
    }

    [Fact]
    public async Task AutoEnqueue_GetByRoomId_ReturnsMatchingOnly()
    {
        var repository = AutoEnqueue;
        await repository.UpsertAsync(100, 1, true);
        await repository.UpsertAsync(200, 2, true);

        var record = await repository.GetByRoomIdAsync(200);

        Assert.NotNull(record);
        Assert.Equal(2, record!.Mid);
    }
}
