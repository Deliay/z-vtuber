using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Net.WebSockets;
using Z.Vtuber.RecordingTranscript.Consumer.Asr;

namespace Z.Vtuber.Tests;

/// <summary>
/// 本地假 FunASR websocket server：基于 TcpListener + WebSocket.CreateFromStream，
/// 按 run-task/finish-task 指令回放 <see cref="FakeFunAsrConnection.Results"/>，
/// 并支持多种失败模拟（FailOnRunTaskJson / NeverStart / AbortAfterFirstBinary / SilentOnFinish）。
/// </summary>
internal sealed class FakeFunAsrServer : IAsyncDisposable
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

/// <summary>单条假连接：记录收到的文本/二进制消息，按配置回放服务端事件或模拟异常。</summary>
internal sealed class FakeFunAsrConnection
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

    /// <summary>收到 run-task 后既不回 task-started 也不关闭连接，用于验证启动超时。</summary>
    public bool NeverStart { get; set; }

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
            var action = FunAsrProtocol.TryParseAction(json, out var parsedAction) ? parsedAction : null;
            if (action == "run-task")
            {
                if (FailOnRunTaskJson is not null)
                {
                    await SendTextAsync(socket, FailOnRunTaskJson);
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "failed", CancellationToken.None);
                    return;
                }

                if (NeverStart)
                {
                    // 模拟服务端收到 run-task 后既不回 task-started 也不关闭连接（等客户端启动超时）
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
