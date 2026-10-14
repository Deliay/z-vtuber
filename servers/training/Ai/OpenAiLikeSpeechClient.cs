using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Z.Vtuber.Common.Persistence.Entities;
using Z.Vtuber.Training.Configuration;
using Z.Vtuber.Training.Synthesis;

namespace Z.Vtuber.Training.Ai;

/// <summary>
/// OpenAI-like 语音客户端：<c>POST {endpoint}/chat/completions</c>（Bearer 鉴权），
/// body 携带 <c>modalities:["text","audio"]</c> 与 <c>audio:{format:"wav", voice:"data:&lt;mime&gt;;base64,&lt;参考语音&gt;"}</c>，
/// messages 两段：user 单段文本（<c>SpeechPromptBuilder</c> 组装 prompt）+ assistant 目标文本，
/// 解析响应 <c>choices[0].message.audio.data</c> base64 → WAV 字节。
/// 非 2xx/超时/传输失败 → <see cref="SpeechModelUnavailableException"/>（映射 502 model service unavailable）；
/// 缺音频段/空/非法 base64 → <see cref="ModelOutputException"/>（映射 502 invalid model output）。
/// 超时经 <c>Ai:RequestTimeoutSeconds</c> 生效（复用 named client <c>AiModel</c>）。
/// 日志绝不打 apiKey、文本与音频内容。
/// </summary>
public sealed class OpenAiLikeSpeechClient : ISpeechModelClient
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<OpenAiLikeSpeechClient> _logger;
    private readonly string _defaultLang;

    public OpenAiLikeSpeechClient(
        HttpClient httpClient, AiOptions options, SpeechOptions speechOptions,
        ILogger<OpenAiLikeSpeechClient> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
        _defaultLang = speechOptions.DefaultLang;
        // 超时经 HttpClient.Timeout 生效（超时抛 TaskCanceledException，按未取消客户端映射 502）。
        _httpClient.Timeout = TimeSpan.FromSeconds(Math.Max(1, options.RequestTimeoutSeconds));
    }

    public async Task<byte[]> SynthesizeSpeechAsync(
        TrainingProviderRecord provider, TrainingModelRecord model,
        SpeechSynthesizeRequest request, CancellationToken ct = default)
    {
        var url = $"{provider.Endpoint.TrimEnd('/')}/chat/completions";

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, url);
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", provider.ApiKey);
        httpRequest.Content = new StringContent(BuildBody(model.Model, request), Encoding.UTF8, "application/json");

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(httpRequest, ct);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new SpeechModelUnavailableException("speech model request timed out");
        }
        catch (HttpRequestException ex)
        {
            throw new SpeechModelUnavailableException($"speech model request failed: {ex.Message}", ex);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "OpenAI-like speech endpoint returned {Status} for model {Model}",
                    (int)response.StatusCode, model.Model);
                throw new SpeechModelUnavailableException($"model service returned {(int)response.StatusCode}");
            }

            string json;
            try
            {
                json = await response.Content.ReadAsStringAsync(ct);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new SpeechModelUnavailableException("speech model response read timed out");
            }
            catch (HttpRequestException ex)
            {
                throw new SpeechModelUnavailableException($"speech model response read failed: {ex.Message}", ex);
            }

            return ParseAudio(json);
        }
    }

    /// <summary>
    /// 组装 <c>/chat/completions</c> 请求体：<c>modalities=["text","audio"]</c>、<c>audio.format="wav"</c>、
    /// <c>audio.voice</c> 携带参考语音 data URI（<c>ReferenceContentType</c> 映射的 format + base64）、
    /// messages 两段（user 单段文本 prompt 在前、assistant 目标文本在后）。
    /// prompt 的语种提示以配置的 <c>Speech:DefaultLang</c> 为默认基准（非默认语种才附加朗读语言要求）。
    /// </summary>
    internal string BuildBody(string model, SpeechSynthesizeRequest request)
    {
        var mineType = MapAudioFormat(request.ReferenceContentType);
        var audioBase64 = Convert.ToBase64String(request.ReferenceAudio);
        
        var payload = new
        {
            model,
            modalities = new[] { "text", "audio" },
            audio = new
            {
                format = "wav", 
                voice = $"data:{mineType};base64,{audioBase64}"
            },
            messages = new object[]
            {
                new
                {
                    role = "user",
                    content = SpeechPromptBuilder.Build(request, _defaultLang),
                },
                new
                {
                    role = "assistant",
                    content = request.Text,
                }
            },
        };

        return JsonSerializer.Serialize(payload);
    }

    /// <summary>
    /// 参考语音 Content-Type → <c>audio.voice</c> data URI 子类型：分号参数归一后
    /// <c>audio/mpeg</c>→<c>mp3</c>、<c>audio/wav</c>/<c>audio/x-wav</c>/<c>audio/wave</c>→<c>wav</c>，
    /// 未知按 <c>mp3</c>（参考音频多来自 recording 合并结果，多为 <c>audio/mpeg</c>）。
    /// </summary>
    internal static string MapAudioFormat(string? contentType)
    {
        var mime = contentType?.Split(';')[0].Trim().ToLowerInvariant();
        return mime switch
        {
            "audio/wav" or "audio/x-wav" or "audio/wave" => "wav",
            _ => "mp3",
        };
    }

    private static byte[] ParseAudio(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var choices = document.RootElement.GetProperty("choices");
            if (choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0)
            {
                throw new ModelOutputException("model response has no choices");
            }

            if (!choices[0].TryGetProperty("message", out var message)
                || !message.TryGetProperty("audio", out var audio)
                || audio.ValueKind != JsonValueKind.Object
                || !audio.TryGetProperty("data", out var data)
                || data.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(data.GetString()))
            {
                throw new ModelOutputException("model response has no audio data");
            }

            var bytes = Convert.FromBase64String(data.GetString()!);
            if (bytes.Length == 0)
            {
                throw new ModelOutputException("model response audio is empty");
            }

            return bytes;
        }
        catch (ModelOutputException)
        {
            throw;
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or IndexOutOfRangeException)
        {
            throw new ModelOutputException($"invalid model response envelope: {ex.Message}");
        }
    }
}
