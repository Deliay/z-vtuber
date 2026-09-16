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
/// 任务实时 segments SSE：先 replay 已入库 segments，再订阅 Redis <c>transcript-segment:{taskId}</c> 转发新句子；
/// 以 <c>start</c> 为键去重；15s 心跳注释；任务终态（DONE/FAILED）推送 done 事件并关闭。
/// </summary>
public sealed class TranscriptSegmentStream(
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

        var existing = await segmentRepository.ListByTaskAsync(taskId, ct);
        foreach (var segment in existing)
        {
            if (!seen.Add(segment.StartTime))
            {
                continue;
            }

            await WriteSegmentAsync(response, segment.StartTime, segment.EndTime, segment.Text, ct);
        }

        var task = await taskRepository.GetByIdAsync(taskId, ct);
        if (task is null || IsTerminal(task.Status))
        {
            await WriteDoneAsync(response, task?.Status ?? TranscriptTaskStatus.Done, ct);
            return;
        }

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
            while (!ct.IsCancellationRequested)
            {
                var readTask = queue.Reader.WaitToReadAsync(ct).AsTask();
                var delayTask = Task.Delay(HeartbeatInterval, ct);
                var completed = await Task.WhenAny(readTask, delayTask);

                if (completed == delayTask)
                {
                    await WriteHeartbeatAsync(response, ct);
                    if (await TryWriteDoneIfTerminalAsync(response, taskId, ct))
                    {
                        break;
                    }

                    continue;
                }

                if (!await readTask)
                {
                    break;
                }

                while (queue.Reader.TryRead(out var evt))
                {
                    if (!string.Equals(evt.TaskId, taskKey, StringComparison.Ordinal) || !seen.Add(evt.Start))
                    {
                        continue;
                    }

                    await WriteSegmentAsync(response, evt.Start, evt.End, evt.Text, ct);
                }

                if (await TryWriteDoneIfTerminalAsync(response, taskId, ct))
                {
                    break;
                }
            }
        }
        finally
        {
            await subscriber.UnsubscribeAsync(channel);
        }
    }

    private static bool IsTerminal(string status) =>
        status is TranscriptTaskStatus.Done or TranscriptTaskStatus.Failed;

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
