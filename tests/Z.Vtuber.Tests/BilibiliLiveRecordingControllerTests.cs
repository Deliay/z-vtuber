using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using MongoDB.Driver;
using Z.Vtuber.Common.Persistence;
using Z.Vtuber.Common.Persistence.Entities;
using Z.Vtuber.Common.Services;
using Z.Vtuber.Controllers;

namespace Z.Vtuber.Tests;

public class BilibiliLiveRecordingControllerTests(MongoDbFixture fixture) : IClassFixture<MongoDbFixture>, IAsyncLifetime
{
    public Task InitializeAsync() => fixture.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private sealed class StubHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, HttpResponseMessage>? Handler { get; set; }
        public List<string> Requests { get; } = [];
        public List<string?> RangeHeaders { get; } = [];
        public List<string?> IfRangeHeaders { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!.PathAndQuery);
            RangeHeaders.Add(GetHeader(request, "Range"));
            IfRangeHeaders.Add(GetHeader(request, "If-Range"));
            return Task.FromResult(Handler?.Invoke(request) ?? new HttpResponseMessage(HttpStatusCode.OK));
        }

        private static string? GetHeader(HttpRequestMessage request, string name) =>
            request.Headers.TryGetValues(name, out var values) ? string.Join(",", values) : null;
    }

    private sealed class StubHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private static BilibiliLiveRecordingController CreateController(
        IMongoDatabase database,
        HttpClient client)
    {
        client.BaseAddress ??= new Uri("http://recording");
        var controller = new BilibiliLiveRecordingController(
            new SubscriptionService(database),
            new AccountBasicInfoRepository(database),
            new RecordingStatusRepository(database),
            new StubHttpClientFactory(client));
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
        return controller;
    }

    private async Task SeedSubscriptionAsync(long mid, long roomId)
    {
        var subscription = new SubscriptionService(fixture.Database);
        await subscription.SubscribeAsync(mid, SubscriptionType.Recording);
        await new AccountBasicInfoRepository(fixture.Database).UpsertAsync(new AccountBasicInfoRecord
        {
            Mid = mid,
            RoomId = roomId,
            Data = """{"uName":"某主播"}""",
            FetchedAt = DateTime.UtcNow,
        });
    }

    [Fact]
    public async Task RecordingStatus_AggregatesSubscriptionAccountAndState()
    {
        await SeedSubscriptionAsync(1001, 900001);
        await new RecordingStatusRepository(fixture.Database).UpsertAsync(new RecordingStatusRecord
        {
            RoomId = 900001,
            State = "recording",
            HostName = "host",
            SessionTitle = "晚间歌回",
            Files = ["[RAW][900001][某主播] 2026-09-13-晚间歌回.flv"],
            UpdatedAt = DateTime.UtcNow,
        });

        var controller = CreateController(fixture.Database, new HttpClient(new StubHandler()));
        var result = await controller.ListRecordingStatus(CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var items = (System.Collections.IEnumerable)ok.Value!.GetType().GetProperty("items")!.GetValue(ok.Value)!;
        var item = Assert.Single(items.Cast<object>());
        Assert.Equal("recording", item.GetType().GetProperty("state")!.GetValue(item));
        Assert.Equal(900001L, item.GetType().GetProperty("roomId")!.GetValue(item));
    }

    [Fact]
    public async Task RecordingStatus_NoState_ReturnsUnknown()
    {
        await SeedSubscriptionAsync(1002, 900002);

        var controller = CreateController(fixture.Database, new HttpClient(new StubHandler()));
        var result = await controller.ListRecordingStatus(CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var items = (System.Collections.IEnumerable)ok.Value!.GetType().GetProperty("items")!.GetValue(ok.Value)!;
        var item = Assert.Single(items.Cast<object>());
        Assert.Equal("unknown", item.GetType().GetProperty("state")!.GetValue(item));
    }

    [Fact]
    public async Task RecordingStatus_StaleRecording_ReturnsUnknown()
    {
        await SeedSubscriptionAsync(1003, 900003);
        await new RecordingStatusRepository(fixture.Database).UpsertAsync(new RecordingStatusRecord
        {
            RoomId = 900003,
            State = "recording",
            HostName = "host",
            SessionTitle = "旧场次",
            UpdatedAt = DateTime.UtcNow - TimeSpan.FromMinutes(10),
        });

        var controller = CreateController(fixture.Database, new HttpClient(new StubHandler()));
        var result = await controller.ListRecordingStatus(CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var items = (System.Collections.IEnumerable)ok.Value!.GetType().GetProperty("items")!.GetValue(ok.Value)!;
        var item = Assert.Single(items.Cast<object>());
        Assert.Equal("unknown", item.GetType().GetProperty("state")!.GetValue(item));
    }

    [Fact]
    public async Task ListRecordings_ProxiesRecordingService()
    {
        var handler = new StubHandler
        {
            Handler = _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"files":["a.flv"]}""", Encoding.UTF8, "application/json"),
            },
        };
        var controller = CreateController(fixture.Database, new HttpClient(handler));

        var result = await controller.ListRecordings(900003, CancellationToken.None);

        var content = Assert.IsType<ContentResult>(result);
        Assert.Contains("a.flv", content.Content);
        Assert.Contains("/api/900003/recordings", handler.Requests[0]);
    }

    [Fact]
    public async Task ListRecordings_RecordingUnreachable_Returns502()
    {
        var handler = new StubHandler
        {
            Handler = _ => throw new HttpRequestException("boom"),
        };
        var controller = CreateController(fixture.Database, new HttpClient(handler));

        var result = await controller.ListRecordings(900004, CancellationToken.None);

        var status = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status502BadGateway, status.StatusCode);
    }

    [Fact]
    public async Task DownloadRecording_StreamsContent()
    {
        var payload = new byte[] { 1, 2, 3, 4, 5 };
        var handler = new StubHandler
        {
            Handler = _ =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(payload),
                };
                response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                response.Content.Headers.ContentDisposition = new ContentDispositionHeaderValue("attachment")
                {
                    FileName = "a.flv",
                };
                return response;
            },
        };
        var controller = CreateController(fixture.Database, new HttpClient(handler));
        controller.HttpContext.Response.Body = new MemoryStream();

        var result = await controller.DownloadRecording(900005, "a.flv", CancellationToken.None);

        Assert.IsType<EmptyResult>(result);
        controller.Response.Body.Position = 0;
        var buffer = new byte[payload.Length];
        var read = await controller.Response.Body.ReadAsync(buffer);
        Assert.Equal(payload.Length, read);
        Assert.Equal(payload, buffer);
    }

    [Fact]
    public async Task DownloadRecording_NotFound_Passthrough()
    {
        var handler = new StubHandler { Handler = _ => new HttpResponseMessage(HttpStatusCode.NotFound) };
        var controller = CreateController(fixture.Database, new HttpClient(handler));

        var result = await controller.DownloadRecording(900006, "missing.flv", CancellationToken.None);

        Assert.IsType<NotFoundResult>(result);
    }

    [Theory]
    [InlineData("""{"error":"file is still recording and cannot be downloaded"}""")]
    [InlineData("""{"error":"invalid fileName"}""")]
    public async Task DownloadRecording_UpstreamBadRequest_Passthrough(string body)
    {
        var handler = new StubHandler
        {
            Handler = _ => new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            },
        };
        var controller = CreateController(fixture.Database, new HttpClient(handler));

        var result = await controller.DownloadRecording(900010, "a.flv", CancellationToken.None);

        var content = Assert.IsType<ContentResult>(result);
        Assert.Equal(StatusCodes.Status400BadRequest, content.StatusCode);
        Assert.Equal(body, content.Content);
        Assert.Equal("application/json", content.ContentType);
    }

    [Fact]
    public async Task DownloadRecording_UpstreamServerError_Returns502()
    {
        var handler = new StubHandler { Handler = _ => new HttpResponseMessage(HttpStatusCode.InternalServerError) };
        var controller = CreateController(fixture.Database, new HttpClient(handler));

        var result = await controller.DownloadRecording(900011, "a.flv", CancellationToken.None);

        var status = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status502BadGateway, status.StatusCode);
    }

    [Fact]
    public async Task DownloadRecording_ForwardsRangeAndIfRangeHeadersToUpstream()
    {
        var handler = new StubHandler
        {
            Handler = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([]) },
        };
        var controller = CreateController(fixture.Database, new HttpClient(handler));
        controller.HttpContext.Request.Headers.Range = "bytes=0-99";
        controller.HttpContext.Request.Headers.IfRange = "\"etag-value\"";
        controller.HttpContext.Response.Body = new MemoryStream();

        var result = await controller.DownloadRecording(900020, "a.flv", CancellationToken.None);

        Assert.IsType<EmptyResult>(result);
        Assert.Equal("bytes=0-99", handler.RangeHeaders[0]);
        Assert.Equal("\"etag-value\"", handler.IfRangeHeaders[0]);
    }

    [Fact]
    public async Task DownloadRecording_WithoutRange_DoesNotForwardRangeHeader()
    {
        var handler = new StubHandler
        {
            Handler = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([]) },
        };
        var controller = CreateController(fixture.Database, new HttpClient(handler));
        controller.HttpContext.Response.Body = new MemoryStream();

        var result = await controller.DownloadRecording(900021, "a.flv", CancellationToken.None);

        Assert.IsType<EmptyResult>(result);
        Assert.Null(handler.RangeHeaders[0]);
        Assert.Null(handler.IfRangeHeaders[0]);
    }

    [Fact]
    public async Task DownloadRecording_Upstream206_PassthroughPartialContent()
    {
        var fragment = new byte[] { 1, 2, 3, 4, 5 };
        var handler = new StubHandler
        {
            Handler = _ =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.PartialContent)
                {
                    Content = new ByteArrayContent(fragment),
                };
                response.Headers.AcceptRanges.Add("bytes");
                response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                response.Content.Headers.ContentRange = new ContentRangeHeaderValue(0, 4, 200);
                return response;
            },
        };
        var controller = CreateController(fixture.Database, new HttpClient(handler));
        controller.HttpContext.Response.Body = new MemoryStream();

        var result = await controller.DownloadRecording(900022, "a.flv", CancellationToken.None);

        Assert.IsType<EmptyResult>(result);
        Assert.Equal(StatusCodes.Status206PartialContent, controller.Response.StatusCode);
        Assert.Equal("bytes 0-4/200", controller.Response.Headers.ContentRange.ToString());
        Assert.Equal("bytes", controller.Response.Headers.AcceptRanges.ToString());
        Assert.Equal(fragment.Length, controller.Response.ContentLength);
        Assert.Equal(fragment, await ReadBodyAsync(controller.HttpContext));
    }

    [Fact]
    public async Task DownloadRecording_Upstream200_PassthroughAcceptRanges()
    {
        var payload = new byte[] { 7, 7, 7 };
        var handler = new StubHandler
        {
            Handler = _ =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(payload),
                };
                response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                response.Headers.AcceptRanges.Add("bytes");
                return response;
            },
        };
        var controller = CreateController(fixture.Database, new HttpClient(handler));
        controller.HttpContext.Response.Body = new MemoryStream();

        var result = await controller.DownloadRecording(900023, "a.flv", CancellationToken.None);

        Assert.IsType<EmptyResult>(result);
        Assert.Equal(StatusCodes.Status200OK, controller.Response.StatusCode);
        Assert.Equal("bytes", controller.Response.Headers.AcceptRanges.ToString());
        Assert.Equal(payload, await ReadBodyAsync(controller.HttpContext));
    }

    [Fact]
    public async Task DownloadRecording_Upstream416_PassthroughWithout502()
    {
        var handler = new StubHandler
        {
            Handler = _ =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.RequestedRangeNotSatisfiable)
                {
                    Content = new ByteArrayContent([]),
                };
                response.Content.Headers.ContentRange = new ContentRangeHeaderValue(200);
                return response;
            },
        };
        var controller = CreateController(fixture.Database, new HttpClient(handler));
        controller.HttpContext.Response.Body = new MemoryStream();

        var result = await controller.DownloadRecording(900024, "a.flv", CancellationToken.None);

        Assert.IsType<EmptyResult>(result);
        Assert.Equal(StatusCodes.Status416RangeNotSatisfiable, controller.Response.StatusCode);
        Assert.Equal("bytes */200", controller.Response.Headers.ContentRange.ToString());
    }

    private static async Task<byte[]> ReadBodyAsync(HttpContext context)
    {
        context.Response.Body.Position = 0;
        using var buffer = new MemoryStream();
        await context.Response.Body.CopyToAsync(buffer);
        return buffer.ToArray();
    }

    [Fact]
    public async Task ProxyAudio_NotFound_Passthrough()
    {
        var handler = new StubHandler { Handler = _ => new HttpResponseMessage(HttpStatusCode.NotFound) };
        var controller = CreateController(fixture.Database, new HttpClient(handler));

        var result = await controller.ProxyAudio(900007, CancellationToken.None);

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task ProxyAudio_Recording_StreamsAndDisablesCache()
    {
        var payload = new byte[] { 9, 8, 7 };
        var handler = new StubHandler
        {
            Handler = _ =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) };
                response.Content.Headers.ContentType = new MediaTypeHeaderValue("video/x-flv");
                return response;
            },
        };
        var controller = CreateController(fixture.Database, new HttpClient(handler));
        controller.HttpContext.Response.Body = new MemoryStream();

        var result = await controller.ProxyAudio(900008, CancellationToken.None);

        Assert.IsType<EmptyResult>(result);
        Assert.Equal("video/x-flv", controller.Response.ContentType);
        Assert.Equal("no-store", controller.Response.Headers.CacheControl.ToString());
    }
}
