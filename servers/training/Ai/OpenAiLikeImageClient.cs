using System.Net.Http.Headers;
using System.Text.Json;
using Z.Vtuber.Common.Persistence.Entities;
using Z.Vtuber.Training.Configuration;

namespace Z.Vtuber.Training.Ai;

/// <summary>
/// OpenAI-like 图片客户端：<c>POST {endpoint}/images/edits</c>（Bearer 鉴权），
/// <c>MultipartFormDataContent</c> 字段 <c>model</c>/<c>prompt</c> 与
/// 图片（单图字段名 <c>image</c>，与现网逐字节一致零回归；多图字段名 <c>image[]</c> 重复）以及
/// <c>response_format=b64_json</c>（显式声明，避免标准 OpenAI 兼容实现默认返回 <c>url</c>），
/// 解析响应 <c>data[0].b64_json</c> → base64 解码字节。非 2xx/超时/传输失败 → <see cref="HttpRequestException"/>
/// （映射 502 model service unavailable）；缺字段/解码失败/<c>url</c> 形态 → <see cref="ModelOutputException"/>
/// （映射 502 invalid model output）。超时经 <c>Image:RequestTimeoutSeconds</c> 生效。
/// 日志绝不打 apiKey、prompt 或图片内容。
/// </summary>
public sealed class OpenAiLikeImageClient : IImageModelClient
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<OpenAiLikeImageClient> _logger;

    public OpenAiLikeImageClient(HttpClient httpClient, ImageOptions options, ILogger<OpenAiLikeImageClient> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
        // 超时经 HttpClient.Timeout 生效（超时抛 TaskCanceledException 向上传播）。
        _httpClient.Timeout = TimeSpan.FromSeconds(Math.Max(1, options.RequestTimeoutSeconds));
    }

    public async Task<GeneratedImage> GenerateImageAsync(
        TrainingProviderRecord provider, TrainingModelRecord model,
        string prompt, IReadOnlyList<GenerateImageInput> images, CancellationToken ct = default)
    {
        var url = $"{provider.Endpoint.TrimEnd('/')}/images/edits";

        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", provider.ApiKey);
        request.Content = BuildForm(model.Model, prompt, images);

        using var response = await _httpClient.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning(
                "OpenAI-like image endpoint returned {Status} for model {Model}",
                (int)response.StatusCode, model.Model);
            throw new HttpRequestException($"model service returned {(int)response.StatusCode}");
        }

        var json = await response.Content.ReadAsStringAsync(ct);
        return new GeneratedImage(ParseB64(json), "image/png");
    }

    /// <summary>
    /// 组装 <c>/images/edits</c> multipart 表单（model/prompt/图片/response_format）：
    /// 单图挂字段名 <c>image</c>（与现网逐字节一致，零回归）；多图按 OpenAI 契约挂 <c>image[]</c> 重复字段。
    /// 显式声明 <c>response_format=b64_json</c> 与解析层「仅认 b64_json」自洽（标准 OpenAI 兼容实现默认返回 url）。
    /// </summary>
    internal static MultipartFormDataContent BuildForm(
        string model, string prompt, IReadOnlyList<GenerateImageInput> images)
    {
        var content = new MultipartFormDataContent();
        content.Add(new StringContent(model), "model");
        content.Add(new StringContent(prompt), "prompt");
        content.Add(new StringContent("b64_json"), "response_format");

        // 单图零回归：字段名恰为 image（非 image[]），文件名缺省仍为 image。
        if (images.Count == 1)
        {
            AddImage(content, images[0], "image", "image");
            return content;
        }

        for (var i = 0; i < images.Count; i++)
        {
            AddImage(content, images[i], "image[]", $"image-{i + 1}");
        }

        return content;
    }

    private static void AddImage(
        MultipartFormDataContent content, GenerateImageInput image, string fieldName, string fallbackFileName)
    {
        var file = new ByteArrayContent(image.Bytes);
        if (!string.IsNullOrWhiteSpace(image.MimeType))
        {
            file.Headers.TryAddWithoutValidation("Content-Type", image.MimeType);
        }

        content.Add(file, fieldName, string.IsNullOrWhiteSpace(image.FileName) ? fallbackFileName : image.FileName);
    }

    private static byte[] ParseB64(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var data = document.RootElement.GetProperty("data");
            if (data.ValueKind != JsonValueKind.Array || data.GetArrayLength() == 0)
            {
                throw new ModelOutputException("model response has no image data");
            }

            var b64 = data[0].GetProperty("b64_json");
            if (b64.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(b64.GetString()))
            {
                throw new ModelOutputException("model response b64_json is empty");
            }

            return Convert.FromBase64String(b64.GetString()!);
        }
        catch (ModelOutputException)
        {
            throw;
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw new ModelOutputException($"invalid model response envelope: {ex.Message}");
        }
    }
}
