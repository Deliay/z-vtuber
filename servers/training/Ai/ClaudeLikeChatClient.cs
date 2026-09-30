using System.Text;
using System.Text.Json;
using Z.Vtuber.Common.Persistence.Entities;
using Z.Vtuber.Training.Configuration;

namespace Z.Vtuber.Training.Ai;

/// <summary>
/// Claude-like 聊天客户端：<c>POST {endpoint}/v1/messages</c>（<c>x-api-key</c> + <c>anthropic-version</c>），
/// 内容块仅 text；Anthropic Messages 协议无音频模态，<see cref="ChatPrompt.Audio"/> 非空直接抛
/// <see cref="ModelAudioNotSupportedException"/>（映射 400）。日志绝不打 apiKey。
/// </summary>
public sealed class ClaudeLikeChatClient : IChatModelClient
{
    private const int MaxTokens = 4096;

    private readonly HttpClient _httpClient;

    public ClaudeLikeChatClient(HttpClient httpClient, AiOptions options, ILogger<ClaudeLikeChatClient> logger)
    {
        _httpClient = httpClient;
        _httpClient.Timeout = TimeSpan.FromSeconds(Math.Max(1, options.RequestTimeoutSeconds));
    }

    public async Task<string> CompleteJsonAsync(
        TrainingProviderRecord provider, TrainingModelRecord model,
        ChatPrompt prompt, CancellationToken ct = default)
    {
        if (prompt.Audio is not null)
        {
            throw new ModelAudioNotSupportedException();
        }

        var url = $"{provider.Endpoint.TrimEnd('/')}/v1/messages";
        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Add("x-api-key", provider.ApiKey);
        request.Headers.Add("anthropic-version", "2023-06-01");
        request.Content = new StringContent(BuildBody(model.Model, prompt), Encoding.UTF8, "application/json");

        using var response = await _httpClient.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"model service returned {(int)response.StatusCode}");
        }

        var json = await response.Content.ReadAsStringAsync(ct);
        try
        {
            using var document = JsonDocument.Parse(json);
            var text = document.RootElement.GetProperty("content")[0].GetProperty("text");
            if (text.ValueKind != JsonValueKind.String)
            {
                throw new ModelOutputException("model output content is not a string");
            }

            return text.GetString() ?? string.Empty;
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or IndexOutOfRangeException or InvalidOperationException)
        {
            throw new ModelOutputException($"invalid model response envelope: {ex.Message}");
        }
    }

    private static string BuildBody(string model, ChatPrompt prompt)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("model", model);
            writer.WriteNumber("temperature", 0);
            writer.WriteNumber("max_tokens", MaxTokens);
            writer.WriteString("system", prompt.System);
            writer.WritePropertyName("messages");
            writer.WriteStartArray();
            writer.WriteStartObject();
            writer.WriteString("role", "user");
            writer.WritePropertyName("content");
            writer.WriteStartArray();
            writer.WriteStartObject();
            writer.WriteString("type", "text");
            writer.WriteString("text", prompt.User);
            writer.WriteEndObject();
            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }
}
