using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Z.Vtuber.RecordingTranscript.Consumer.Asr;

namespace Z.Vtuber.Tests;

/// <summary>
/// 三方识别失败可观测性日志断言：四类失败（task-failed / 连接失败 / 启动超时 / 异常断开与停滞）
/// 均须落 Warn 日志，含 phase、taskId、运行统计（elapsedMs/bytesSent/segmentsYielded）与场景特有字段；
/// 同时锁定 FunAsrProtocol.TryParseTaskFailed 的解析分支与 ParseErrorMessage 行为兼容。
/// </summary>
public class AliFunAsrErrorLogTests
{
    private static AliFunAsrService CreateService(
        string url,
        long offset,
        TimeSpan? idleTimeout = null,
        ILogger<AliFunAsrService>? logger = null,
        TimeSpan? startTimeout = null) =>
        new(new Uri(url), "test-key", offset, "qwen-fun-asr-streaming", [],
            idleTimeout ?? TimeSpan.FromSeconds(5),
            logger ?? NullLogger<AliFunAsrService>.Instance,
            startTimeout);

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

    private static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    // ---- FunAsrProtocol.TryParseTaskFailed / ParseErrorMessage ----

    [Fact]
    public void TryParseTaskFailed_CompletePayload_ReturnsCodeAndMessage()
    {
        var json =
            """{"header":{"event":"task-failed","error_code":"InvalidParameter","error_message":"model not exist"}}""";

        Assert.True(FunAsrProtocol.TryParseTaskFailed(json, out var code, out var message));

        Assert.Equal("InvalidParameter", code);
        Assert.Equal("model not exist", message);
    }

    [Fact]
    public void TryParseTaskFailed_HeaderWithoutFields_ReturnsTrueWithNulls()
    {
        var json = """{"header":{"event":"task-failed"}}""";

        Assert.True(FunAsrProtocol.TryParseTaskFailed(json, out var code, out var message));

        Assert.Null(code);
        Assert.Null(message);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{"payload":{"foo":"bar"}}""")]
    public void TryParseTaskFailed_NonJsonOrNoHeader_ReturnsFalseWithNulls(string json)
    {
        Assert.False(FunAsrProtocol.TryParseTaskFailed(json, out var code, out var message));

        Assert.Null(code);
        Assert.Null(message);
    }

    [Fact]
    public void ParseErrorMessage_CompletePayload_FormatsCodeAndMessage()
    {
        var json =
            """{"header":{"event":"task-failed","error_code":"InvalidParameter","error_message":"model not exist"}}""";

        Assert.Equal(
            "FunASR task failed (InvalidParameter): model not exist",
            FunAsrProtocol.ParseErrorMessage(json));
    }

    [Fact]
    public void ParseErrorMessage_NonJson_ReturnsFixedText()
    {
        Assert.Equal("FunASR task failed", FunAsrProtocol.ParseErrorMessage("not json"));
    }

    // ---- 失败日志断言 ----

    [Fact]
    public async Task TaskFailed_LogsErrorCodeMessageRawPhaseAndStats()
    {
        await using var server = new FakeFunAsrServer();
        server.Connection.FailOnRunTaskJson =
            """{"header":{"event":"task-failed","error_code":"InvalidParameter","error_message":"model not exist","unknown":"kept"},"payload":{"foo":"bar"}}""";
        var logger = new ListLogger<AliFunAsrService>();
        var service = CreateService(server.Url, 1234, logger: logger);
        var audio = new MemoryStream(new byte[100]);

        var ex = await Assert.ThrowsAnyAsync<InvalidOperationException>(
            () => CollectAsync(service.TranscribeAsync(audio)));

        // 落库 Error（ex.Message）含阶段前缀与错误码
        Assert.Contains("[Streaming]", ex.Message);
        Assert.Contains("InvalidParameter", ex.Message);
        Assert.Contains("model not exist", ex.Message);

        var entry = Assert.Single(
            logger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("task-failed"));
        Assert.Contains("phase=Streaming", entry.Message);
        Assert.Contains("taskId=", entry.Message);
        Assert.Contains("errorCode=InvalidParameter", entry.Message);
        Assert.Contains("errorMessage=model not exist", entry.Message);
        Assert.Contains("raw=", entry.Message);
        Assert.Contains("\"unknown\":\"kept\"", entry.Message);
        Assert.Contains("elapsedMs=", entry.Message);
        Assert.Contains("bytesSent=", entry.Message);
        Assert.Contains("segmentsYielded=", entry.Message);
        Assert.DoesNotContain("test-key", entry.Message);
    }

    [Fact]
    public async Task TaskFailed_OverlongRaw_TruncatesWithMarker()
    {
        await using var server = new FakeFunAsrServer();
        var padding = new string('a', 4096);
        server.Connection.FailOnRunTaskJson =
            $"{{\"header\":{{\"event\":\"task-failed\",\"error_code\":\"X\",\"error_message\":\"boom\"}},\"payload\":{{\"p\":\"{padding}\"}}}}";
        var logger = new ListLogger<AliFunAsrService>();
        var service = CreateService(server.Url, 0, logger: logger);
        var audio = new MemoryStream(new byte[10]);

        await Assert.ThrowsAnyAsync<Exception>(() => CollectAsync(service.TranscribeAsync(audio)));

        var entry = Assert.Single(logger.Entries, e => e.Message.Contains("task-failed"));
        Assert.Contains("...(truncated)", entry.Message);
        Assert.DoesNotContain(padding, entry.Message);
    }

    [Fact]
    public async Task ConnectFailure_LogsConnectingContextAndPropagates()
    {
        var port = GetFreePort();
        var logger = new ListLogger<AliFunAsrService>();
        var service = CreateService($"ws://127.0.0.1:{port}/", 777, logger: logger);
        var audio = new MemoryStream(new byte[10]);

        var ex = await Assert.ThrowsAnyAsync<Exception>(() => CollectAsync(service.TranscribeAsync(audio)));

        // 异常类型与传播不变（连接层异常直接上抛）
        Assert.True(ex is HttpRequestException or WebSocketException, $"actual={ex.GetType().FullName}");

        var entry = Assert.Single(logger.Entries, e => e.Message.Contains("connect-failed"));
        Assert.Contains("phase=Connecting", entry.Message);
        Assert.Contains("model=qwen-fun-asr-streaming", entry.Message);
        Assert.Contains($"endpoint=ws://127.0.0.1:{port}/", entry.Message);
        Assert.Contains("offset=777ms", entry.Message);
        Assert.Contains("elapsedMs=", entry.Message);
        Assert.DoesNotContain("test-key", entry.Message);
    }

    [Fact]
    public async Task StartTimeout_LogsStartingAndThrowsWithTaskId()
    {
        await using var server = new FakeFunAsrServer();
        server.Connection.NeverStart = true;
        var logger = new ListLogger<AliFunAsrService>();
        var service = CreateService(
            server.Url, 0, logger: logger, startTimeout: TimeSpan.FromMilliseconds(300));
        var audio = new MemoryStream(new byte[10]);

        var ex = await Assert.ThrowsAnyAsync<TimeoutException>(
            () => CollectAsync(service.TranscribeAsync(audio)));

        Assert.Contains("[Starting]", ex.Message);
        Assert.Contains("taskId=", ex.Message);
        Assert.Contains("0.3", ex.Message);

        var entry = Assert.Single(logger.Entries, e => e.Message.Contains("task-started-timeout"));
        Assert.Contains("phase=Starting", entry.Message);
        Assert.Contains("taskId=", entry.Message);
        Assert.Contains("timeoutSeconds=0.3", entry.Message);
        Assert.Contains("bytesSent=0", entry.Message);
    }

    [Fact]
    public async Task IdleTimeout_LogsFinishingPhaseAndStats()
    {
        await using var server = new FakeFunAsrServer();
        server.Connection.SilentOnFinish = true;
        var logger = new ListLogger<AliFunAsrService>();
        var service = CreateService(
            server.Url, 0, idleTimeout: TimeSpan.FromMilliseconds(400), logger: logger);
        var audio = new MemoryStream(new byte[100]);

        var ex = await Assert.ThrowsAnyAsync<TimeoutException>(
            () => CollectAsync(service.TranscribeAsync(audio)));

        Assert.Contains("[Finishing]", ex.Message);

        var entry = Assert.Single(logger.Entries, e => e.Message.Contains("idle-timeout"));
        Assert.Contains("phase=Finishing", entry.Message);
        Assert.Contains("timeoutSeconds=0.4", entry.Message);
        Assert.Contains("bytesSent=100", entry.Message);
        Assert.Contains("segmentsYielded=0", entry.Message);
    }

    [Fact]
    public async Task AbortedConnection_LogsPhaseAndRunStats()
    {
        await using var server = new FakeFunAsrServer();
        server.Connection.AbortAfterFirstBinary = true;
        var logger = new ListLogger<AliFunAsrService>();
        var service = CreateService(server.Url, 0, logger: logger);
        var audio = new MemoryStream(new byte[64000]);

        await Assert.ThrowsAnyAsync<Exception>(() => CollectAsync(service.TranscribeAsync(audio)));

        var entry = Assert.Single(
            logger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("receive-loop-failed"));
        Assert.Contains("phase=", entry.Message);
        Assert.Contains("bytesSent=", entry.Message);
        Assert.DoesNotContain("bytesSent=0 ", entry.Message);
    }
}
