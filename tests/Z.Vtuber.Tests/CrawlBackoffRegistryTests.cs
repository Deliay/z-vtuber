using Microsoft.Extensions.Options;
using Z.Vtuber.Crawler.Configuration;
using Z.Vtuber.Crawler.Crawlers;

namespace Z.Vtuber.Tests;

public class CrawlBackoffRegistryTests
{
    private static BilibiliCrawlerOptions DefaultOptions() => new()
    {
        BackoffBaseSeconds = 30,
        BackoffMaxMinutes = 15,
        CooldownMinutes = 30,
        RiskControlCooldownThreshold = 5,
    };

    [Fact]
    public void TryAcquire_FreshKey_ReturnsTrue()
    {
        var registry = new CrawlBackoffRegistry(Options.Create(DefaultOptions()));

        var allowed = registry.TryAcquire("key:1", out var wait);

        Assert.True(allowed);
        Assert.Equal(TimeSpan.Zero, wait);
    }

    [Fact]
    public void ReportRiskControl_IncreasesBackoffLevel()
    {
        var registry = new CrawlBackoffRegistry(Options.Create(DefaultOptions()));

        registry.ReportRiskControl("key:1");
        var allowed = registry.TryAcquire("key:1", out var wait);

        Assert.False(allowed);
        Assert.True(wait > TimeSpan.Zero);
    }

    [Fact]
    public void Backoff_ExponentialGrowth()
    {
        var registry = new CrawlBackoffRegistry(Options.Create(DefaultOptions()));

        registry.ReportRiskControl("key:1");
        registry.TryAcquire("key:1", out var wait1);

        registry.ReportRiskControl("key:1");
        registry.TryAcquire("key:1", out var wait2);

        Assert.True(wait2 > wait1, $"wait2 ({wait2}) should be greater than wait1 ({wait1})");
    }

    [Fact]
    public void Backoff_CappedAtMaxMinutes()
    {
        var options = DefaultOptions();
        options.BackoffMaxMinutes = 1; // 1 minute cap
        options.RiskControlCooldownThreshold = 100; // avoid cooldown
        var registry = new CrawlBackoffRegistry(Options.Create(options));

        for (var i = 0; i < 20; i++)
            registry.ReportRiskControl("key:1");

        registry.TryAcquire("key:1", out var wait);

        // base=30, level=20 would be huge, but capped at 1 min + 25% jitter
        Assert.True(wait <= TimeSpan.FromMinutes(1.25), $"wait ({wait}) should be capped near 1 minute");
    }

    [Fact]
    public void Backoff_JitterWithinPlusMinus25Percent()
    {
        var options = DefaultOptions();
        options.BackoffBaseSeconds = 100;
        var registry = new CrawlBackoffRegistry(Options.Create(options));

        registry.ReportRiskControl("key:1");
        registry.TryAcquire("key:1", out var wait);

        var baseSeconds = 100.0;
        var minExpected = baseSeconds * 0.75;
        var maxExpected = baseSeconds * 1.25;

        Assert.True(wait.TotalSeconds >= minExpected, $"wait ({wait.TotalSeconds}s) should be >= {minExpected}s");
        Assert.True(wait.TotalSeconds <= maxExpected, $"wait ({wait.TotalSeconds}s) should be <= {maxExpected}s");
    }

    [Fact]
    public void Cooldown_EntersAfterThreshold()
    {
        var options = DefaultOptions();
        options.RiskControlCooldownThreshold = 3;
        options.CooldownMinutes = 10;
        var registry = new CrawlBackoffRegistry(Options.Create(options));

        for (var i = 0; i < 3; i++)
            registry.ReportRiskControl("key:1");

        var allowed = registry.TryAcquire("key:1", out var wait);

        Assert.False(allowed);
        Assert.True(wait >= TimeSpan.FromMinutes(10) - TimeSpan.FromMilliseconds(1), $"cooldown wait ({wait}) should be >= 10 minutes");
    }

    [Fact]
    public void Cooldown_AllowsAfterExpiry()
    {
        var options = DefaultOptions();
        options.RiskControlCooldownThreshold = 3;
        options.CooldownMinutes = 5;
        var fakeTime = new FakeTimeProvider();
        var registry = new CrawlBackoffRegistry(Options.Create(options), fakeTime);

        for (var i = 0; i < 3; i++)
            registry.ReportRiskControl("key:1");

        // Advance past cooldown
        fakeTime.Advance(TimeSpan.FromMinutes(6));

        var allowed = registry.TryAcquire("key:1", out _);

        Assert.True(allowed);
    }

    [Fact]
    public void ReportSuccess_ClearsBackoffLevel()
    {
        var registry = new CrawlBackoffRegistry(Options.Create(DefaultOptions()));

        registry.ReportRiskControl("key:1");
        registry.ReportRiskControl("key:1");
        registry.ReportRiskControl("key:1");

        registry.ReportSuccess("key:1");
        var allowed = registry.TryAcquire("key:1", out var wait);

        Assert.True(allowed);
        Assert.Equal(TimeSpan.Zero, wait);
    }

    [Fact]
    public void DifferentKeys_AreIsolated()
    {
        var registry = new CrawlBackoffRegistry(Options.Create(DefaultOptions()));

        registry.ReportRiskControl("key:1");

        var allowedA = registry.TryAcquire("key:1", out _);
        var allowedB = registry.TryAcquire("key:2", out _);

        Assert.False(allowedA);
        Assert.True(allowedB);
    }

    [Fact]
    public void ReportRiskControl_ResetsCooldownOnNewRiskControl()
    {
        var options = DefaultOptions();
        options.RiskControlCooldownThreshold = 3;
        options.CooldownMinutes = 10;
        var fakeTime = new FakeTimeProvider();
        var registry = new CrawlBackoffRegistry(Options.Create(options), fakeTime);

        for (var i = 0; i < 3; i++)
            registry.ReportRiskControl("key:1");

        // Partial cooldown
        fakeTime.Advance(TimeSpan.FromMinutes(5));

        // New risk control resets cooldown
        registry.ReportRiskControl("key:1");

        // Still in cooldown (reset to 10 min from now)
        var allowed = registry.TryAcquire("key:1", out var wait);
        Assert.False(allowed);
        Assert.True(wait >= TimeSpan.FromMinutes(5) - TimeSpan.FromMilliseconds(1), $"wait ({wait}) should be >= 5 minutes after cooldown reset");
    }

    [Fact]
    public void ReportRiskControl_InCooldown_DoesNotIncreaseBackoffLevel()
    {
        var options = DefaultOptions();
        options.RiskControlCooldownThreshold = 3;
        options.CooldownMinutes = 10;
        var fakeTime = new FakeTimeProvider();
        var registry = new CrawlBackoffRegistry(Options.Create(options), fakeTime);

        for (var i = 0; i < 3; i++)
            registry.ReportRiskControl("key:1");

        // Report more risk controls while in cooldown
        registry.ReportRiskControl("key:1");
        registry.ReportRiskControl("key:1");

        // Exit cooldown
        fakeTime.Advance(TimeSpan.FromMinutes(11));
        registry.ReportSuccess("key:1");

        // Backoff should start fresh (level 0)
        var allowed = registry.TryAcquire("key:1", out _);
        Assert.True(allowed);
    }
}

internal sealed class FakeTimeProvider : TimeProvider
{
    private DateTimeOffset _now = DateTimeOffset.UtcNow;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan duration) => _now += duration;
}
