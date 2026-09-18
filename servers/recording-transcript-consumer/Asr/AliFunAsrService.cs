using System.Diagnostics;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Channels;
using Z.Vtuber.Common.Persistence.Entities;
using Z.Vtuber.RecordingTranscript.Consumer.Configuration;

namespace Z.Vtuber.RecordingTranscript.Consumer.Asr;

public interface IStreamingAsrServiceFactory
{
    IStreamingAsrService Create(TranscriptModelRecord model, long offset, IReadOnlyList<string> context);
}

public sealed class AliFunAsrServiceFactory(
    TranscriptConsumerOptions options,
    ILoggerFactory loggerFactory) : IStreamingAsrServiceFactory
{
    public IStreamingAsrService Create(TranscriptModelRecord model, long offset, IReadOnlyList<string> context) =>
        new AliFunAsrService(
            new Uri(options.AsrEndpoint),
            model.ApiKey,
            offset,
            model.Model,
            context,
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
    private const int MaxRawLogLength = 2048;
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(15);

    private readonly Uri _endpoint;
    private readonly string _apiKey;
    private readonly long _offset;
    private readonly string _model;
    private readonly IReadOnlyList<string> _context;
    private readonly TimeSpan _idleTimeout;
    private readonly TimeSpan _startTimeout;
    private readonly ILogger _logger;

    /// <summary>识别链路阶段，仅用于失败日志标注；跨任务共享读取，允许少量竞态。</summary>
    private enum AsrPhase
    {
        Connecting,
        Starting,
        Streaming,
        Finishing,
    }

    /// <summary>单次识别的运行统计：connect 起计时，send/receive/主枚举各写各字段，失败日志统一读取。</summary>
    private sealed class RunStats
    {
        private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
        private long _bytesSent;
        private int _segmentsYielded;

        public long ElapsedMs => _stopwatch.ElapsedMilliseconds;

        public long BytesSent => Interlocked.Read(ref _bytesSent);

        public int SegmentsYielded => Volatile.Read(ref _segmentsYielded);

        public void AddBytes(int count) => Interlocked.Add(ref _bytesSent, count);

        public void IncrementSegments() => Interlocked.Increment(ref _segmentsYielded);
    }

    private AsrPhase _phase = AsrPhase.Connecting;

    public AliFunAsrService(
        Uri endpoint,
        string apiKey,
        long offset,
        string model,
        IReadOnlyList<string> context,
        TimeSpan idleTimeout,
        ILogger logger,
        TimeSpan? startTimeout = null)
    {
        _endpoint = endpoint;
        _apiKey = apiKey;
        _offset = offset;
        _model = model;
        _context = context;
        _idleTimeout = idleTimeout;
        _startTimeout = startTimeout ?? StartTimeout;
        _logger = logger;
    }

    public async IAsyncEnumerable<StreamingAsrSegment> TranscribeAsync(
        Stream stream, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var stats = new RunStats();
        _phase = AsrPhase.Connecting;

        using var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Authorization", $"bearer {_apiKey}");
        socket.Options.SetRequestHeader("X-DashScope-DataInspection", "enable");

        _logger.LogInformation(
            "FunASR connecting: endpoint={Endpoint} model={Model} offset={Offset}ms",
            _endpoint, _model, _offset);
        try
        {
            await socket.ConnectAsync(_endpoint, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogFailure(
                AsrPhase.Connecting, null, stats, "connect-failed",
                $"endpoint={_endpoint} offset={_offset}ms error={ex.Message}");
            throw;
        }

        _logger.LogInformation("FunASR connected: endpoint={Endpoint}", _endpoint);

        var taskId = Guid.NewGuid().ToString("N");
        await SendTextAsync(socket, FunAsrProtocol.BuildRunTask(taskId, _model, _context), ct);
        _phase = AsrPhase.Starting;
        _logger.LogInformation(
            "FunASR run-task sent: taskId={TaskId} model={Model} contextLines={ContextLines}",
            taskId, _model, _context.Count);

        var channel = Channel.CreateUnbounded<StreamingAsrSegment>(
            new UnboundedChannelOptions { SingleWriter = true, SingleReader = true });
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        var receiveTask = Task.Run(
            () => ReceiveLoopAsync(socket, channel.Writer, started, taskId, stats, linkedCts.Token), linkedCts.Token);
        var sendTask = Task.Run(
            () => SendAudioLoopAsync(socket, stream, taskId, started.Task, stats, linkedCts.Token), linkedCts.Token);

        // send/receive 任一任务异常（如无 task-started 超时、服务端停滞、websocket 异常关闭）都必须让主枚举
        // 立即观测到，否则 channel 永不完成会导致任务永久悬挂并持续续期占住并发额度。
        // 注意：不能等 Task.WhenAll——两个任务中一方先失败时另一方可能仍在阻塞，会导致失败被延迟数秒才抛出。
        _ = FaultChannelOnFailure(receiveTask, channel.Writer);
        _ = FaultChannelOnFailure(sendTask, channel.Writer);

        try
        {
            await foreach (var segment in channel.Reader.ReadAllAsync(ct))
            {
                stats.IncrementSegments();
                if (stats.SegmentsYielded % ProgressLogEverySegments == 0)
                {
                    _logger.LogInformation(
                        "FunASR progress: taskId={TaskId} segments={Count} lastEnd={LastEnd}ms",
                        taskId, stats.SegmentsYielded, segment.End);
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
                "FunASR transcribe finished: taskId={TaskId} segments={Count}", taskId, stats.SegmentsYielded);
        }
    }

    /// <summary>任一后台任务失败即用其根异常完成 channel，使主枚举立即抛出（而非等待所有任务收尾）。</summary>
    private static Task FaultChannelOnFailure(Task task, ChannelWriter<StreamingAsrSegment> writer)
    {
        return task.ContinueWith(
            t =>
            {
                if (t.IsFaulted && t.Exception is not null)
                {
                    writer.TryComplete(t.Exception.GetBaseException());
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private async Task ReceiveLoopAsync(
        ClientWebSocket socket,
        ChannelWriter<StreamingAsrSegment> writer,
        TaskCompletionSource started,
        string taskId,
        RunStats stats,
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
                        LogFailure(
                            _phase, taskId, stats, "connection-closed",
                            $"closeStatus={result.CloseStatus} closeStatusDescription={result.CloseStatusDescription} finishedNormally={finishedNormally}");
                        writer.TryComplete(finishedNormally
                            ? null
                            : new IOException(
                                $"[{_phase}] FunASR connection closed before task-finished (closeStatus={result.CloseStatus}, description={result.CloseStatusDescription})"));
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
                        // task-failed 视为已进入识别链路；记录三方错误码/描述与原始报文（截断）供排查
                        _phase = AsrPhase.Streaming;
                        FunAsrProtocol.TryParseTaskFailed(json, out var errorCode, out var errorMessage);
                        LogFailure(
                            _phase, taskId, stats, "task-failed",
                            $"errorCode={errorCode ?? "-"} errorMessage={errorMessage ?? "-"} raw={TruncateRaw(json)}");
                        writer.TryComplete(new InvalidOperationException(
                            $"[{_phase}] FunASR task failed ({errorCode}): {errorMessage}"));
                        return;
                    default:
                        _logger.LogDebug("FunASR unknown event ignored: taskId={TaskId} event={Event}", taskId, @event);
                        break;
                }
            }

            LogFailure(_phase, taskId, stats, "connection-ended", $"finishedNormally={finishedNormally}");
            writer.TryComplete(finishedNormally
                ? null
                : new IOException($"[{_phase}] FunASR connection ended before task-finished"));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // 主流程取消（停机/锁释放）
            writer.TryComplete();
        }
        catch (OperationCanceledException)
        {
            // idleCts 触发：服务端超过 AsrIdleTimeoutSeconds 无任何消息 → 识别卡死，FAILED 可重试
            LogFailure(
                _phase, taskId, stats, "idle-timeout",
                $"timeoutSeconds={_idleTimeout.TotalSeconds}");
            writer.TryComplete(new TimeoutException(
                $"[{_phase}] FunASR produced no message for {_idleTimeout.TotalSeconds}s; treating task as stuck"));
        }
        catch (Exception ex)
        {
            LogFailure(_phase, taskId, stats, "receive-loop-failed", $"error={ex.Message}", ex);
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
        RunStats stats,
        CancellationToken ct)
    {
        try
        {
            await started.WaitAsync(_startTimeout, ct);
        }
        catch (TimeoutException)
        {
            LogFailure(
                AsrPhase.Starting, taskId, stats, "task-started-timeout",
                $"timeoutSeconds={_startTimeout.TotalSeconds}");
            throw new TimeoutException(
                $"[{AsrPhase.Starting}] FunASR task-started not received within {_startTimeout.TotalSeconds}s: taskId={taskId}");
        }

        _phase = AsrPhase.Streaming;

        var buffer = new byte[SendChunkBytes];
        int read;
        while ((read = await stream.ReadAsync(buffer, ct)) > 0)
        {
            await socket.SendAsync(
                new ArraySegment<byte>(buffer, 0, read), WebSocketMessageType.Binary, true, ct);
            stats.AddBytes(read);
            _logger.LogDebug("FunASR audio sent: taskId={TaskId} bytes={Bytes}", taskId, stats.BytesSent);
        }

        await SendTextAsync(socket, FunAsrProtocol.BuildFinishTask(taskId), ct);
        _phase = AsrPhase.Finishing;
        _logger.LogInformation(
            "FunASR finish-task sent: taskId={TaskId} audioBytes={Bytes}", taskId, stats.BytesSent);
    }

    /// <summary>统一失败日志：固定输出 phase/taskId/model/elapsedMs/bytesSent/segmentsYielded，details 为场景特有字段。</summary>
    private void LogFailure(
        AsrPhase phase,
        string? taskId,
        RunStats stats,
        string reason,
        string details,
        Exception? exception = null)
    {
        _logger.LogWarning(
            exception,
            "FunASR failure {Reason}: phase={Phase} taskId={TaskId} model={Model} elapsedMs={ElapsedMs} bytesSent={BytesSent} segmentsYielded={SegmentsYielded} {Details}",
            reason,
            phase,
            taskId ?? "-",
            _model,
            stats.ElapsedMs,
            stats.BytesSent,
            stats.SegmentsYielded,
            details);
    }

    /// <summary>原始报文超过上限即截断并追加标记，控制日志体积同时标明不完整。</summary>
    private static string TruncateRaw(string raw) =>
        raw.Length <= MaxRawLogLength ? raw : raw[..MaxRawLogLength] + "...(truncated)";

    private static Task SendTextAsync(ClientWebSocket socket, string json, CancellationToken ct) =>
        socket.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, ct);
}
