using MongoDB.Bson;
using Z.Vtuber.Common.Persistence.Entities;
using Z.Vtuber.RecordingTranscript.Api.Contracts;

namespace Z.Vtuber.Tests;

/// <summary>
/// Plan 1.3：实体 → DTO 映射的播放偏移口径——<c>ToSegmentDto</c> 叠加偏移且负值不钳制（保持单射，
/// 避免多句同钳 0 在 web Map key 下互相覆盖）、<c>ToTaskDto</c> 输出 <c>offsetMs</c>。
/// </summary>
public class TranscriptMappingTests
{
    private static TranscriptSegmentRecord Segment(long start, long end, string text = "句子") =>
        new()
        {
            TaskId = ObjectId.GenerateNewId(),
            StartTime = start,
            EndTime = end,
            Text = text,
        };

    private static TranscriptTaskRecord Task(long offsetMs = 0) =>
        new()
        {
            RecordingId = ObjectId.GenerateNewId(),
            RoomId = 100,
            File = "a.flv",
            ModelId = ObjectId.GenerateNewId(),
            Type = TranscriptTaskTypes.Recording,
            Mode = TranscriptTaskModes.File,
            Source = TranscriptTaskSources.Manual,
            Status = TranscriptTaskStatus.Done,
            Position = 4321,
            OffsetMs = offsetMs,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

    [Theory]
    [InlineData(0, 1000, 1500)]
    [InlineData(2500, 3500, 4000)]
    [InlineData(-1500, -500, 0)]
    public void ToSegmentDto_AppliesOffset(long offsetMs, long expectedStart, long expectedEnd)
    {
        var dto = TranscriptMapping.ToSegmentDto(Segment(1000, 1500), offsetMs);

        Assert.Equal(expectedStart, dto.Start);
        Assert.Equal(expectedEnd, dto.End);
        Assert.Equal("句子", dto.Text);
    }

    [Fact]
    public void ToSegmentDto_DefaultOffset_KeepsOriginalTimes()
    {
        var dto = TranscriptMapping.ToSegmentDto(Segment(1000, 1500));

        Assert.Equal(1000, dto.Start);
        Assert.Equal(1500, dto.End);
    }

    [Fact]
    public void ToSegmentDto_NegativeOffset_DoesNotClampToZero()
    {
        // 映射层不钳制：不同原始 start 叠加负偏移后仍互不相同（web mergeSegments 以 start 为键）
        var first = TranscriptMapping.ToSegmentDto(Segment(0, 300), -2000);
        var second = TranscriptMapping.ToSegmentDto(Segment(100, 400), -2000);

        Assert.Equal(-2000, first.Start);
        Assert.Equal(-1900, second.Start);
        Assert.NotEqual(first.Start, second.Start);
    }

    [Fact]
    public void ToTaskDto_EmitsOffsetMs()
    {
        var dto = TranscriptMapping.ToTaskDto(Task(offsetMs: 2500), null, null);

        Assert.Equal(2500, dto.OffsetMs);
        Assert.Equal(4321, dto.Position);
        Assert.Equal("DONE", dto.Status);
    }

    [Fact]
    public void ToTaskDto_LegacyTaskWithoutOffset_EmitsZero()
    {
        var dto = TranscriptMapping.ToTaskDto(Task(), null, null);

        Assert.Equal(0, dto.OffsetMs);
    }
}
