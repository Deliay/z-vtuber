using MongoDB.Bson;
using Z.Vtuber.Common.Persistence;
using Z.Vtuber.Common.Persistence.Entities;
using Z.Vtuber.Training.FeaturePoints;

namespace Z.Vtuber.Tests;

/// <summary>
/// Plan 3.2：<see cref="TrainingModelResolver"/> 直测（Testcontainers Mongo）——
/// 自动登记（含能力要求更新）、指定优先、默认取 CreatedAt 最早、指定失效回退、无候选 none。
/// </summary>
public class TrainingModelResolverTests(MongoDbFixture fixture)
    : IClassFixture<MongoDbFixture>, IAsyncLifetime
{
    public Task InitializeAsync() => fixture.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private TrainingFeaturePointRepository FeaturePoints => new(fixture.Database);
    private TrainingModelRepository Models => new(fixture.Database);
    private TrainingProviderRepository Providers => new(fixture.Database);

    private ITrainingModelResolver Resolver =>
        new TrainingModelResolver(FeaturePoints, Models, Providers);

    private static readonly string[] TextIn = ["text"];
    private static readonly string[] TextOut = ["text"];
    private static readonly string[] AudioTextIn = ["audio", "text"];

    private async Task<TrainingModelRecord> SeedModelAsync(
        string model, bool enabled = true, string[]? input = null, string[]? output = null,
        DateTime? createdAt = null)
    {
        var now = createdAt ?? DateTime.UtcNow;
        var provider = await Providers.InsertAsync(new TrainingProviderRecord
        {
            Name = $"p-{Guid.NewGuid():N}", Type = "openai", Endpoint = "http://llm", ApiKey = "k",
            CreatedAt = now, UpdatedAt = now,
        });
        return await Models.InsertAsync(new TrainingModelRecord
        {
            ProviderId = provider.Id, Model = model, Enabled = enabled,
            InputCapabilities = input ?? TextIn, OutputCapabilities = output ?? TextOut,
            CreatedAt = now, UpdatedAt = now,
        });
    }

    [Fact]
    public async Task Resolve_AutoRegisters_WithNameAndCapabilities()
    {
        await SeedModelAsync("m1", input: AudioTextIn);

        var resolution = await Resolver.ResolveAsync("fp-x", "功能点X", AudioTextIn, TextOut);

        Assert.Equal("default", resolution.Source);
        var stored = await FeaturePoints.GetBySlugAsync("fp-x");
        Assert.NotNull(stored);
        Assert.Equal("功能点X", stored!.Name);
        Assert.Equal(AudioTextIn, stored.InputCapabilities);
    }

    [Fact]
    public async Task Resolve_SpecifiedAvailable_ReturnsSpecified()
    {
        await SeedModelAsync("first", createdAt: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var target = await SeedModelAsync("target", createdAt: new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc));
        await Resolver.ResolveAsync("fp", "F", TextIn, TextOut);
        await FeaturePoints.SetModelAsync("fp", target.Id, DateTime.UtcNow);

        var resolution = await Resolver.ResolveAsync("fp", "F", TextIn, TextOut);

        Assert.Equal("specified", resolution.Source);
        Assert.Equal(target.Id, resolution.ModelId);
        Assert.EndsWith("/target", resolution.ModelSlug);
    }

    [Fact]
    public async Task Resolve_NoSpecified_ReturnsEarliestCreatedDefault()
    {
        await SeedModelAsync("later", createdAt: new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc));
        var earliest = await SeedModelAsync("earliest", createdAt: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        var resolution = await Resolver.ResolveAsync("fp", "F", TextIn, TextOut);

        Assert.Equal("default", resolution.Source);
        Assert.Equal(earliest.Id, resolution.ModelId);
    }

    [Fact]
    public async Task Resolve_SpecifiedUnavailable_FallsBackToDefault()
    {
        await SeedModelAsync("first", createdAt: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var disabled = await SeedModelAsync("disabled", enabled: false,
            createdAt: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        await Resolver.ResolveAsync("fp", "F", TextIn, TextOut);
        await FeaturePoints.SetModelAsync("fp", disabled.Id, DateTime.UtcNow);

        var resolution = await Resolver.ResolveAsync("fp", "F", TextIn, TextOut);

        Assert.Equal("default", resolution.Source);
        Assert.NotEqual(disabled.Id, resolution.ModelId);
    }

    [Fact]
    public async Task Resolve_NoCandidates_ReturnsNone()
    {
        await SeedModelAsync("audio-only", input: ["audio"], output: ["audio"]);

        var resolution = await Resolver.ResolveAsync("fp", "F", TextIn, TextOut);

        Assert.Equal("none", resolution.Source);
        Assert.Null(resolution.ModelId);
        Assert.Null(resolution.ModelSlug);
    }

    [Fact]
    public async Task Resolve_SecondCall_UpdatesDeclaredCapabilities()
    {
        await SeedModelAsync("audio-text", input: AudioTextIn, output: TextOut);
        await SeedModelAsync("text-only", input: TextIn, output: TextOut);

        await Resolver.ResolveAsync("fp", "F", AudioTextIn, TextOut);
        var second = await Resolver.ResolveAsync("fp", "改名", TextIn, TextOut);

        Assert.Equal("default", second.Source);
        var stored = await FeaturePoints.GetBySlugAsync("fp");
        Assert.Equal("改名", stored!.Name);
        Assert.Equal(TextIn, stored.InputCapabilities);
    }

    [Fact]
    public async Task ListCandidates_FiltersDisabledAndCapabilityMismatch()
    {
        var ok = await SeedModelAsync("ok", input: AudioTextIn, output: TextOut);
        await SeedModelAsync("disabled", enabled: false, input: AudioTextIn, output: TextOut);
        await SeedModelAsync("text-only", input: TextIn, output: TextOut);

        var candidates = await Resolver.ListCandidatesAsync(AudioTextIn, TextOut);

        Assert.Equal(ok.Id, Assert.Single(candidates).Id);
    }
}
