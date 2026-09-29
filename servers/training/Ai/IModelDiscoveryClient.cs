using Z.Vtuber.Common.Persistence.Entities;

namespace Z.Vtuber.Training.Ai;

/// <summary>
/// 从 Provider Endpoint 同步模型列表：OpenAI-like 调 <c>GET {endpoint}/models</c>、
/// Claude-like 调 <c>GET {endpoint}/v1/models</c>，解析后去重排序。
/// Endpoint 不可达/超时/非 2xx 抛 <see cref="HttpRequestException"/>；
/// 响应无法解析抛 <see cref="InvalidProviderResponseException"/>。
/// </summary>
public interface IModelDiscoveryClient
{
    Task<IReadOnlyList<string>> DiscoverAsync(
        TrainingProviderRecord provider, CancellationToken ct = default);
}
