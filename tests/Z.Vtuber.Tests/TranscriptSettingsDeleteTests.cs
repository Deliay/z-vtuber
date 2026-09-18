using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Z.Vtuber.Common.Persistence;
using Z.Vtuber.Common.Persistence.Entities;
using Z.Vtuber.RecordingTranscript.Api.Configuration;
using Z.Vtuber.RecordingTranscript.Api.Controllers;

namespace Z.Vtuber.Tests;

public class TranscriptSettingsDeleteTests(MongoDbFixture mongo) : IClassFixture<MongoDbFixture>, IAsyncLifetime
{
    public Task InitializeAsync() => mongo.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private RecordingRepository Recordings => new(mongo.Database);
    private TranscriptTaskRepository Tasks => new(mongo.Database);
    private TranscriptAutoEnqueueRepository AutoEnqueue => new(mongo.Database);

    private static readonly IOptions<TranscriptApiOptions> DefaultOptions =
        Options.Create(new TranscriptApiOptions());

    private RecordingsArchiveController CreateController() =>
        WithContext(new RecordingsArchiveController(
            DefaultOptions, Recordings, Tasks, new TranscriptModelRepository(mongo.Database), AutoEnqueue,
            new AccountBasicInfoRepository(mongo.Database), new FakeRecordingFileStatsClient(),
            NullLogger<RecordingsArchiveController>.Instance));

    private static T WithContext<T>(T controller) where T : ControllerBase
    {
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
        return controller;
    }

    [Fact]
    public async Task DeleteSettings_Existing_Returns204AndRemovesRecord()
    {
        await AutoEnqueue.UpsertAsync(910001, 111, enabled: true);
        Assert.NotNull(await AutoEnqueue.GetByRoomIdAsync(910001));

        var result = await CreateController().DeleteSettings(910001);

        Assert.IsType<NoContentResult>(result);
        Assert.Null(await AutoEnqueue.GetByRoomIdAsync(910001));
    }

    [Fact]
    public async Task DeleteSettings_NotExisting_Returns404()
    {
        var result = await CreateController().DeleteSettings(910002);

        Assert.IsType<NotFoundObjectResult>(result);
    }

    [Fact]
    public async Task DeleteSettings_DoesNotAffectExistingTasks()
    {
        var recording = await Recordings.InsertAsync(new RecordingRecord
        {
            RoomId = 910003,
            Mid = 222,
            Title = "场次",
            Files = ["[RAW] a.flv"],
            StartedAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
        });
        var model = await new TranscriptModelRepository(mongo.Database).InsertAsync(new TranscriptModelRecord
        {
            Name = "model",
            Provider = "ali-fun-asr",
            Type = "streaming",
            ApiKey = "key",
            Model = "fun-asr-realtime",
            Enabled = true,
            MaxConcurrency = 1,
            CreatedAt = DateTime.UtcNow,
        });
        var task = await Tasks.InsertAsync(new TranscriptTaskRecord
        {
            RecordingId = recording,
            RoomId = 910003,
            File = "[RAW] a.flv",
            ModelId = model,
            Type = TranscriptTaskTypes.Recording,
            Mode = TranscriptTaskModes.File,
            Source = TranscriptTaskSources.Auto,
            Status = TranscriptTaskStatus.Running,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await AutoEnqueue.UpsertAsync(910003, 222, enabled: true, model);

        var result = await CreateController().DeleteSettings(910003);

        Assert.IsType<NoContentResult>(result);
        Assert.Null(await AutoEnqueue.GetByRoomIdAsync(910003));
        Assert.NotNull(await Tasks.GetByIdAsync(task));
    }
}
