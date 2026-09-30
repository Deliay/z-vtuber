using System.Net;
using System.Text;
using Microsoft.Extensions.Options;
using Z.Vtuber.Training.Clients;
using Z.Vtuber.Training.Configuration;
using Z.Vtuber.Training.Contracts;

namespace Z.Vtuber.Tests;

/// <summary>
/// 上游 index-tts-api 客户端契约：multipart 字段 text/lang/speaker_prompt（必带）/emo_prompt/emo_vector/
/// emo_text/emo_alpha 与固定参数；无情绪时省略 emo_*；超时/非 2xx/空响应 → <see cref="TtsUnavailableException"/>。
/// </summary>
public class IndexTtsClientTests
{
    private sealed class StubHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, HttpResponseMessage>? Handler { get; set; }
        public List<string> Paths { get; } = [];
        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Paths.Add(request.RequestUri!.PathAndQuery);
            if (request.Content is not null)
            {
                Bodies.Add(await request.Content.ReadAsStringAsync(cancellationToken));
            }

            return Handler?.Invoke(request) ?? new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    private static IndexTtsClient Create(StubHandler handler, Action<TtsOptions>? configure = null)
    {
        var options = new TtsOptions();
        configure?.Invoke(options);
        var client = new HttpClient(handler) { BaseAddress = new Uri("http://tts.test") };
        return new IndexTtsClient(client, Options.Create(options));
    }

    private static HttpResponseMessage Wav() =>
        new(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3, 4]) };

    private static TtsSynthesizeRequest SpeakerOnly() =>
        new("你好", "ZH", new TtsSpeakerReference([9, 9], "audio/mpeg"), null);

    private static TtsSynthesizeRequest WithEmotion() =>
        new(
            "你好",
            "ZH",
            new TtsSpeakerReference([9, 9], "audio/mpeg"),
            new TtsEmotionReference([8, 8], "audio/mpeg",
                new EmotionResult(5, "开心", 0, 0, 0.8, 0, 0, 0, 0, 0)));

    [Fact]
    public async Task SpeakerOnly_BuildsRequiredFieldsAndOmitsEmotion()
    {
        var handler = new StubHandler { Handler = _ => Wav() };
        var client = Create(handler);

        var bytes = await client.SynthesizeAsync(SpeakerOnly());

        Assert.Equal([1, 2, 3, 4], bytes);
        Assert.Equal("/synthesize", handler.Paths[0]);
        var body = handler.Bodies[0];
        Assert.Contains("name=text", body);
        Assert.Contains("name=lang", body);
        Assert.Contains("name=speaker_prompt", body);
        Assert.Contains("name=use_emo_text", body);
        Assert.Contains("false", body);
        Assert.Contains("name=duration_factor", body);
        Assert.Contains("name=text_normalization", body);
        Assert.Contains("name=max_text_tokens_per_segment", body);
        Assert.Contains("name=interval_silence", body);
        Assert.DoesNotContain("name=emo_prompt", body);
        Assert.DoesNotContain("name=emo_vector", body);
        Assert.DoesNotContain("name=emo_alpha", body);
        Assert.DoesNotContain("name=emo_text", body);
    }

    [Fact]
    public async Task WithEmotion_BuildsEmotionFields()
    {
        var handler = new StubHandler { Handler = _ => Wav() };
        var client = Create(handler);

        await client.SynthesizeAsync(WithEmotion());

        var body = handler.Bodies[0];
        Assert.Contains("name=emo_prompt", body);
        Assert.Contains("name=emo_vector", body);
        Assert.Contains("0,0,0.8,0,0,0,0,0", body);
        Assert.Contains("name=emo_alpha", body);
        Assert.Contains("0.5", body);
        Assert.Contains("name=emo_text", body);
        Assert.Contains("开心", body);
    }

    [Fact]
    public async Task ConfiguredDefaults_AreApplied()
    {
        var handler = new StubHandler { Handler = _ => Wav() };
        var client = Create(handler, o =>
        {
            o.DurationFactor = 1.2;
            o.TextNormalization = false;
            o.MaxTextTokensPerSegment = 64;
            o.IntervalSilence = 100;
        });

        await client.SynthesizeAsync(SpeakerOnly());

        var body = handler.Bodies[0];
        Assert.Contains("1.2", body);
        Assert.Contains("64", body);
        Assert.Contains("100", body);
    }

    [Fact]
    public async Task NonSuccess_ThrowsTtsUnavailable()
    {
        var handler = new StubHandler
        {
            Handler = _ => new HttpResponseMessage(HttpStatusCode.BadGateway)
            {
                Content = new StringContent("upstream", Encoding.UTF8, "text/plain"),
            },
        };
        var client = Create(handler);

        await Assert.ThrowsAsync<TtsUnavailableException>(() => client.SynthesizeAsync(SpeakerOnly()));
    }

    [Fact]
    public async Task EmptyBody_ThrowsTtsUnavailable()
    {
        var handler = new StubHandler
        {
            Handler = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([]) },
        };
        var client = Create(handler);

        await Assert.ThrowsAsync<TtsUnavailableException>(() => client.SynthesizeAsync(SpeakerOnly()));
    }

    [Fact]
    public async Task Timeout_ThrowsTtsUnavailable()
    {
        var handler = new StubHandler { Handler = _ => throw new TaskCanceledException("timeout") };
        var client = Create(handler);

        await Assert.ThrowsAsync<TtsUnavailableException>(() => client.SynthesizeAsync(SpeakerOnly()));
    }
}
