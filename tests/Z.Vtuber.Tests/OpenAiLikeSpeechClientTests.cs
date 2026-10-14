using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Z.Vtuber.Common.Persistence.Entities;
using Z.Vtuber.Training.Ai;
using Z.Vtuber.Training.Configuration;

namespace Z.Vtuber.Tests;

/// <summary>
/// OpenAI-like 语音客户端契约：<c>chat/completions</c> 请求体（model/modalities/audio.format/
/// messages input_audio+text）、鉴权头、base64 <c>audio.data</c> 解析；缺音频段 → <see cref="ModelOutputException"/>；
/// 非 2xx/超时 → <see cref="SpeechModelUnavailableException"/>；工厂派发与 Claude-like 不支持。
/// </summary>
public class OpenAiLikeSpeechClientTests
{
    private sealed class StubHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, HttpResponseMessage>? Handler { get; set; }
        public List<string> Paths { get; } = [];
        public List<string?> Authorizations { get; } = [];
        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Paths.Add(request.RequestUri!.PathAndQuery);
            Authorizations.Add(request.Headers.Authorization?.ToString());
            if (request.Content is not null)
            {
                Bodies.Add(await request.Content.ReadAsStringAsync(cancellationToken));
            }

            return Handler?.Invoke(request) ?? new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    private static OpenAiLikeSpeechClient Create(
        StubHandler handler, Action<AiOptions>? configure = null, Action<SpeechOptions>? configureSpeech = null)
    {
        var options = new AiOptions();
        configure?.Invoke(options);
        var speechOptions = new SpeechOptions();
        configureSpeech?.Invoke(speechOptions);
        var client = new HttpClient(handler) { BaseAddress = new Uri("http://llm.test") };
        return new OpenAiLikeSpeechClient(
            client, options, speechOptions, NullLogger<OpenAiLikeSpeechClient>.Instance);
    }

    private static TrainingProviderRecord Provider => new()
    {
        Name = "p", Type = "openai", Endpoint = "http://llm.test", ApiKey = "secret",
    };

    private static TrainingModelRecord Model => new() { Model = "gpt-audio" };

    private static SpeechSynthesizeRequest Request(
        string contentType = "audio/mpeg", string? transcript = "参考内容", string? emotion = null, string lang = "EN") =>
        new([9, 8, 7], contentType, transcript, "目标文本", lang, emotion);

    private static HttpResponseMessage AudioOk(byte[] bytes)
    {
        var b64 = Convert.ToBase64String(bytes);
        var json = "{\"choices\":[{\"message\":{\"audio\":{\"data\":\"" + b64 + "\"}}}]}";
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
    }

    [Fact]
    public async Task Synthesize_BuildsChatCompletionsRequest_AndParsesAudio()
    {
        var handler = new StubHandler { Handler = _ => AudioOk([1, 2, 3, 4]) };
        var client = Create(handler);

        var bytes = await client.SynthesizeSpeechAsync(Provider, Model, Request());

        Assert.Equal([1, 2, 3, 4], bytes);
        Assert.Equal("/chat/completions", handler.Paths[0]);
        Assert.Equal("Bearer secret", handler.Authorizations[0]);

        using var document = JsonDocument.Parse(handler.Bodies[0]);
        var root = document.RootElement;
        Assert.Equal("gpt-audio", root.GetProperty("model").GetString());
        Assert.Equal(["text", "audio"], root.GetProperty("modalities").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal("wav", root.GetProperty("audio").GetProperty("format").GetString());

        var content = root.GetProperty("messages")[0].GetProperty("content");
        Assert.Equal("input_audio", content[0].GetProperty("type").GetString());
        Assert.Equal(
            Convert.ToBase64String([9, 8, 7]),
            content[0].GetProperty("input_audio").GetProperty("data").GetString());
        Assert.Equal("mp3", content[0].GetProperty("input_audio").GetProperty("format").GetString());
        Assert.Equal("text", content[1].GetProperty("type").GetString());
        Assert.Contains("待朗读的文本", content[1].GetProperty("text").GetString());
    }

    [Theory]
    [InlineData("audio/mpeg", "mp3")]
    [InlineData("audio/mpeg; codecs=x", "mp3")]
    [InlineData("audio/wav", "wav")]
    [InlineData("audio/x-wav", "wav")]
    [InlineData("audio/wave", "wav")]
    [InlineData("application/octet-stream", "mp3")]
    [InlineData(null, "mp3")]
    public void MapAudioFormat_MapsKnownContentTypes(string? contentType, string expected)
    {
        Assert.Equal(expected, OpenAiLikeSpeechClient.MapAudioFormat(contentType));
    }

    [Fact]
    public async Task BuildBody_LanguageHintUsesConfiguredDefaultLang()
    {
        var handler = new StubHandler { Handler = _ => AudioOk([1]) };
        // 配置默认语种 EN：EN 请求不附语种提示，ZH 请求附「中文」提示。
        var client = Create(handler, configureSpeech: o => o.DefaultLang = "EN");

        await client.SynthesizeSpeechAsync(Provider, Model, Request(lang: "EN"));

        using (var document = JsonDocument.Parse(handler.Bodies[0]))
        {
            var text = document.RootElement.GetProperty("messages")[0].GetProperty("content")[1]
                .GetProperty("text").GetString();
            Assert.DoesNotContain("请使用英文朗读。", text);
        }

        handler.Bodies.Clear();
        await client.SynthesizeSpeechAsync(Provider, Model, Request(lang: "ZH"));

        using (var document = JsonDocument.Parse(handler.Bodies[0]))
        {
            var text = document.RootElement.GetProperty("messages")[0].GetProperty("content")[1]
                .GetProperty("text").GetString();
            Assert.Contains("请使用中文朗读。", text);
        }
    }

    [Fact]
    public async Task ResponseWithoutAudio_ThrowsModelOutput()
    {
        var handler = new StubHandler
        {
            Handler = _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"choices":[{"message":{"content":"hi"}}]}""", Encoding.UTF8, "application/json"),
            },
        };
        var client = Create(handler);

        await Assert.ThrowsAsync<ModelOutputException>(() => client.SynthesizeSpeechAsync(Provider, Model, Request()));
    }

    [Fact]
    public async Task NonSuccess_ThrowsSpeechModelUnavailable()
    {
        var handler = new StubHandler
        {
            Handler = _ => new HttpResponseMessage(HttpStatusCode.BadGateway)
            {
                Content = new StringContent("upstream", Encoding.UTF8, "text/plain"),
            },
        };
        var client = Create(handler);

        await Assert.ThrowsAsync<SpeechModelUnavailableException>(
            () => client.SynthesizeSpeechAsync(Provider, Model, Request()));
    }

    [Fact]
    public async Task Timeout_ThrowsSpeechModelUnavailable()
    {
        var handler = new StubHandler { Handler = _ => throw new TaskCanceledException("timeout") };
        var client = Create(handler);

        await Assert.ThrowsAsync<SpeechModelUnavailableException>(
            () => client.SynthesizeSpeechAsync(Provider, Model, Request()));
    }

    [Fact]
    public void Factory_OpenAi_ReturnsSpeechClient()
    {
        var client = new HttpClient(new StubHandler()) { BaseAddress = new Uri("http://llm.test") };
        var factory = new SpeechModelClientFactory(
            new StubHttpClientFactory(client), Options.Create(new AiOptions()),
            Options.Create(new SpeechOptions()), NullLoggerFactory.Instance);

        Assert.IsType<OpenAiLikeSpeechClient>(factory.Create("openai"));
        Assert.IsType<OpenAiLikeSpeechClient>(factory.Create("OpenAI"));
    }

    [Theory]
    [InlineData("claude")]
    [InlineData("unknown")]
    public void Factory_NonAudioProvider_ThrowsNotSupported(string providerType)
    {
        var client = new HttpClient(new StubHandler());
        var factory = new SpeechModelClientFactory(
            new StubHttpClientFactory(client), Options.Create(new AiOptions()),
            Options.Create(new SpeechOptions()), NullLoggerFactory.Instance);

        Assert.Throws<ModelAudioOutputNotSupportedException>(() => factory.Create(providerType));
    }
}
