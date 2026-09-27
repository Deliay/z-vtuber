using System.Text.Json;
using MongoDB.Bson;
using Z.Vtuber.Common.Persistence.Entities;
using Z.Vtuber.Training.Contracts;

namespace Z.Vtuber.Tests;

/// <summary>
/// Plan 1.3：`TrainingMapping` 纯函数——AudioKey 采用 **原始 start 口径**
/// （<c>transcript:{taskId}:{原始startMs}</c>，原始 start = 详情返回 start − task.offsetMs），
/// <c>StartMs/EndMs</c> 保持偏移后展示口径；AudioUri 拼接；时间校验；实体 → DTO 映射。
/// </summary>
public class TrainingMappingTests
{
    private const string TaskId = "66f1a2b3c4d5e6f7a8b9c0d1";

    [Theory]
    [InlineData("task-a", 0, "transcript:task-a:0")]
    [InlineData("task-a", 1234, "transcript:task-a:1234")]
    [InlineData("task-a", 86400000, "transcript:task-a:86400000")]
    [InlineData("66f1a2b3c4d5e6f7a8b9c0d1", 1000, "transcript:66f1a2b3c4d5e6f7a8b9c0d1:1000")]
    public void GenerateAudioKey_IsTranscriptTaskAndRawStart(string taskId, long rawStartMs, string expected)
    {
        Assert.Equal(expected, TrainingMapping.GenerateAudioKey(taskId, rawStartMs));
    }

    [Fact]
    public void ToImportItem_ZeroOffset_KeyUsesStartAndTimesStayUnchanged()
    {
        var now = new DateTime(2026, 9, 27, 8, 0, 0, DateTimeKind.Utc);
        var datasetId = ObjectId.GenerateNewId();

        var item = TrainingMapping.ToImportItem(
            datasetId, TaskId, roomId: 100, file: "a.flv", offsetMs: 0,
            startMs: 1000, endMs: 1500, text: "第一句", now);

        Assert.Equal(datasetId, item.DatasetId);
        Assert.Equal("transcript", item.AudioType);
        Assert.Equal(TaskId, item.Source);
        Assert.Equal("b/live/100/recordings/a.flv", item.AudioUri);
        Assert.Equal($"transcript:{TaskId}:1000", item.AudioKey);
        Assert.Equal(1000, item.StartMs);
        Assert.Equal(1500, item.EndMs);
        Assert.Equal("第一句", item.Text);
        Assert.Equal(now, item.CreatedAt);
        Assert.Equal(now, item.UpdatedAt);
    }

    [Fact]
    public void ToImportItem_PositiveOffset_KeyUsesRawStart_WhileTimesRemainOffsetApplied()
    {
        // 任务 offset=+2500：详情返回 start = 原始 1000 + 2500 = 3500
        var item = TrainingMapping.ToImportItem(
            ObjectId.GenerateNewId(), TaskId, roomId: 100, file: "a.flv", offsetMs: 2500,
            startMs: 3500, endMs: 4000, text: "第二句", DateTime.UtcNow);

        // AudioKey 反推原始 start（不含播放偏移）
        Assert.Equal($"transcript:{TaskId}:1000", item.AudioKey);
        Assert.Equal($"transcript:{TaskId}:1000",
            TrainingMapping.GenerateAudioKey(TaskId, item.StartMs - 2500));
        // 展示口径仍为偏移后时间
        Assert.Equal(3500, item.StartMs);
        Assert.Equal(4000, item.EndMs);
    }

    [Fact]
    public void ToImportItem_NegativeOffset_ReconstructsRawStartFromOffsetAppliedStart()
    {
        // 任务 offset=-1500：详情返回 start = 原始 2000 - 1500 = 500
        var item = TrainingMapping.ToImportItem(
            ObjectId.GenerateNewId(), TaskId, roomId: 100, file: "a.flv", offsetMs: -1500,
            startMs: 500, endMs: 1000, text: "负偏移", DateTime.UtcNow);

        Assert.Equal($"transcript:{TaskId}:2000", item.AudioKey);
        Assert.Equal(500, item.StartMs);
        Assert.Equal(1000, item.EndMs);
        // 反推口径 = start − offsetMs（恒 ≥ 0）
        Assert.Equal(2000, item.StartMs - (-1500));
    }

    [Fact]
    public void ToImportItem_NegativeOffsetClampingStart_StillReconstructsNonNegativeRawStart()
    {
        // 详情返回 start 可为负（映射层不钳制）：raw = -500 - (-2500) = 2000
        var item = TrainingMapping.ToImportItem(
            ObjectId.GenerateNewId(), TaskId, roomId: 100, file: null, offsetMs: -2500,
            startMs: -500, endMs: 0, text: "钳零场景", DateTime.UtcNow);

        Assert.Equal($"transcript:{TaskId}:2000", item.AudioKey);
        Assert.Equal(-500, item.StartMs);
        Assert.Equal(string.Empty, item.AudioUri);
    }

    [Fact]
    public void ToImportItem_OffsetChangedAfterFirstImport_KeepsSameAudioKey()
    {
        // 偏差 1 锁定：任务偏移变更后（返回 start 随之平移）重复导入仍命中同一 AudioKey
        var first = TrainingMapping.ToImportItem(
            ObjectId.GenerateNewId(), TaskId, 100, "a.flv", offsetMs: 2500,
            startMs: 3500, endMs: 4000, text: "句", DateTime.UtcNow);
        var second = TrainingMapping.ToImportItem(
            ObjectId.GenerateNewId(), TaskId, 100, "a.flv", offsetMs: 0,
            startMs: 1000, endMs: 1500, text: "句", DateTime.UtcNow);

        Assert.Equal(first.AudioKey, second.AudioKey);
        Assert.NotEqual(first.StartMs, second.StartMs);
    }

    [Fact]
    public void ToImportItem_EmptyOrNullFile_YieldsEmptyAudioUri()
    {
        var nullFile = TrainingMapping.ToImportItem(
            ObjectId.GenerateNewId(), TaskId, 100, null, 0, 0, 1000, "无文件", DateTime.UtcNow);
        var blankFile = TrainingMapping.ToImportItem(
            ObjectId.GenerateNewId(), TaskId, 100, "   ", 0, 0, 1000, "空白文件", DateTime.UtcNow);

        Assert.Equal(string.Empty, nullFile.AudioUri);
        Assert.Equal(string.Empty, blankFile.AudioUri);
        Assert.Equal($"transcript:{TaskId}:0", nullFile.AudioKey);
    }

    [Theory]
    [InlineData(100, "a.flv", "b/live/100/recordings/a.flv")]
    [InlineData(300, "nested/房间.flv", "b/live/300/recordings/nested/房间.flv")]
    public void BuildAudioUri_JoinsRoomAndFile(long roomId, string file, string expected)
    {
        Assert.Equal(expected, TrainingMapping.BuildAudioUri(roomId, file));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void BuildAudioUri_BlankFile_ReturnsEmpty(string? file)
    {
        Assert.Equal(string.Empty, TrainingMapping.BuildAudioUri(100, file));
    }

    // InlineData 数值需带 L 后缀：attribute 只能写 Int32 字面量，xUnit 反射无法把 boxed Int32 绑定到 long?
    [Theory]
    [InlineData(0L, null, true)]
    [InlineData(0L, 1L, true)]
    [InlineData(1000L, 2000L, true)]
    [InlineData(1000L, null, true)]
    [InlineData(-1L, null, false)]
    [InlineData(-1L, 100L, false)]
    [InlineData(1000L, 1000L, false)]
    [InlineData(1000L, 999L, false)]
    [InlineData(0L, 0L, false)]
    public void IsValidTimeRange_ChecksStartNonNegativeAndEndAfterStart(long startMs, long? endMs, bool expected)
    {
        Assert.Equal(expected, TrainingMapping.IsValidTimeRange(startMs, endMs));
    }

    [Fact]
    public void ToDatasetDto_MapsAllFields()
    {
        var createdAt = new DateTime(2026, 9, 27, 1, 2, 3, DateTimeKind.Utc);
        var record = new TrainingDatasetRecord
        {
            Name = "第一期",
            Type = "audio",
            CreatedAt = createdAt,
            UpdatedAt = createdAt.AddMinutes(5),
        };

        var dto = TrainingMapping.ToDatasetDto(record);

        Assert.Equal(record.Id.ToString(), dto.Id);
        Assert.Equal("第一期", dto.Name);
        Assert.Equal("audio", dto.Type);
        Assert.Equal(createdAt, dto.CreatedAt);
        Assert.Equal(createdAt.AddMinutes(5), dto.UpdatedAt);
    }

    [Fact]
    public void ToItemDto_MapsAllFields_WithoutExtensions()
    {
        var now = new DateTime(2026, 9, 27, 1, 2, 3, DateTimeKind.Utc);
        var datasetId = ObjectId.GenerateNewId();
        var record = new TrainingDatasetItemRecord
        {
            DatasetId = datasetId,
            AudioType = "transcript",
            Source = TaskId,
            AudioUri = "b/live/100/recordings/a.flv",
            AudioKey = $"transcript:{TaskId}:1000",
            StartMs = 1000,
            EndMs = null,
            Text = "句子",
            CreatedAt = now,
            UpdatedAt = now,
        };

        var dto = TrainingMapping.ToItemDto(record);

        Assert.Equal(record.Id.ToString(), dto.Id);
        Assert.Equal(datasetId.ToString(), dto.DatasetId);
        Assert.Equal("transcript", dto.AudioType);
        Assert.Equal(TaskId, dto.Source);
        Assert.Equal("b/live/100/recordings/a.flv", dto.AudioUri);
        Assert.Equal($"transcript:{TaskId}:1000", dto.AudioKey);
        Assert.Equal(1000, dto.StartMs);
        Assert.Null(dto.EndMs);
        Assert.Equal("句子", dto.Text);
        Assert.Null(dto.Extensions);
        Assert.Equal(now, dto.CreatedAt);
        Assert.Equal(now, dto.UpdatedAt);
    }

    [Fact]
    public void ToItemDto_WithExtensions_EmitsJsonObject()
    {
        var record = new TrainingDatasetItemRecord
        {
            DatasetId = ObjectId.GenerateNewId(),
            AudioType = "transcript",
            Source = TaskId,
            AudioKey = $"transcript:{TaskId}:0",
            Extensions = new BsonDocument("emotion", new BsonDocument("label", "happy")),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

        var dto = TrainingMapping.ToItemDto(record);

        Assert.NotNull(dto.Extensions);
        var extensions = dto.Extensions!.Value;
        Assert.Equal(JsonValueKind.Object, extensions.ValueKind);
        Assert.Equal("happy", extensions.GetProperty("emotion").GetProperty("label").GetString());
    }
}
