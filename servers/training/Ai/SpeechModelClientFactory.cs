using Microsoft.Extensions.Options;
using Z.Vtuber.Common.Persistence.Entities;
using Z.Vtuber.Training.Configuration;

namespace Z.Vtuber.Training.Ai;

/// <summary>
/// 语音客户端工厂：按 <c>provider.Type</c> 派发（openai → <see cref="OpenAiLikeSpeechClient"/>）；
/// claude 及其他类型抛 <see cref="ModelAudioOutputNotSupportedException"/>（无音频输出能力，映射 400）。
/// 复用 named client <c>AiModel</c>（无 BaseAddress，逐请求拼绝对 endpoint；挂 <c>x-opencode-session</c> 头）。
/// </summary>
public sealed class SpeechModelClientFactory(
    IHttpClientFactory httpClientFactory,
    IOptions<AiOptions> options,
    ILoggerFactory loggerFactory) : ISpeechModelClientFactory
{
    public ISpeechModelClient Create(string providerType)
    {
        var client = httpClientFactory.CreateClient(ChatModelClientFactory.ClientName);
        return providerType?.Trim().ToLowerInvariant() switch
        {
            TrainingProviderTypes.OpenAi => new OpenAiLikeSpeechClient(
                client, options.Value, loggerFactory.CreateLogger<OpenAiLikeSpeechClient>()),
            _ => throw new ModelAudioOutputNotSupportedException(),
        };
    }
}
