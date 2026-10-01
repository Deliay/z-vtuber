using MongoDB.Bson;
using Z.Vtuber.Common.Persistence;
using Z.Vtuber.Common.Persistence.Entities;
using Z.Vtuber.Training.Contracts;

namespace Z.Vtuber.Training.FeaturePoints;

/// <summary>功能点模型解析结果：<c>source</c> = specified | default | none。</summary>
public sealed record ModelResolution(string Slug, ObjectId? ModelId, string? ModelSlug, string Source);

/// <summary>功能点模型解析：slug 自动登记 + 指定/默认解析。</summary>
public interface ITrainingModelResolver
{
    /// <summary>
    /// 自动登记（upsert slug + 能力要求，保留既有指定）后解析：
    /// 指定模型可用 → <c>specified</c>；否则默认选择（可用模型中 CreatedAt 最早者）→ <c>default</c>
    /// （指定存在但不可用同样回 <c>default</c>）；无候选 → <c>none</c>。
    /// </summary>
    Task<ModelResolution> ResolveAsync(
        string slug,
        string? name,
        IReadOnlyList<string> input,
        IReadOnlyList<string> output,
        CancellationToken ct = default);

    /// <summary>功能点当前可用模型集合（Enabled + 能力匹配，CreatedAt 升序），供指定校验与列表标注复用。</summary>
    Task<List<TrainingModelRecord>> ListCandidatesAsync(
        IReadOnlyList<string> input,
        IReadOnlyList<string> output,
        CancellationToken ct = default);
}

/// <inheritdoc />
public sealed class TrainingModelResolver(
    TrainingFeaturePointRepository featurePoints,
    TrainingModelRepository models,
    TrainingProviderRepository providers) : ITrainingModelResolver
{
    /// <summary>解析结果来源：指定模型可用。</summary>
    public const string SourceSpecified = "specified";

    /// <summary>解析结果来源：回退默认选择。</summary>
    public const string SourceDefault = "default";

    /// <summary>解析结果来源：无可用模型。</summary>
    public const string SourceNone = "none";

    public async Task<List<TrainingModelRecord>> ListCandidatesAsync(
        IReadOnlyList<string> input, IReadOnlyList<string> output, CancellationToken ct = default) =>
        await models.ListAsync(enabled: true, input, output, ct);

    public async Task<ModelResolution> ResolveAsync(
        string slug,
        string? name,
        IReadOnlyList<string> input,
        IReadOnlyList<string> output,
        CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var registered = await featurePoints.RegisterAsync(new TrainingFeaturePointRecord
        {
            Slug = slug,
            Name = string.IsNullOrWhiteSpace(name) ? slug : name.Trim(),
            InputCapabilities = input.ToArray(),
            OutputCapabilities = output.ToArray(),
            CreatedAt = now,
            UpdatedAt = now,
        }, ct);

        var candidates = await ListCandidatesAsync(input, output, ct);

        TrainingModelRecord? chosen = null;
        var source = SourceNone;
        if (registered.ModelId is { } specifiedId)
        {
            chosen = candidates.FirstOrDefault(c => c.Id == specifiedId);
            if (chosen is not null)
            {
                source = SourceSpecified;
            }
        }

        if (chosen is null && candidates.Count > 0)
        {
            chosen = candidates[0];
            source = SourceDefault;
        }

        if (chosen is null)
        {
            return new ModelResolution(slug, null, null, SourceNone);
        }

        var providerNames = await BuildProviderNamesAsync(ct);
        var modelSlug = TrainingMapping.BuildModelSlug(
            providerNames.GetValueOrDefault(chosen.ProviderId, string.Empty), chosen.Model);
        return new ModelResolution(slug, chosen.Id, modelSlug, source);
    }

    private async Task<Dictionary<ObjectId, string>> BuildProviderNamesAsync(CancellationToken ct) =>
        (await providers.ListAsync(ct)).ToDictionary(p => p.Id, p => p.Name);
}
