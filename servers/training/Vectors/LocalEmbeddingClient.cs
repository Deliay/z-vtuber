using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Z.Vtuber.Training.Configuration;

namespace Z.Vtuber.Training.Vectors;

/// <summary>
/// 本地 embedding 客户端（named client <c>EmbeddingService</c>）：<c>POST {Embedding:BaseUrl}{ApiPath}</c>。
/// 默认 Ollama <c>/api/embeddings</c>（body <c>{"model","prompt"}</c>，响应 <c>{"embedding":[...]}</c>）；
/// 亦兼容较新的 Ollama <c>/api/embed</c>（body <c>{"model","input"}</c>，响应 <c>{"embeddings":[[...]]}</c>，取首个向量）；
/// <c>ApiPath</c> 含 <c>v1</c> 时按 OpenAI 兼容（body <c>{"model","input"}</c>，响应 <c>data[0].embedding</c>）。
/// 不可达/超时/非 2xx/响应非法 → <see cref="EmbeddingUnavailableException"/>；日志不打文本内容。
/// </summary>
public sealed class LocalEmbeddingClient : IEmbeddingClient
{
    /// <summary>named client 名称（DI 注册键）。</summary>
    public const string ClientName = "EmbeddingService";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly EmbeddingOptions _options;
    private readonly ILogger<LocalEmbeddingClient> _logger;

    public LocalEmbeddingClient(
        IHttpClientFactory httpClientFactory,
        IOptions<EmbeddingOptions> options,
        ILogger<LocalEmbeddingClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<float[]> GetEmbeddingAsync(string text, CancellationToken ct = default)
    {
        // 经 IHttpClientFactory 每次取用，保持 handler 轮换（长运行 DNS/连接漂移），避免单例捕获 HttpClient。
        var httpClient = _httpClientFactory.CreateClient(ClientName);
        httpClient.Timeout = TimeSpan.FromSeconds(Math.Max(1, _options.RequestTimeoutSeconds));

        // 仅 Ollama 旧端点 /api/embeddings 用 prompt；OpenAI 兼容 /v1/embeddings 与较新的 Ollama /api/embed 都用 input
        // （/api/embed 传 prompt 会被忽略并返回空 embeddings）。
        var usesLegacyOllamaPrompt = _options.ApiPath
            .TrimEnd('/')
            .EndsWith("/api/embeddings", StringComparison.OrdinalIgnoreCase);
        var payload = usesLegacyOllamaPrompt
            ? new Dictionary<string, object?> { ["model"] = _options.Model, ["prompt"] = text }
            : new Dictionary<string, object?> { ["model"] = _options.Model, ["input"] = text };

        HttpResponseMessage response;
        try
        {
            response = await httpClient.PostAsJsonAsync(_options.ApiPath, payload, ct);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new EmbeddingUnavailableException("embedding request timed out");
        }
        catch (HttpRequestException ex)
        {
            throw new EmbeddingUnavailableException($"embedding request failed: {ex.Message}", ex);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                throw new EmbeddingUnavailableException(
                    $"embedding service returned {(int)response.StatusCode}");
            }

            string body;
            try
            {
                body = await response.Content.ReadAsStringAsync(ct);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new EmbeddingUnavailableException("embedding response read timed out");
            }

            var vector = ParseVector(body);
            if (vector is null || vector.Length == 0)
            {
                throw new EmbeddingUnavailableException("embedding response is invalid");
            }

            _logger.LogDebug(
                "Embedding generated: endpoint={Endpoint} dimensions={Dimensions}",
                _options.ApiPath, vector.Length);
            return vector;
        }
    }

    /// <summary>
    /// 兼容 Ollama <c>{"embedding":[...]}</c>、较新的 Ollama <c>{"embeddings":[[...]]}</c>（取首个向量）
    /// 与 OpenAI <c>data[0].embedding</c> 三种响应。
    /// </summary>
    internal static float[]? ParseVector(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;

            JsonElement array;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("embedding", out var embedding))
            {
                array = embedding;
            }
            else if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("embeddings", out var embeddings))
            {
                array = embeddings;
            }
            else if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("data", out var data)
                && data.ValueKind == JsonValueKind.Array
                && data.GetArrayLength() > 0
                && data[0].TryGetProperty("embedding", out var openAiEmbedding))
            {
                array = openAiEmbedding;
            }
            else
            {
                return null;
            }

            // Ollama /api/embed 的 embeddings 为二维数组（[[...]]），取首个向量。
            if (array.ValueKind == JsonValueKind.Array
                && array.GetArrayLength() > 0
                && array[0].ValueKind == JsonValueKind.Array)
            {
                array = array[0];
            }

            if (array.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var result = new float[array.GetArrayLength()];
            var i = 0;
            foreach (var element in array.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.Number || !element.TryGetDouble(out var value))
                {
                    return null;
                }

                result[i++] = (float)value;
            }

            return result;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
