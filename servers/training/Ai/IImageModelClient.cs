using Z.Vtuber.Common.Persistence.Entities;

namespace Z.Vtuber.Training.Ai;

/// <summary>图片输入（字节 + mime + 文件名，源自 multipart 上传）。</summary>
public sealed record GenerateImageInput(byte[] Bytes, string MimeType, string FileName);

/// <summary>图片生成结果（字节 + mime，缺省 <c>image/png</c>）。</summary>
public sealed record GeneratedImage(byte[] Bytes, string MimeType);

/// <summary>
/// 图片生成客户端抽象：调用模型生成图片。
/// 传输失败/超时/非 2xx 抛 <see cref="HttpRequestException"/>（映射 502 <c>model service unavailable</c>）；
/// 输出无法解析出图片抛 <see cref="ModelOutputException"/>（映射 502 <c>invalid model output</c>）；
/// Provider 类型不支持图片输出抛 <see cref="ModelImageNotSupportedException"/>（映射 400）。
/// 日志绝不打 apiKey、prompt 或图片内容。
/// </summary>
public interface IImageModelClient
{
    Task<GeneratedImage> GenerateImageAsync(
        TrainingProviderRecord provider, TrainingModelRecord model,
        string prompt, GenerateImageInput image, CancellationToken ct = default);
}

/// <summary>
/// 按 <c>provider.Type</c> 派发（openai → OpenAI-like <c>/images/edits</c>）；
/// claude 等其他类型抛 <see cref="ModelImageNotSupportedException"/>（无图片输出能力）。
/// </summary>
public interface IImageModelClientFactory
{
    IImageModelClient Create(string providerType);
}
