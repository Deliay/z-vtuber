using Z.Vtuber.Common.Persistence.Entities;

namespace Z.Vtuber.Training.Ai;

/// <summary>音频输入（base64 + mime，如 <c>audio/mpeg</c>）。</summary>
public sealed record ChatAudio(string Base64, string MimeType);

/// <summary>模型调用入参：system + user 文本 + 可选音频。</summary>
public sealed record ChatPrompt(string System, string User, ChatAudio? Audio);

/// <summary>
/// 聊天模型客户端抽象：调用模型并要求 JSON 输出，返回原始文本（由 <see cref="ModelJsonParser"/> 解析）。
/// 传输失败/超时/非 2xx 抛 <see cref="HttpRequestException"/>；能力/输出问题抛本文件定义的专用异常。
/// </summary>
public interface IChatModelClient
{
    Task<string> CompleteJsonAsync(
        TrainingProviderRecord provider, TrainingModelRecord model,
        ChatPrompt prompt, CancellationToken ct = default);
}

/// <summary>按 <c>provider.Type</c> 派发（openai → OpenAI-like / claude → Claude-like）；未知类型抛 <see cref="ProviderTypeException"/>。</summary>
public interface IChatModelClientFactory
{
    IChatModelClient Create(string providerType);
}
