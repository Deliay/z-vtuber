using Microsoft.Extensions.Options;
using Z.Vtuber.Common.Persistence.Entities;
using Z.Vtuber.Training.Configuration;

namespace Z.Vtuber.Training.Ai;

/// <summary>
/// 图片客户端工厂：按 <c>provider.Type</c> 派发（openai → <see cref="OpenAiLikeImageClient"/>）；
/// claude 及其他类型抛 <see cref="ModelImageNotSupportedException"/>（无图片输出能力）。
/// 复用 named client <c>AiModel</c>（无 BaseAddress，逐请求拼绝对 endpoint；挂 <c>x-opencode-session</c> 头）。
/// </summary>
public sealed class ImageModelClientFactory(
    IHttpClientFactory httpClientFactory,
    IOptions<ImageOptions> options,
    ILoggerFactory loggerFactory) : IImageModelClientFactory
{
    public IImageModelClient Create(string providerType)
    {
        var client = httpClientFactory.CreateClient(ChatModelClientFactory.ClientName);
        return providerType?.Trim().ToLowerInvariant() switch
        {
            TrainingProviderTypes.OpenAi => new OpenAiLikeImageClient(
                client, options.Value, loggerFactory.CreateLogger<OpenAiLikeImageClient>()),
            _ => throw new ModelImageNotSupportedException(),
        };
    }
}
