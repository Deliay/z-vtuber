using System.Globalization;
using Microsoft.Extensions.Options;
using Z.Vtuber.Training.Configuration;
using Z.Vtuber.Training.Synthesis;

namespace Z.Vtuber.Training.Clients;

/// <summary>
/// index-tts-api 客户端（typed client，BaseAddress = <c>Tts:BaseUrl</c>）：
/// <c>multipart/form-data POST /synthesize</c>，字段 text/lang/speaker_prompt[+emo_prompt/emo_vector/emo_text/emo_alpha]
/// 及固定参数 duration_factor/text_normalization/max_text_tokens_per_segment/interval_silence/use_emo_text=false/use_random=false。
/// 超时（<c>Tts:RequestTimeoutSeconds</c>，默认 120s）/非 2xx → <see cref="TtsUnavailableException"/>；绝不记录音频内容。
/// </summary>
public sealed class IndexTtsClient : ITtsClient
{
    /// <summary>named client 名称（DI 注册键）。</summary>
    public const string ClientName = "TtsService";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly TtsOptions _options;

    public IndexTtsClient(IHttpClientFactory httpClientFactory, IOptions<TtsOptions> options)
    {
        _httpClientFactory = httpClientFactory;
        _options = options.Value;
    }

    public async Task<byte[]> SynthesizeAsync(TtsSynthesizeRequest request, CancellationToken ct = default)
    {
        // 经 IHttpClientFactory 每次取用，保持 handler 轮换（长运行 DNS/连接漂移），避免单例捕获 HttpClient。
        var httpClient = _httpClientFactory.CreateClient(ClientName);
        httpClient.Timeout = TimeSpan.FromSeconds(Math.Max(1, _options.RequestTimeoutSeconds));

        using var content = BuildForm(request);

        HttpResponseMessage response;
        try
        {
            response = await httpClient.PostAsync("/synthesize", content, ct);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TtsUnavailableException("tts request timed out");
        }
        catch (HttpRequestException ex)
        {
            throw new TtsUnavailableException($"tts request failed: {ex.Message}", ex);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                throw new TtsUnavailableException($"tts service returned {(int)response.StatusCode}");
            }

            byte[] bytes;
            try
            {
                bytes = await response.Content.ReadAsByteArrayAsync(ct);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TtsUnavailableException("tts response read timed out");
            }
            catch (HttpRequestException ex)
            {
                throw new TtsUnavailableException($"tts response read failed: {ex.Message}", ex);
            }

            if (bytes.Length == 0)
            {
                throw new TtsUnavailableException("tts returned empty audio");
            }

            return bytes;
        }
    }

    internal MultipartFormDataContent BuildForm(TtsSynthesizeRequest request)
    {
        var content = new MultipartFormDataContent();
        content.Add(new StringContent(request.Text), "text");
        content.Add(new StringContent(request.Lang), "lang");
        content.Add(BuildFile(request.Speaker.Bytes, request.Speaker.ContentType), "speaker_prompt", "speaker");
        content.Add(new StringContent(_options.DurationFactor.ToString("0.#####", CultureInfo.InvariantCulture)), "duration_factor");
        content.Add(new StringContent(_options.TextNormalization ? "true" : "false"), "text_normalization");
        content.Add(new StringContent(_options.MaxTextTokensPerSegment.ToString(CultureInfo.InvariantCulture)), "max_text_tokens_per_segment");
        content.Add(new StringContent(_options.IntervalSilence.ToString(CultureInfo.InvariantCulture)), "interval_silence");
        content.Add(new StringContent("false"), "use_emo_text");
        content.Add(new StringContent("false"), "use_random");

        if (request.Emotion is { } emotion)
        {
            content.Add(BuildFile(emotion.Bytes, emotion.ContentType), "emo_prompt", "emotion");
            content.Add(
                new StringContent(EmotionVector.Format(EmotionVector.FromEmotion(emotion.Emotion))), "emo_vector");
            content.Add(
                new StringContent(EmotionVector.NormalizeEmoAlpha(emotion.Emotion, _options.DefaultEmoAlpha)
                    .ToString("0.#####", CultureInfo.InvariantCulture)), "emo_alpha");
            content.Add(new StringContent(emotion.Emotion.EmotionDescription), "emo_text");
        }

        return content;
    }

    private static ByteArrayContent BuildFile(byte[] bytes, string contentType)
    {
        var file = new ByteArrayContent(bytes);
        if (!string.IsNullOrWhiteSpace(contentType))
        {
            file.Headers.TryAddWithoutValidation("Content-Type", contentType);
        }

        return file;
    }
}
