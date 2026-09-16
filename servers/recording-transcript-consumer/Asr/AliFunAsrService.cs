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
            loggerFactory.CreateLogger<AliFunAsrService>());
}

/// <summary>
/// FunASR realtime 流式识别：单任务一条 websocket，逐帧发送 raw pcm，仅 sentence_end=true 成句 yield；
/// 时间 = 返回时间 + 构造传入的 offset；连接中断抛异常使任务 FAILED。
/// 端点与 API Key 均可注入，便于针对本地假 websocket server 测试。
/// </summary>
public sealed class AliFunAsrService : IStreamingAsrService
{
    private const int SendChunkBytes = 32000;
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(15);

    private readonly Uri _endpoint;
    private readonly string _apiKey;
    private readonly long _offset;
    private readonly string _model;
    private readonly ILogger _logger;

    public AliFunAsrService(Uri endpoint, string apiKey, long offset, string model, ILogger logger)
    {
        _endpoint = endpoint;
        _apiKey = apiKey;
        _offset = offset;
        _model = model;
        _logger = logger;
    }

    public async IAsyncEnumerable<StreamingAsrSegment> TranscribeAsync(
        Stream stream, [EnumeratorCancellation] CancellationToken ct = default)
    {
        using var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Authorization", $"bearer {_apiKey}");
        socket.Options.SetRequestHeader("X-DashScope-DataInspection", "enable");

        await socket.ConnectAsync(_endpoint, ct);

        var taskId = Guid.NewGuid().ToString("N");
        await SendTextAsync(socket, FunAsrProtocol.BuildRunTask(taskId, _model), ct);

        var channel = Channel.CreateUnbounded<StreamingAsrSegment>(
            new UnboundedChannelOptions { SingleWriter = true, SingleReader = true });
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        var receiveTask = Task.Run(
            () => ReceiveLoopAsync(socket, channel.Writer, started, linkedCts.Token), linkedCts.Token);
        var sendTask = Task.Run(
            () => SendAudioLoopAsync(socket, stream, taskId, started.Task, linkedCts.Token), linkedCts.Token);

        try
        {
            await foreach (var segment in channel.Reader.ReadAllAsync(ct))
            {
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
        }
    }

    private async Task ReceiveLoopAsync(
        ClientWebSocket socket,
        ChannelWriter<StreamingAsrSegment> writer,
        TaskCompletionSource started,
        CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        try
        {
            while (socket.State == WebSocketState.Open)
            {
                using var message = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        writer.TryComplete();
                        return;
                    }

                    message.Write(buffer, 0, result.Count);
                }
                while (!result.EndOfMessage);

                if (result.MessageType != WebSocketMessageType.Text)
                {
                    continue;
                }

                var json = Encoding.UTF8.GetString(message.ToArray());
                if (!FunAsrProtocol.TryParseAction(json, out var action))
                {
                    continue;
                }

                switch (action)
                {
                    case FunAsrProtocol.ActionTaskStarted:
                        started.TrySetResult();
                        break;
                    case FunAsrProtocol.ActionResultGenerated:
                        if (FunAsrProtocol.TryParseSentence(json, _offset, out var segment) && segment is not null)
                        {
                            await writer.WriteAsync(segment, ct);
                        }
                        break;
                    case FunAsrProtocol.ActionTaskFinished:
                        writer.TryComplete();
                        return;
                    case FunAsrProtocol.ActionTaskFailed:
                        writer.TryComplete(new InvalidOperationException(FunAsrProtocol.ParseErrorMessage(json)));
                        return;
                }
            }

            writer.TryComplete();
        }
        catch (OperationCanceledException)
        {
            writer.TryComplete();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "FunASR websocket receive loop failed");
            writer.TryComplete(ex);
        }
        finally
        {
            started.TrySetException(new IOException("FunASR connection closed before task-started"));
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
        int read;
        while ((read = await stream.ReadAsync(buffer, ct)) > 0)
        {
            await socket.SendAsync(
                new ArraySegment<byte>(buffer, 0, read), WebSocketMessageType.Binary, true, ct);
        }

        await SendTextAsync(socket, FunAsrProtocol.BuildFinishTask(taskId), ct);
    }

    private static Task SendTextAsync(ClientWebSocket socket, string json, CancellationToken ct) =>
        socket.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, ct);
}
