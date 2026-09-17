using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Z.Vtuber.RecordingTranscript.Consumer.Asr;

namespace Z.Vtuber.Tests;

public class AliFunAsrOffsetTests
{
    private static string ActionOf(string json) =>
        FunAsrProtocolAccessor.TryParseAction(json, out var action) ? action! : string.Empty;

    private static string ResultJson(long begin, long end, string text, bool sentenceEnd) =>
        "{\"header\":{\"event\":\"result-generated\"},\"payload\":{\"output\":{\"sentence\":{\"begin_time\":" +
        begin + ",\"end_time\":" + end + ",\"text\":\"" + text + "\",\"sentence_end\":" +
        (sentenceEnd ? "true" : "false") + "}}}}";

    private static AliFunAsrService CreateService(
        string url,
        long offset,
        TimeSpan? idleTimeout = null,
        ILogger<AliFunAsrService>? logger = null,
        IReadOnlyList<string>? context = null) =>
        new(new Uri(url), "test-key", offset, "qwen-fun-asr-streaming", context ?? [],
            idleTimeout ?? TimeSpan.FromSeconds(5),
            logger ?? NullLogger<AliFunAsrService>.Instance);

    private static async Task<List<StreamingAsrSegment>> CollectAsync(
        IAsyncEnumerable<StreamingAsrSegment> source, CancellationToken ct = default)
    {
        var list = new List<StreamingAsrSegment>();
        await foreach (var segment in source.WithCancellation(ct))
        {
            list.Add(segment);
        }

        return list;
    }

    [Fact]
    public async Task Protocol_SendsRunTaskThenFramesThenFinishTask()
    {
        await using var server = new FakeFunAsrServer();
        server.Connection.Results.Add(ResultJson(0, 500, "hello", true));
        var service = CreateService(server.Url, 0);
        var audio = new MemoryStream(new byte[32000]);

        var segments = await CollectAsync(service.TranscribeAsync(audio));

        var parsed = FunAsrProtocol.TryParseSentence(server.Connection.Results[0], 0, out var parsedSegment);
        Assert.True(segments.Count == 1,
            $"segments={segments.Count}; parsed={parsed}; parsedSeg={parsedSegment?.Start}/{parsedSegment?.End}/{parsedSegment?.Text}; json={server.Connection.Results[0]}; binary={server.Connection.BinaryLengths.Count}");
        Assert.Equal("run-task", ActionOf(server.Connection.TextMessages[0]));
        Assert.Equal("finish-task", ActionOf(server.Connection.TextMessages[^1]));
        Assert.True(server.Connection.BinaryLengths.Count > 0);
        Assert.Equal(32000, server.Connection.BinaryLengths.Sum());
    }

    [Fact]
    public async Task OnlySentenceEnd_YieldsCompletedSentences()
    {
        await using var server = new FakeFunAsrServer();
        server.Connection.Results.Add(ResultJson(0, 300, "中", false));
        server.Connection.Results.Add(ResultJson(300, 800, "你好", true));
        var service = CreateService(server.Url, 0);
        var audio = new MemoryStream(new byte[100]);

        var segments = await CollectAsync(service.TranscribeAsync(audio));

        var segment = Assert.Single(segments);
        Assert.Equal("你好", segment.Text);
        Assert.Equal(300, segment.Start);
        Assert.Equal(800, segment.End);
    }

    [Fact]
    public async Task YieldedTime_IsPayloadTimePlusOffset()
    {
        await using var server = new FakeFunAsrServer();
        server.Connection.Results.Add(ResultJson(500, 1500, "abc", true));
        var service = CreateService(server.Url, 5000);
        var audio = new MemoryStream(new byte[100]);

        var segments = await CollectAsync(service.TranscribeAsync(audio));

        var segment = Assert.Single(segments);
        Assert.Equal(5500, segment.Start);
        Assert.Equal(6500, segment.End);
    }

    [Fact]
    public async Task Context_NonEmpty_InjectsUserMessageIntoRunTaskInput()
    {
        await using var server = new FakeFunAsrServer();
        server.Connection.Results.Add(ResultJson(0, 500, "hello", true));
        var service = CreateService(server.Url, 0, context: ["主播名", "舰长A", "SC text"]);
        var audio = new MemoryStream(new byte[100]);

        await CollectAsync(service.TranscribeAsync(audio));

        using var doc = JsonDocument.Parse(server.Connection.TextMessages[0]);
        var context = doc.RootElement.GetProperty("payload").GetProperty("input").GetProperty("context");
        var message = Assert.Single(context.EnumerateArray());
        Assert.Equal("user", message.GetProperty("role").GetString());
        var content = message.GetProperty("content").EnumerateArray().Single();
        Assert.Equal("input_text", content.GetProperty("type").GetString());
        Assert.Equal("主播名\n舰长A\nSC text", content.GetProperty("text").GetString());
    }

    [Fact]
    public async Task Context_Empty_KeepsInputObjectEmpty()
    {
        await using var server = new FakeFunAsrServer();
        server.Connection.Results.Add(ResultJson(0, 500, "hello", true));
        var service = CreateService(server.Url, 0);
        var audio = new MemoryStream(new byte[100]);

        await CollectAsync(service.TranscribeAsync(audio));

        using var doc = JsonDocument.Parse(server.Connection.TextMessages[0]);
        var input = doc.RootElement.GetProperty("payload").GetProperty("input");
        Assert.Equal(JsonValueKind.Object, input.ValueKind);
        Assert.Empty(input.EnumerateObject());
    }

    [Fact]
    public async Task InterruptedConnection_Throws()
    {
        await using var server = new FakeFunAsrServer();
        server.Connection.AbortAfterFirstBinary = true;
        var service = CreateService(server.Url, 0);
        var audio = new MemoryStream(new byte[64000]);

        await Assert.ThrowsAnyAsync<Exception>(() => CollectAsync(service.TranscribeAsync(audio)));
    }

    [Fact]
    public async Task ServerTaskFailed_SurfacesErrorCodeAndMessage()
    {
        await using var server = new FakeFunAsrServer();
        server.Connection.FailOnRunTaskJson =
            """{"header":{"event":"task-failed","error_code":"InvalidParameter","error_message":"model not exist"}}""";
        var logger = new ListLogger<AliFunAsrService>();
        var service = CreateService(server.Url, 0, logger: logger);
        var audio = new MemoryStream(new byte[100]);

        var ex = await Assert.ThrowsAnyAsync<InvalidOperationException>(
            () => CollectAsync(service.TranscribeAsync(audio)));

        Assert.Contains("model not exist", ex.Message);
        Assert.Contains("InvalidParameter", ex.Message);
        Assert.Contains(logger.Entries, e => e.Message.Contains("task-failed") && e.Message.Contains("model not exist"));
    }

    [Fact]
    public async Task IdleTimeout_ServerNeverSendsTaskFinished_ThrowsTimeout()
    {
        await using var server = new FakeFunAsrServer();
        server.Connection.SilentOnFinish = true;
        var service = CreateService(server.Url, 0, TimeSpan.FromMilliseconds(400));
        var audio = new MemoryStream(new byte[100]);

        // 服务端收到 finish-task 后沉默：停滞超时应使任务可失败重试，而非永久悬挂
        await Assert.ThrowsAnyAsync<TimeoutException>(() => CollectAsync(service.TranscribeAsync(audio)));
    }

    [Fact]
    public async Task KeyLifecycleNodes_AreLogged()
    {
        await using var server = new FakeFunAsrServer();
        server.Connection.Results.Add(ResultJson(0, 500, "hello", true));
        var logger = new ListLogger<AliFunAsrService>();
        var service = CreateService(server.Url, 0, logger: logger);
        var audio = new MemoryStream(new byte[100]);

        await CollectAsync(service.TranscribeAsync(audio));

        var messages = string.Join("\n", logger.Entries.Select(e => e.Message));
        Assert.Contains("run-task sent", messages);
        Assert.Contains("task-started", messages);
        Assert.Contains("finish-task sent", messages);
        Assert.Contains("task-finished", messages);
    }

    private sealed class FakeFunAsrServer : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _loop;

        public FakeFunAsrServer()
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Url = $"ws://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/";
            Connection = new FakeFunAsrConnection();
            _loop = Task.Run(AcceptLoopAsync);
        }

        public string Url { get; }

        public FakeFunAsrConnection Connection { get; }

        private async Task AcceptLoopAsync()
        {
            try
            {
                var client = await _listener.AcceptTcpClientAsync(_cts.Token);
                await Connection.AttachAsync(client);
                await Connection.RunAsync();
            }
            catch (OperationCanceledException)
            {
                // shutting down
            }
            catch
            {
                // connection torn down by test
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _cts.CancelAsync();
            try
            {
                _listener.Stop();
            }
            catch
            {
                // ignore
            }

            try
            {
                await _loop;
            }
            catch
            {
                // ignore
            }

            _cts.Dispose();
        }
    }

    private sealed class FakeFunAsrConnection
    {
        private TcpClient? _client;
        private NetworkStream? _stream;
        private WebSocket? _socket;

        public List<string> TextMessages { get; } = [];

        public List<int> BinaryLengths { get; } = [];

        public List<string> Results { get; } = [];

        public bool AbortAfterFirstBinary { get; set; }

        /// <summary>收到 finish-task 后不回 task-finished 且不关闭连接，用于验证停滞超时。</summary>
        public bool SilentOnFinish { get; set; }

        /// <summary>收到 run-task 后立即下发该 task-failed 事件并关闭连接（模拟模型/参数无效）。</summary>
        public string? FailOnRunTaskJson { get; set; }

        public async Task AttachAsync(TcpClient client)
        {
            _client = client;
            _stream = client.GetStream();
            var key = await ReadHandshakeKeyAsync(_stream);
            var accept = ComputeAccept(key);
            var response =
                "HTTP/1.1 101 Switching Protocols\r\n" +
                "Upgrade: websocket\r\n" +
                "Connection: Upgrade\r\n" +
                $"Sec-WebSocket-Accept: {accept}\r\n\r\n";
            var bytes = Encoding.ASCII.GetBytes(response);
            await _stream.WriteAsync(bytes);
            await _stream.FlushAsync();
            _socket = WebSocket.CreateFromStream(
                _stream, isServer: true, subProtocol: null, keepAliveInterval: TimeSpan.Zero);
        }

        public async Task RunAsync()
        {
            var socket = _socket!;
            var buffer = new byte[64 * 1024];
            while (socket.State == WebSocketState.Open)
            {
                using var message = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        return;
                    }

                    message.Write(buffer, 0, result.Count);
                }
                while (!result.EndOfMessage);

                if (result.MessageType == WebSocketMessageType.Binary)
                {
                    BinaryLengths.Add((int)message.Length);
                    if (AbortAfterFirstBinary)
                    {
                        Abort();
                        return;
                    }

                    continue;
                }

                var json = Encoding.UTF8.GetString(message.ToArray());
                TextMessages.Add(json);
                var action = ActionOf(json);
                if (action == "run-task")
                {
                    if (FailOnRunTaskJson is not null)
                    {
                        await SendTextAsync(socket, FailOnRunTaskJson);
                        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "failed", CancellationToken.None);
                        return;
                    }

                    // 服务端事件使用 header.event（官方协议）
                    await SendTextAsync(socket, """{"header":{"event":"task-started"}}""");
                }
                else if (action == "finish-task")
                {
                    if (SilentOnFinish)
                    {
                        // 模拟服务端收到 finish-task 后既不回 task-finished 也不关闭连接（识别卡死）
                        return;
                    }

                    foreach (var payload in Results)
                    {
                        await SendTextAsync(socket, payload);
                    }

                    await SendTextAsync(socket, """{"header":{"event":"task-finished"}}""");
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
                    return;
                }
            }
        }

        public void Abort()
        {
            try
            {
                _client!.Client.LingerState = new LingerOption(true, 0);
            }
            catch
            {
                // ignore
            }

            _client?.Close();
        }

        private static async Task SendTextAsync(WebSocket socket, string json) =>
            await socket.SendAsync(
                Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, CancellationToken.None);

        private static async Task<string> ReadHandshakeKeyAsync(NetworkStream stream)
        {
            var bytes = new List<byte>();
            var one = new byte[1];
            while (true)
            {
                var read = await stream.ReadAsync(one);
                if (read == 0)
                {
                    throw new IOException("client closed during handshake");
                }

                bytes.Add(one[0]);
                var count = bytes.Count;
                if (count >= 4
                    && bytes[count - 4] == 13 && bytes[count - 3] == 10
                    && bytes[count - 2] == 13 && bytes[count - 1] == 10)
                {
                    break;
                }

                if (count > 16384)
                {
                    throw new InvalidOperationException("handshake headers too large");
                }
            }

            var headers = Encoding.ASCII.GetString(bytes.ToArray());
            foreach (var line in headers.Split("\r\n"))
            {
                if (line.StartsWith("Sec-WebSocket-Key:", StringComparison.OrdinalIgnoreCase))
                {
                    return line["Sec-WebSocket-Key:".Length..].Trim();
                }
            }

            throw new InvalidOperationException("missing Sec-WebSocket-Key");
        }

        private static string ComputeAccept(string key)
        {
            const string magic = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";
            var hash = SHA1.HashData(Encoding.UTF8.GetBytes(key + magic));
            return Convert.ToBase64String(hash);
        }
    }

    /// <summary>测试访问器：FunAsrProtocol 为 internal，测试程序集通过 InternalsVisibleTo 可见。</summary>
    private static class FunAsrProtocolAccessor
    {
        public static bool TryParseAction(string json, out string? action) =>
            FunAsrProtocol.TryParseAction(json, out action);
    }
}
