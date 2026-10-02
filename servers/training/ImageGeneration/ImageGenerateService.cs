using System.Diagnostics;
using MongoDB.Bson;
using Z.Vtuber.Common.Contracts;
using Z.Vtuber.Common.Persistence;
using Z.Vtuber.Common.Persistence.Entities;
using Z.Vtuber.Training.Ai;
using Z.Vtuber.Training.Contracts;
using Z.Vtuber.Training.FeaturePoints;

namespace Z.Vtuber.Training.ImageGeneration;

/// <summary>
/// 绘图生成编排：模型选择（显式 <c>modelId</c> 校验优先；缺省按功能点 <c>image-generate</c>
/// 指定优先 → 默认首候选解析，无可用模型 409）→ 读 Provider → <see cref="IImageModelClient"/> 生成。
/// Info 日志仅记录 modelSlug/图片张数/图片总字节数/耗时，不打文本、图片内容与 apiKey。
/// </summary>
public sealed class ImageGenerateService(
    TrainingModelRepository models,
    TrainingProviderRepository providers,
    ITrainingModelResolver modelResolver,
    IImageModelClientFactory imageClientFactory,
    ILogger<ImageGenerateService> logger)
{
    /// <summary>绘图模型输入能力要求（须覆盖 文本 + 图片）。</summary>
    internal static readonly string[] RequiredInput =
        [TrainingModelCapabilities.Text, TrainingModelCapabilities.Image];

    /// <summary>绘图模型输出能力要求（须含 图片）。</summary>
    internal static readonly string[] RequiredOutput = [TrainingModelCapabilities.Image];

    public async Task<GeneratedImage> GenerateAsync(
        string text, string? modelId, IReadOnlyList<GenerateImageInput> images, CancellationToken ct = default)
    {
        var (model, provider) = await ResolveModelAsync(modelId, ct);

        var client = imageClientFactory.Create(provider.Type);

        var stopwatch = Stopwatch.StartNew();
        var result = await client.GenerateImageAsync(provider, model, text, images, ct);
        stopwatch.Stop();

        logger.LogInformation(
            "training image-generate: modelSlug={ModelSlug} imageCount={ImageCount} totalImageBytes={TotalImageBytes} elapsedMs={ElapsedMs}",
            TrainingMapping.BuildModelSlug(provider.Name, model.Model),
            images.Count,
            images.Sum(i => (long)i.Bytes.Length),
            stopwatch.ElapsedMilliseconds);
        return result;
    }

    /// <summary>
    /// 解析生成所用模型与 Provider：显式 <c>modelId</c>（404/400/能力覆盖校验）优先；
    /// 缺省按功能点 <c>image-generate</c> 解析（指定优先 → 默认首候选），无可用模型抛
    /// <see cref="NoImageModelAvailableException"/>。
    /// </summary>
    private async Task<(TrainingModelRecord Model, TrainingProviderRecord Provider)> ResolveModelAsync(
        string? rawModelId, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(rawModelId))
        {
            if (!ObjectId.TryParse(rawModelId, out var modelObjectId)
                || await models.GetByIdAsync(modelObjectId, ct) is not { } explicitModel)
            {
                throw new ImageModelNotFoundException();
            }

            if (!explicitModel.Enabled)
            {
                throw new ImageModelDisabledException();
            }

            // 能力校验双层之一：编排层显式 Matches（派发层 Claude-like 兜底另计）。
            if (!TrainingModelCapabilities.Matches(explicitModel.InputCapabilities, RequiredInput))
            {
                throw new ImageModelInputNotSupportedException();
            }

            if (!TrainingModelCapabilities.Matches(explicitModel.OutputCapabilities, RequiredOutput))
            {
                throw new ImageModelOutputNotSupportedException();
            }

            if (await providers.GetByIdAsync(explicitModel.ProviderId, ct) is not { } explicitProvider)
            {
                throw new ImageModelNotFoundException();
            }

            return (explicitModel, explicitProvider);
        }

        var definition = FeaturePointIds.ImageGenerateDefinition;
        var resolution = await modelResolver.ResolveAsync(
            definition.Slug, definition.Name,
            definition.InputCapabilities, definition.OutputCapabilities, ct);

        if (resolution.ModelId is not { } resolvedId
            || await models.GetByIdAsync(resolvedId, ct) is not { } resolvedModel
            || await providers.GetByIdAsync(resolvedModel.ProviderId, ct) is not { } resolvedProvider)
        {
            throw new NoImageModelAvailableException();
        }

        return (resolvedModel, resolvedProvider);
    }
}
