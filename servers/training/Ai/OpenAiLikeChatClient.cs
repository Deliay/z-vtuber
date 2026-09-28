using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Z.Vtuber.Common.Persistence.Entities;
using Z.Vtuber.Training.Configuration;

namespace Z.Vtuber.Training.Ai;

/// <summary>
/// OpenAI-like 聊天客户端：<c>POST {endpoint}/chat/completions</c>（Bearer 鉴权），
/// user 内容为 text 段 + 可选 <c>input_audio</c> 段；要求 <c>response_format: json_object</c>，
/// 服务端不支持（首个响应 400）时去掉该字段重试一次。日志绝不打 apiKey 与音频内容。
/// </summary>
public sealed class OpenAiLikeChatClient : IChatModelClient
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<OpenAiLikeChatClient> _logger;

    public OpenAiLikeChatClient(HttpClient httpClient, AiOptions options, ILogger<OpenAiLikeChatClient> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
        // 超时经 HttpClient.Timeout 生效（超时抛 TaskCanceledException 向上传播）。
        _httpClient.Timeout = TimeSpan.FromSeconds(Math.Max(1, options.RequestTimeoutSeconds));
    }

    public async Task<string> CompleteJsonAsync(
        TrainingProviderRecord provider, TrainingModelRecord model,
        ChatPrompt prompt, CancellationToken ct = default)
    {
        var url = $"{provider.Endpoint.TrimEnd('/')}/chat/completions";

        using var first = await SendAsync(url, provider.ApiKey, BuildBody(model.Model, prompt, true), ct);
        if (first.StatusCode == HttpStatusCode.BadRequest)
        {
            // 部分 OpenAI-like 服务不支持 response_format：降级为纯 prompt 约束重试一次。
            _logger.LogWarning(
                "OpenAI-like endpoint rejected response_format for model {Model}; retrying without it",
                model.Model);
            using var retry = await SendAsync(url, provider.ApiKey, BuildBody(model.Model, prompt, false), ct);
            return await ExtractContentAsync(retry, ct);
        }

        return await ExtractContentAsync(first, ct);
    }

    private async Task<HttpResponseMessage> SendAsync(
        string url, string apiKey, string body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        return await _httpClient.SendAsync(request, ct);
    }

    private static async Task<string> ExtractContentAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"model service returned {(int)response.StatusCode}");
        }

        var json = await response.Content.ReadAsStringAsync(ct);
        try
        {
            using var document = JsonDocument.Parse(json);
            var content = document.RootElement
                .GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content");
            if (content.ValueKind != JsonValueKind.String)
            {
                throw new ModelOutputException("model output content is not a string");
            }

            return content.GetString() ?? string.Empty;
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or IndexOutOfRangeException or InvalidOperationException)
        {
            throw new ModelOutputException($"invalid model response envelope: {ex.Message}");
        }
    }

    private static string BuildBody(string model, ChatPrompt prompt, bool includeResponseFormat)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("model", model);
            writer.WritePropertyName("messages");
            writer.WriteStartArray();

            writer.WriteStartObject();
            writer.WriteString("role", "system");
            writer.WriteString("content", prompt.System);
            writer.WriteEndObject();

            writer.WriteStartObject();
            writer.WriteString("role", "user");
            writer.WritePropertyName("content");
            writer.WriteStartArray();
            writer.WriteStartObject();
            writer.WriteString("type", "text");
            writer.WriteString("text", prompt.User);
            writer.WriteEndObject();

            if (prompt.Audio is not null)
            {
                writer.WriteStartObject();
                writer.WriteString("type", "input_audio");
                writer.WritePropertyName("input_audio");
                writer.WriteStartObject();
                writer.WriteString("data", prompt.Audio.Base64);
                writer.WriteString("format", FormatFor(prompt.Audio.MimeType));
                writer.WriteEndObject();
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.WriteEndArray();

            if (includeResponseFormat)
            {
                writer.WritePropertyName("response_format");
                writer.WriteStartObject();
                writer.WriteString("type", "json_object");
                writer.WriteEndObject();
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>mime → input_audio.format（OpenAI-like 仅接受 mp3/wav/mp4）。</summary>
    private static string FormatFor(string mimeType) => mimeType.ToLowerInvariant() switch
    {
        "audio/wav" or "audio/x-wav" or "audio/wave" => "wav",
        "audio/mp4" or "audio/m4a" or "video/mp4" => "mp4",
        _ => "mp3",
    };
}
