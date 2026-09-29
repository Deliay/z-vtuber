using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Z.Vtuber.Common.Persistence.Entities;
using Z.Vtuber.Training.Configuration;

namespace Z.Vtuber.Training.Ai;

/// <summary>
/// 从 Provider Endpoint 同步模型列表：OpenAI-like <c>GET {endpoint}/models</c>、
/// Claude-like <c>GET {endpoint}/v1/models</c>；解析 <c>data[].id</c>（兼容根数组、<c>data</c> 缺失 → 空列表），
/// 去重后按 ordinal 升序返回。不可达/超时/非 2xx → <see cref="HttpRequestException"/>，
/// 响应无法解析 → <see cref="InvalidProviderResponseException"/>。日志只打 endpoint 与条数，绝不打 apiKey。
/// </summary>
public sealed class ModelDiscoveryClient(
    IHttpClientFactory httpClientFactory,
    IOptions<AiOptions> options,
    ILogger<ModelDiscoveryClient> logger) : IModelDiscoveryClient
{
    public async Task<IReadOnlyList<string>> DiscoverAsync(
        TrainingProviderRecord provider, CancellationToken ct = default)
    {
        var type = provider.Type?.Trim().ToLowerInvariant();
        var path = type switch
        {
            TrainingProviderTypes.OpenAi => "/models",
            TrainingProviderTypes.Claude => "/v1/models",
            _ => throw new ProviderTypeException($"unsupported provider type: {provider.Type}"),
        };

        var url = $"{provider.Endpoint.TrimEnd('/')}{path}";
        var client = httpClientFactory.CreateClient(ChatModelClientFactory.ClientName);
        client.Timeout = TimeSpan.FromSeconds(Math.Max(1, options.Value.RequestTimeoutSeconds));

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (type == TrainingProviderTypes.OpenAi)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", provider.ApiKey);
        }
        else
        {
            request.Headers.Add("x-api-key", provider.ApiKey);
            request.Headers.Add("anthropic-version", "2023-06-01");
        }

        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(request, ct);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new HttpRequestException("model discovery timed out");
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException($"provider service returned {(int)response.StatusCode}");
            }

            var payload = await response.Content.ReadAsStringAsync(ct);
            var ids = ParseIds(payload);
            var ordered = ids
                .Distinct(StringComparer.Ordinal)
                .OrderBy(id => id, StringComparer.Ordinal)
                .ToList();

            logger.LogInformation(
                "Discovered {Count} models from endpoint {Endpoint}", ordered.Count, provider.Endpoint);
            return ordered;
        }
    }

    private static List<string> ParseIds(string payload)
    {
        var ids = new List<string>();
        try
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;

            JsonElement array;
            if (root.ValueKind == JsonValueKind.Array)
            {
                array = root;
            }
            else if (root.ValueKind == JsonValueKind.Object)
            {
                // data 缺失/非数组 → 无模型（不视为解析失败）
                array = root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array
                    ? data
                    : default;
            }
            else
            {
                throw new InvalidProviderResponseException("invalid model list response");
            }

            if (array.ValueKind == JsonValueKind.Array)
            {
                foreach (var entry in array.EnumerateArray())
                {
                    if (entry.ValueKind == JsonValueKind.Object
                        && entry.TryGetProperty("id", out var id)
                        && id.ValueKind == JsonValueKind.String
                        && !string.IsNullOrWhiteSpace(id.GetString()))
                    {
                        ids.Add(id.GetString()!);
                    }
                }
            }
        }
        catch (JsonException ex)
        {
            throw new InvalidProviderResponseException($"invalid model list response: {ex.Message}");
        }

        return ids;
    }
}
