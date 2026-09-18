using Microsoft.Extensions.Logging;
using System.Net;
using System.Text;
using System.Text.Json;
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

/// <summary>
/// 回显式 file-stats HTTP 处理器：按请求逐文件返回 exists/size，
/// 记录每批 items 数量用于验证客户端分块；<see cref="Missing"/> 标记不存在文件。
/// </summary>
public sealed class EchoRecordingFileStatsHandler : HttpMessageHandler
{
    public List<int> BatchItemCounts { get; } = [];

    public HashSet<(long RoomId, string FileName)> Missing { get; } = [];

    public Dictionary<(long RoomId, string FileName), long> Sizes { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = await request.Content!.ReadAsStringAsync(cancellationToken);
        using var doc = JsonDocument.Parse(body);
        var rooms = doc.RootElement.GetProperty("items").EnumerateArray().ToList();
        BatchItemCounts.Add(rooms.Count);

        var items = rooms.Select(room =>
        {
            var roomId = room.GetProperty("roomId").GetInt64();
            var files = room.GetProperty("files").EnumerateArray()
                .Select(file => file.GetString() ?? string.Empty)
                .Select(name =>
                {
                    var exists = !Missing.Contains((roomId, name));
                    return new
                    {
                        name,
                        exists,
                        size = exists ? Sizes.GetValueOrDefault((roomId, name)) : (long?)null,
                    };
                })
                .ToArray();
            return new { roomId, files };
        }).ToArray();

        var json = JsonSerializer.Serialize(new { items });
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
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
