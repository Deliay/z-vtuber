using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.Http;
using MongoDB.Bson;
using StackExchange.Redis;
using Z.Vtuber.Common.Live;
using Z.Vtuber.Common.Persistence;
using Z.Vtuber.Common.Persistence.Entities;

namespace Z.Vtuber.RecordingTranscript.Api.Sse;

/// <summary>
/// 任务实时 segments SSE：先订阅 Redis <c>transcript-segment:{taskId}</c>，再 replay 已入库 segments；
/// 以 <c>start</c> 为键去重；15s 心跳注释；任务终态（DONE/FAILED）推送 done 事件并关闭。
/// 订阅先行保证「publisher 先落库再发布」的语义下，订阅之后发布的事件不会丢失，replay 覆盖订阅前已落库的数据。
/// </summary>
public class TranscriptSegmentStream(
    TranscriptSegmentRepository segmentRepository,
    TranscriptTaskRepository taskRepository,
    IConnectionMultiplexer redis,
    ILogger<TranscriptSegmentStream> logger)
{
    internal static TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(15);

    public async Task StreamAsync(HttpResponse response, ObjectId taskId, CancellationToken ct)
    {
        response.StatusCode = StatusCodes.Status200OK;
        response.ContentType = "text/event-stream";
        response.Headers.CacheControl = "no-cache";
        response.Headers.Connection = "keep-alive";

        var taskKey = taskId.ToString();
        var seen = new HashSet<long>();

        // 订阅建立前先读一次任务播放偏移：整条流按同一口径写出（流存续期间变更不重读，
        // web 保存偏移后会主动断开重连）；去重键仍为原始 StartTime。
        var offsetMs = await LoadOffsetAsync(taskId, ct);

        var channel = new RedisChannel(
            RecordingEventChannels.SegmentChannel(taskKey),
            RedisChannel.PatternMode.Literal);
        var queue = Channel.CreateUnbounded<TranscriptSegmentEvent>();
        var subscriber = redis.GetSubscriber();

        await subscriber.SubscribeAsync(channel, (_, value) =>
        {
            try
            {
                var evt = JsonSerializer.Deserialize<TranscriptSegmentEvent>(value.ToString());
                if (evt is not null)
                {
                    queue.Writer.TryWrite(evt);
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to deserialize transcript segment event for task {TaskId}", taskKey);
            }
        });

        try
        {
            var existing = await ReplaySegmentsAsync(taskId, ct);
            foreach (var segment in existing)
            {
                if (!seen.Add(segment.StartTime))
                {
                    continue;
                }

                await WriteSegmentAsync(
                    response,
                    segment.StartTime + offsetMs,
                    segment.EndTime + offsetMs,
                    segment.Text,
                    ct);
            }

            await DrainAsync(response, queue, seen, taskKey, offsetMs, ct);

            var task = await taskRepository.GetByIdAsync(taskId, ct);
            if (task is null || IsTerminal(task.Status))
            {
                await DrainAsync(response, queue, seen, taskKey, offsetMs, ct);
                await WriteDoneAsync(response, task?.Status ?? TranscriptTaskStatus.Done, ct);
                return;
            }

            while (!ct.IsCancellationRequested)
            {
                var readTask = queue.Reader.WaitToReadAsync(ct).AsTask();
                var delayTask = Task.Delay(HeartbeatInterval, ct);
                var completed = await Task.WhenAny(readTask, delayTask);

                if (completed == delayTask)
                {
                    await WriteHeartbeatAsync(response, ct);
                    await DrainAsync(response, queue, seen, taskKey, offsetMs, ct);
                    if (await TryWriteDoneIfTerminalAsync(response, taskId, ct))
                    {
                        await DrainAsync(response, queue, seen, taskKey, offsetMs, ct);
                        break;
                    }

                    continue;
                }

                if (!await readTask)
                {
                    break;
                }

                await DrainAsync(response, queue, seen, taskKey, offsetMs, ct);

                if (await TryWriteDoneIfTerminalAsync(response, taskId, ct))
                {
                    await DrainAsync(response, queue, seen, taskKey, offsetMs, ct);
                    break;
                }
            }
        }
        finally
        {
            await subscriber.UnsubscribeAsync(channel);
        }
    }

    protected virtual Task<IReadOnlyList<TranscriptSegmentRecord>> ReplaySegmentsAsync(
        ObjectId taskId, CancellationToken ct) =>
        segmentRepository.ListByTaskAsync(taskId, ct);

    /// <summary>读取任务播放偏移（建立流时读一次；任务缺失时按 0 处理，由后续终态分支收口）。</summary>
    private async Task<long> LoadOffsetAsync(ObjectId taskId, CancellationToken ct) =>
        (await taskRepository.GetByIdAsync(taskId, ct))?.OffsetMs ?? 0;

    private static bool IsTerminal(string status) =>
        status is TranscriptTaskStatus.Done or TranscriptTaskStatus.Failed;

    private static async Task DrainAsync(
        HttpResponse response,
        Channel<TranscriptSegmentEvent> queue,
        HashSet<long> seen,
        string taskKey,
        long offsetMs,
        CancellationToken ct)
    {
        while (queue.Reader.TryRead(out var evt))
        {
            // 去重键保持原始 StartTime：偏移只影响写出值，不改变同一句的身份判定
            if (!string.Equals(evt.TaskId, taskKey, StringComparison.Ordinal) || !seen.Add(evt.Start))
            {
                continue;
            }

            await WriteSegmentAsync(response, evt.Start + offsetMs, evt.End + offsetMs, evt.Text, ct);
        }
    }

    private async Task<bool> TryWriteDoneIfTerminalAsync(
        HttpResponse response, ObjectId taskId, CancellationToken ct)
    {
        var task = await taskRepository.GetByIdAsync(taskId, ct);
        if (task is null || !IsTerminal(task.Status))
        {
            return false;
        }

        await WriteDoneAsync(response, task.Status, ct);
        return true;
    }

    private static async Task WriteSegmentAsync(
        HttpResponse response, long start, long end, string text, CancellationToken ct)
    {
        var data = JsonSerializer.Serialize(new { start, end, text });
        await response.WriteAsync($"data: {data}\n\n", ct);
        await response.Body.FlushAsync(ct);
    }

    private static async Task WriteDoneAsync(HttpResponse response, string status, CancellationToken ct)
    {
        var data = JsonSerializer.Serialize(new { status });
        await response.WriteAsync($"event: done\ndata: {data}\n\n", ct);
        await response.Body.FlushAsync(ct);
    }

    private static async Task WriteHeartbeatAsync(HttpResponse response, CancellationToken ct)
    {
        await response.WriteAsync(": heartbeat\n\n", ct);
        await response.Body.FlushAsync(ct);
    }
}
