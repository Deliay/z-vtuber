using System.Net;

namespace Z.Vtuber.Tests;

/// <summary>
/// <see cref="Z.Vtuber.Training.Clients.TranscriptApiClient"/> 的传输语义：
/// 404 → null（控制器据此映 404 task not found），其余非 2xx 抛 HttpRequestException（控制器映 502）。
/// 导入链路的 404/502 分支在 TrainingImportApiTests 以桩覆盖，这里锁定真实 HTTP 客户端本身。
/// </summary>
public class TranscriptApiClientTests
{
    private sealed class StubHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, HttpResponseMessage> Handler { get; set; } =
            _ => new HttpResponseMessage(HttpStatusCode.OK);

        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!.PathAndQuery);
            return Task.FromResult(Handler(request));
        }
    }

    private static Z.Vtuber.Training.Clients.TranscriptApiClient CreateClient(StubHandler handler) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("http://transcript/") });

    [Fact]
    public async Task GetTaskDetail_ParsesTaskAndSegments_AndEscapesTaskId()
    {
        var handler = new StubHandler
        {
            Handler = _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """
                    {"task":{"id":"66d8aaaabbbbccccddddeeee","roomId":123,"file":"a.flv","status":"DONE","offsetMs":2500},
                     "segments":[{"start":1000,"end":1800,"text":"你好"}]}
                    """,
                    System.Text.Encoding.UTF8,
                    "application/json"),
            },
        };

        var detail = await CreateClient(handler).GetTaskDetailAsync("66d8 aaa/bbb");

        Assert.NotNull(detail);
        Assert.Equal("66d8aaaabbbbccccddddeeee", detail!.Task.Id);
        Assert.Equal(123, detail.Task.RoomId);
        Assert.Equal("a.flv", detail.Task.File);
        Assert.Equal("DONE", detail.Task.Status);
        Assert.Equal(2500, detail.Task.OffsetMs);
        var segment = Assert.Single(detail.Segments);
        Assert.Equal(1000, segment.Start);
        Assert.Equal(1800, segment.End);
        Assert.Equal("你好", segment.Text);
        Assert.StartsWith("/api/transcripts/task/66d8%20aaa%2Fbbb", handler.Requests[0]);
    }

    [Fact]
    public async Task GetTaskDetail_NotFound_ReturnsNull()
    {
        var handler = new StubHandler
        {
            Handler = _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        };

        Assert.Null(await CreateClient(handler).GetTaskDetailAsync("missing"));
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    public async Task GetTaskDetail_NonSuccess_ThrowsHttpRequestException(HttpStatusCode status)
    {
        var handler = new StubHandler
        {
            Handler = _ => new HttpResponseMessage(status),
        };

        // 控制器捕获该异常并返回 502 transcript service unavailable
        await Assert.ThrowsAsync<HttpRequestException>(
            () => CreateClient(handler).GetTaskDetailAsync("66d8aaaabbbbccccddddeeee"));
    }
}
