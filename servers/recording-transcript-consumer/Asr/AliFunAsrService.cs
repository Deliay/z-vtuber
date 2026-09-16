using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Channels;
using Z.Vtuber.RecordingTranscript.Consumer.Configuration;

namespace Z.Vtuber.RecordingTranscript.Consumer.Asr;

public interface IStreamingAsrServiceFactory
{
    IStreamingAsrService Create(long offset, string apiKey);
}

public sealed class AliFunAsrServiceFactory(
    TranscriptConsumerOptions options,
    ILoggerFactory loggerFactory) : IStreamingAsrServiceFactory
{
    public IStreamingAsrService Create(long offset, string apiKey) =>
        new AliFunAsrService(
            new Uri(options.AsrEndpoint),
            apiKey,
            offset,
            options.AsrModel,
            TimeSpan.FromSeconds(options.AsrIdleTimeoutSeconds),
            loggerFactory.CreateLogger<AliFunAsrService>());
}

/// <summary>
/// FunASR realtime 流式识别：单任务一条 websocket，逐帧发送 raw pcm，仅 sentence_end=true 成句 yield；
/// 时间 = 返回时间 + 构造传入的 offset；连接中断/服务端停滞抛异常使任务 FAILED 可重试。
/// 端点、API Key、停滞超时均可注入，便于针对本地假 websocket server 测试。
/// </summary>
public sealed class AliFunAsrService : IStreamingAsrService
{
    private const int SendChunkBytes = 32000;
    private const int ProgressLogEverySegments = 20;
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(15);

    private readonly Uri _endpoint;
    private readonly string _apiKey;
    private readonly long _offset;
    private readonly string _model;
    private readonly TimeSpan _idleTimeout;
    private readonly ILogger _logger;

    public AliFunAsrService(
        Uri endpoint, string apiKey, long offset, string model, TimeSpan idleTimeout, ILogger logger)
    {
        _endpoint = endpoint;
        _apiKey = apiKey;
        _offset = offset;
        _model = model;
        _idleTimeout = idleTimeout;
        _logger = logger;
    }

    public async IAsyncEnumerable<StreamingAsrSegment> TranscribeAsync(
        Stream stream, [EnumeratorCancellation] CancellationToken ct = default)
    {
        using var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Authorization", $"bearer {_apiKey}");
        socket.Options.SetRequestHeader("X-DashScope-DataInspection", "enable");

        _logger.LogInformation(
            "FunASR connecting: endpoint={Endpoint} model={Model} offset={Offset}ms",
            _endpoint, _model, _offset);
        await socket.ConnectAsync(_endpoint, ct);
        _logger.LogInformation("FunASR connected: endpoint={Endpoint}", _endpoint);

        var taskId = Guid.NewGuid().ToString("N");
        await SendTextAsync(socket, FunAsrProtocol.BuildRunTask(taskId, _model), ct);
        _logger.LogInformation("FunASR run-task sent: taskId={TaskId} model={Model}", taskId, _model);

        var channel = Channel.CreateUnbounded<StreamingAsrSegment>(
            new UnboundedChannelOptions { SingleWriter = true, SingleReader = true });
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        var receiveTask = Task.Run(
            () => ReceiveLoopAsync(socket, channel.Writer, started, taskId, linkedCts.Token), linkedCts.Token);
        var sendTask = Task.Run(
            () => SendAudioLoopAsync(socket, stream, taskId, started.Task, linkedCts.Token), linkedCts.Token);

        // send/receive 任一任务异常（如无 task-started 超时、服务端停滞、websocket 异常关闭）都必须让主枚举
        // 观测到，否则 channel 永不完成会导致任务永久悬挂并持续续期占住并发额度。
        _ = Task.WhenAll(receiveTask, sendTask).ContinueWith(
            t =>
            {
                if (t.IsFaulted && t.Exception is not null)
                {
                    channel.Writer.TryComplete(t.Exception.GetBaseException());
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        var segmentCount = 0;
        try
        {
            await foreach (var segment in channel.Reader.ReadAllAsync(ct))
            {
                segmentCount++;
                if (segmentCount % ProgressLogEverySegments == 0)
                {
                    _logger.LogInformation(
                        "FunASR progress: taskId={TaskId} segments={Count} lastEnd={LastEnd}ms",
                        taskId, segmentCount, segment.End);
                }
                else
                {
                    _logger.LogDebug(
                        "FunASR segment: taskId={TaskId} start={Start} end={End}", taskId, segment.Start, segment.End);
                }

                yield return segment;
            }
        }
        finally
        {
            await linkedCts.CancelAsync();
            try
            {
                await Task.WhenAll(receiveTask, sendTask);
            }
            catch
            {
                // 主循环已通过 channel 传播异常，这里仅确保后台任务收尾
            }

            _logger.LogInformation(
                "FunASR transcribe finished: taskId={TaskId} segments={Count}", taskId, segmentCount);
        }
    }

    private async Task ReceiveLoopAsync(
        ClientWebSocket socket,
        ChannelWriter<StreamingAsrSegment> writer,
        TaskCompletionSource started,
        string taskId,
        CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        var finishedNormally = false;
        using var idleCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        idleCts.CancelAfter(_idleTimeout);

        try
        {
            while (socket.State == WebSocketState.Open)
            {
                using var message = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), idleCts.Token);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        // 未收到 task-finished 的关闭属于异常终止，不得当作识别完成；带上 CloseStatus 便于排查
                        _logger.LogWarning(
                            "FunASR connection closed: taskId={TaskId} finishedNormally={Finished} closeStatus={CloseStatus} closeStatusDescription={CloseStatusDescription}",
                            taskId,
                            finishedNormally,
                            result.CloseStatus,
                            result.CloseStatusDescription);
                        writer.TryComplete(finishedNormally
                            ? null
                            : new IOException(
                                $"FunASR connection closed before task-finished (closeStatus={result.CloseStatus}, description={result.CloseStatusDescription})"));
                        return;
                    }

                    message.Write(buffer, 0, result.Count);
                }
                while (!result.EndOfMessage);

                // 收到任何完整消息即重置停滞计时（含 result/task-started/task-finished）
                idleCts.CancelAfter(_idleTimeout);

                if (result.MessageType != WebSocketMessageType.Text)
                {
                    continue;
                }

                var json = Encoding.UTF8.GetString(message.ToArray());
                // 服务端事件使用 header.event（官方协议）
                if (!FunAsrProtocol.TryParseEvent(json, out var @event))
                {
                    continue;
                }

                switch (@event)
                {
                    case FunAsrProtocol.EventTaskStarted:
                        _logger.LogInformation("FunASR task-started: taskId={TaskId}", taskId);
                        started.TrySetResult();
                        break;
                    case FunAsrProtocol.EventResultGenerated:
                        if (FunAsrProtocol.TryParseSentence(json, _offset, out var segment) && segment is not null)
                        {
                            _logger.LogDebug(
                                "FunASR sentence: taskId={TaskId} start={Start} end={End}", taskId, segment.Start, segment.End);
                            await writer.WriteAsync(segment, ct);
                        }

                        break;
                    case FunAsrProtocol.EventTaskFinished:
                        finishedNormally = true;
                        _logger.LogInformation("FunASR task-finished: taskId={TaskId}", taskId);
                        writer.TryComplete();
                        return;
                    case FunAsrProtocol.EventTaskFailed:
                        var error = FunAsrProtocol.ParseErrorMessage(json);
                        _logger.LogWarning("FunASR task-failed: taskId={TaskId} error={Error}", taskId, error);
                        writer.TryComplete(new InvalidOperationException(error));
                        return;
                    default:
                        _logger.LogDebug("FunASR unknown event ignored: taskId={TaskId} event={Event}", taskId, @event);
                        break;
                }
            }

            _logger.LogWarning("FunASR connection ended before task-finished: taskId={TaskId}", taskId);
            writer.TryComplete(finishedNormally
                ? null
                : new IOException("FunASR connection ended before task-finished"));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // 主流程取消（停机/锁释放）
            writer.TryComplete();
        }
        catch (OperationCanceledException)
        {
            // idleCts 触发：服务端超过 AsrIdleTimeoutSeconds 无任何消息 → 识别卡死，FAILED 可重试
            _logger.LogWarning(
                "FunASR idle timeout after {Timeout}s: taskId={TaskId} (no server message, likely stuck)",
                _idleTimeout.TotalSeconds, taskId);
            writer.TryComplete(new TimeoutException(
                $"FunASR produced no message for {_idleTimeout.TotalSeconds}s; treating task as stuck"));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "FunASR websocket receive loop failed: taskId={TaskId}", taskId);
            writer.TryComplete(ex);
        }
        finally
        {
            if (!finishedNormally)
            {
                started.TrySetException(new IOException("FunASR connection closed before task-started"));
            }
        }
    }

    private async Task SendAudioLoopAsync(
        ClientWebSocket socket,
        Stream stream,
        string taskId,
        Task started,
        CancellationToken ct)
    {
        await started.WaitAsync(StartTimeout, ct);

        var buffer = new byte[SendChunkBytes];
        long totalBytes = 0;
        int read;
        while ((read = await stream.ReadAsync(buffer, ct)) > 0)
        {
            await socket.SendAsync(
                new ArraySegment<byte>(buffer, 0, read), WebSocketMessageType.Binary, true, ct);
            totalBytes += read;
            _logger.LogDebug("FunASR audio sent: taskId={TaskId} bytes={Bytes}", taskId, totalBytes);
        }

        await SendTextAsync(socket, FunAsrProtocol.BuildFinishTask(taskId), ct);
        _logger.LogInformation(
            "FunASR finish-task sent: taskId={TaskId} audioBytes={Bytes}", taskId, totalBytes);
    }

    private static Task SendTextAsync(ClientWebSocket socket, string json, CancellationToken ct) =>
        socket.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, ct);
}
