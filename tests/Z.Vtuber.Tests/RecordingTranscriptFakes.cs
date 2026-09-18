using Microsoft.Extensions.Logging;
using Z.Vtuber.Common.Live;
using Z.Vtuber.Recording.Recording;
using Z.Vtuber.RecordingTranscript.Api.Clients;

namespace Z.Vtuber.Tests;

/// <summary>
/// 假 file-stats 客户端：默认所有文件磁盘存在（size 取配置或 0），
/// 可通过 <see cref="Missing"/> 标记不存在文件、<see cref="Throw"/> 模拟 recording 不可达。
/// </summary>
public sealed class FakeRecordingFileStatsClient : IRecordingFileStatsClient
{
    public bool Throw { get; set; }

    public HashSet<(long RoomId, string FileName)> Missing { get; } = [];

    public Dictionary<(long RoomId, string FileName), long> Sizes { get; } = [];

    public List<RecordingFileStatsQuery> Queries { get; } = [];

    public Task<IReadOnlyDictionary<(long RoomId, string FileName), RecordingFileStat>> GetFileStatsAsync(
        IReadOnlyList<RecordingFileStatsQuery> queries, CancellationToken ct = default)
    {
        if (Throw)
        {
            throw new HttpRequestException("recording service unavailable");
        }

        Queries.AddRange(queries);
        var result = new Dictionary<(long RoomId, string FileName), RecordingFileStat>();
        foreach (var query in queries)
        {
            foreach (var file in query.Files)
            {
                var key = (query.RoomId, file);
                var exists = !Missing.Contains(key);
                result[key] = new RecordingFileStat(
                    exists, exists ? Sizes.GetValueOrDefault(key) : null);
            }
        }

        return Task.FromResult<IReadOnlyDictionary<(long RoomId, string FileName), RecordingFileStat>>(result);
    }
}


/// <summary>内存事件发布器：记录发布的 started/finished 事件，可配置抛错以验证调用方容错。</summary>
public sealed class FakeRecordingEventPublisher : IRecordingEventPublisher
{
    public List<RecordingStartedEvent> Started { get; } = [];
    public List<RecordingFinishedEvent> Finished { get; } = [];

    /// <summary>为 true 时发布抛异常，用于验证「发布失败仅记日志不阻断录制」。</summary>
    public bool ThrowOnPublish { get; set; }

    public Task PublishStartedAsync(RecordingStartedEvent recordingEvent, CancellationToken ct = default)
    {
        if (ThrowOnPublish)
        {
            throw new InvalidOperationException("started publish failed");
        }

        Started.Add(recordingEvent);
        return Task.CompletedTask;
    }

    public Task PublishFinishedAsync(RecordingFinishedEvent recordingEvent, CancellationToken ct = default)
    {
        if (ThrowOnPublish)
        {
            throw new InvalidOperationException("finished publish failed");
        }

        Finished.Add(recordingEvent);
        return Task.CompletedTask;
    }
}

/// <summary>捕获日志条目的测试 logger（用于断言 warning 已记录）。</summary>
public sealed class ListLogger<T> : ILogger<T>
{
    public List<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = [];

    public bool HasWarning => Entries.Any(e => e.Level == LogLevel.Warning);

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter) =>
        Entries.Add((logLevel, formatter(state, exception), exception));
}
