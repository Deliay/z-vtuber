using System.Text;
using System.Text.Json;

namespace Z.Vtuber.Training.Vectors;

/// <summary>Milvus RESTful API v2 响应：<c>code==0</c> 为成功；<c>message</c> 为错误/描述；<c>data</c> 为业务数据。</summary>
public sealed record MilvusResponse(int Code, string Message, JsonElement? Data)
{
    /// <summary>Milvus 成功码。</summary>
    public const int SuccessCode = 0;

    public bool IsSuccess => Code == SuccessCode;
}

/// <summary>
/// Milvus 底层 HTTP 客户端抽象（REST API v2，路径形如 <c>/v2/vectordb/collections/create</c>）：
/// 传输失败/超时 → <see cref="VectorStoreUnavailableException"/>；业务错误以 <see cref="MilvusResponse.Code"/> 表达（不抛）。
/// </summary>
public interface IMilvusRestClient
{
    Task<MilvusResponse> PostAsync(string path, object payload, CancellationToken ct = default);
}

/// <summary>
/// <see cref="IMilvusRestClient"/> 的 HttpClient 实现（named client <c>MilvusStore</c>）：
/// <c>POST {Milvus:Endpoint}{path}</c>，<c>Authorization: Bearer {Milvus:Token}</c>（token 非空时）。
/// 任何 HTTP 状态码都尝试解析 JSON（Milvus 以 <c>code</c> 表达错误）；无法解析/传输异常视为不可用。
/// </summary>
public sealed class MilvusRestClient(
    HttpClient httpClient,
    Microsoft.Extensions.Options.IOptions<Configuration.MilvusOptions> options) : IMilvusRestClient
{
    /// <summary>named client 名称（DI 注册键）。</summary>
    public const string ClientName = "MilvusStore";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<MilvusResponse> PostAsync(string path, object payload, CancellationToken ct = default)
    {
        var opts = options.Value;
        var uri = $"{opts.Endpoint.TrimEnd('/')}{path}";
        using var request = new HttpRequestMessage(HttpMethod.Post, uri)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload, JsonOptions), Encoding.UTF8, "application/json"),
        };
        if (!string.IsNullOrEmpty(opts.Token))
        {
            request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {opts.Token}");
        }

        HttpResponseMessage response;
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, opts.RequestTimeoutSeconds)));
            response = await httpClient.SendAsync(request, timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new VectorStoreUnavailableException("milvus request timed out");
        }
        catch (HttpRequestException ex)
        {
            throw new VectorStoreUnavailableException($"milvus request failed: {ex.Message}", ex);
        }

        using (response)
        {
            string body;
            try
            {
                body = await response.Content.ReadAsStringAsync(ct);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new VectorStoreUnavailableException("milvus response read timed out");
            }
            catch (HttpRequestException ex)
            {
                throw new VectorStoreUnavailableException($"milvus response read failed: {ex.Message}", ex);
            }

            try
            {
                using var document = JsonDocument.Parse(body);
                var root = document.RootElement;
                var code = root.TryGetProperty("code", out var codeElement) && codeElement.TryGetInt32(out var parsed)
                    ? parsed
                    : -1;
                var message = root.TryGetProperty("message", out var messageElement)
                    && messageElement.ValueKind == JsonValueKind.String
                    ? messageElement.GetString() ?? string.Empty
                    : string.Empty;
                JsonElement? data = root.TryGetProperty("data", out var dataElement)
                    ? dataElement.Clone()
                    : null;
                return new MilvusResponse(code, message, data);
            }
            catch (JsonException ex)
            {
                throw new VectorStoreUnavailableException("milvus returned invalid json", ex);
            }
        }
    }
}
