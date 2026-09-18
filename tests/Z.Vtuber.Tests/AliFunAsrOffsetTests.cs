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
        IReadOnlyList<string>? context = null,
        TimeSpan? startTimeout = null) =>
        new(new Uri(url), "test-key", offset, "qwen-fun-asr-streaming", context ?? [],
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

    /// <summary>测试访问器：FunAsrProtocol 为 internal，测试程序集通过 InternalsVisibleTo 可见。</summary>
    private static class FunAsrProtocolAccessor
    {
        public static bool TryParseAction(string json, out string? action) =>
            FunAsrProtocol.TryParseAction(json, out action);
    }
}
