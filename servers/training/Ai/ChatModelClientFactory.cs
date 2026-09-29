using Microsoft.Extensions.Options;
using Z.Vtuber.Common.Persistence.Entities;
using Z.Vtuber.Training.Configuration;

namespace Z.Vtuber.Training.Ai;

/// <summary>
/// 聊天客户端工厂：按 <c>provider.Type</c> 派发（openai → <see cref="OpenAiLikeChatClient"/> /
/// claude → <see cref="ClaudeLikeChatClient"/>）；未知类型抛 <see cref="ProviderTypeException"/>。
/// 使用 named client <c>AiModel</c>（无 BaseAddress，逐请求拼绝对 endpoint）。
/// </summary>
public sealed class ChatModelClientFactory(
    IHttpClientFactory httpClientFactory,
    IOptions<AiOptions> options,
    ILoggerFactory loggerFactory) : IChatModelClientFactory
{
    /// <summary>named client 名称（DI 注册键）。</summary>
    public const string ClientName = "AiModel";

    public IChatModelClient Create(string providerType)
    {
        var client = httpClientFactory.CreateClient(ClientName);
        return providerType?.Trim().ToLowerInvariant() switch
        {
            TrainingProviderTypes.OpenAi => new OpenAiLikeChatClient(
                client, options.Value, loggerFactory.CreateLogger<OpenAiLikeChatClient>()),
            TrainingProviderTypes.Claude => new ClaudeLikeChatClient(
                client, options.Value, loggerFactory.CreateLogger<ClaudeLikeChatClient>()),
            _ => throw new ProviderTypeException($"unsupported provider type: {providerType}"),
        };
    }
}
