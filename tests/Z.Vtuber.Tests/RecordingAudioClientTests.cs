using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Z.Vtuber.Training.Ai;
using Z.Vtuber.Training.Clients;
using Z.Vtuber.Training.Configuration;

namespace Z.Vtuber.Tests;

/// <summary>
/// Plan 1.7：<see cref="RecordingAudioClient"/> 直测（stub HTTP handler）——
/// 路径/区间拼装、<c>endMs</c> 空兜底（偏差 1）、失败映射与体积上限、非法 uri。
/// </summary>
public class RecordingAudioClientTests
{
    private sealed class RecordingAudioStubHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, HttpResponseMessage>? Handler { get; set; }
        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            var response = Handler?.Invoke(request)
                ?? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) };
            return Task.FromResult(response);
        }
    }

    private static RecordingAudioClient CreateClient(
        RecordingAudioStubHandler handler, AiOptions? aiOptions = null)
    {
        const string baseAddress = "http://recording";
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri(baseAddress) };
        return new RecordingAudioClient(
            httpClient,
            Options.Create(new RecordingServiceOptions { BaseUrl = baseAddress }),
            Options.Create(aiOptions ?? new AiOptions()),
            NullLogger<RecordingAudioClient>.Instance);
    }

    [Fact]
    public async Task GetClip_ValidUri_BuildsAudioPathWithRange()
    {
        var handler = new RecordingAudioStubHandler
        {
            Handler = _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent([1, 2, 3, 4]),
            },
        };
        var client = CreateClient(handler);

        var clip = await client.GetClipAsync("b/live/123/recordings/a.flv", 1000, 2000);

        var query = Assert.Single(handler.Requests).PathAndQuery;
        Assert.Contains("/api/123/recordings/a.flv/audio", query);
        Assert.Contains("startMs=1000", query);
        Assert.Contains("endMs=2000", query);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, clip.Bytes);
        Assert.Equal("audio/mpeg", clip.ContentType);
    }

    [Fact]
    public async Task GetClip_EmptyEndMs_FallsBackToDefaultDuration()
    {
        var handler = new RecordingAudioStubHandler();
        var client = CreateClient(handler, new AiOptions { AudioClipDefaultDurationMs = 300_000 });

        await client.GetClipAsync("b/live/9/recordings/x.flv", 1000, null);
        await client.GetClipAsync("b/live/9/recordings/x.flv", 0, null);

        Assert.Contains("endMs=301000", handler.Requests[0].PathAndQuery);
        Assert.Contains("endMs=300000", handler.Requests[1].PathAndQuery);
    }

    [Fact]
    public async Task GetClip_ExplicitEndMs_IsPreservedUnchanged()
    {
        var handler = new RecordingAudioStubHandler();
        var client = CreateClient(handler, new AiOptions { AudioClipDefaultDurationMs = 300_000 });

        await client.GetClipAsync("b/live/9/recordings/x.flv", 500, 900);

        Assert.Contains("endMs=900", handler.Requests[0].PathAndQuery);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    public async Task GetClip_NonSuccess_ThrowsAudioUnavailable(HttpStatusCode status)
    {
        var handler = new RecordingAudioStubHandler { Handler = _ => new HttpResponseMessage(status) };
        var client = CreateClient(handler);

        await Assert.ThrowsAsync<AudioUnavailableException>(
            () => client.GetClipAsync("b/live/1/recordings/a.flv", 0, 1000));
    }

    [Fact]
    public async Task GetClip_TransportFailure_ThrowsAudioUnavailable()
    {
        var handler = new RecordingAudioStubHandler
        {
            Handler = _ => throw new HttpRequestException("connection refused"),
        };
        var client = CreateClient(handler);

        await Assert.ThrowsAsync<AudioUnavailableException>(
            () => client.GetClipAsync("b/live/1/recordings/a.flv", 0, 1000));
    }

    [Fact]
    public async Task GetClip_BodyOverMaxBytes_ThrowsAudioTooLarge()
    {
        var handler = new RecordingAudioStubHandler
        {
            Handler = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[10]) },
        };
        var client = CreateClient(handler, new AiOptions { MaxAudioBytes = 4 });

        await Assert.ThrowsAsync<AudioTooLargeException>(
            () => client.GetClipAsync("b/live/1/recordings/a.flv", 0, 1000));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-uri")]
    [InlineData("b/live/abc/recordings/a.flv")]
    [InlineData("b/live/1/recordings/a/b.flv")]
    public async Task GetClip_UnparseableUri_ThrowsArgument(string audioUri)
    {
        var handler = new RecordingAudioStubHandler();
        var client = CreateClient(handler);

        await Assert.ThrowsAsync<ArgumentException>(() => client.GetClipAsync(audioUri, 0, 1000));
    }
}
