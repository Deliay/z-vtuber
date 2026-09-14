using Z.Vtuber.Common.Persistence;
using Z.Vtuber.Common.Persistence.Entities;

namespace Z.Vtuber.Recording.Recording;

/// <summary>录制状态上报（RoomId 键 upsert）。</summary>
public sealed class RecordingStatusReporter
{
    private readonly RecordingStatusRepository _repository;

    public RecordingStatusReporter(RecordingStatusRepository repository)
    {
        _repository = repository;
    }

    public string HostName { get; init; } = Environment.MachineName;

    public Task ReportNotLiveAsync(long roomId, CancellationToken ct = default) =>
        _repository.UpsertAsync(new RecordingStatusRecord
        {
            RoomId = roomId,
            State = RecordingStates.NotLive,
            HostName = HostName,
            Files = [],
            UpdatedAt = DateTime.UtcNow,
        }, ct);

    public Task ReportRecordingAsync(
        long roomId,
        string? sessionTitle,
        IReadOnlyList<string> files,
        CancellationToken ct = default) =>
        _repository.UpsertAsync(new RecordingStatusRecord
        {
            RoomId = roomId,
            State = RecordingStates.Recording,
            HostName = HostName,
            SessionTitle = sessionTitle,
            Files = [.. files],
            UpdatedAt = DateTime.UtcNow,
        }, ct);

    public Task ReportErrorAsync(
        long roomId,
        string error,
        string? sessionTitle = null,
        CancellationToken ct = default) =>
        _repository.UpsertAsync(new RecordingStatusRecord
        {
            RoomId = roomId,
            State = RecordingStates.Error,
            HostName = HostName,
            SessionTitle = sessionTitle,
            Files = [],
            Error = error,
            UpdatedAt = DateTime.UtcNow,
        }, ct);

    public Task TouchAsync(long roomId, CancellationToken ct = default) =>
        _repository.TouchAsync(roomId, DateTime.UtcNow, ct);
}
