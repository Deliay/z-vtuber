using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using Z.Vtuber.Crawler.Configuration;

namespace Z.Vtuber.Crawler.Crawlers;

public interface ICrawlBackoffRegistry
{
    bool TryAcquire(string key, out TimeSpan wait);
    void ReportSuccess(string key);
    void ReportRiskControl(string key);
}

public sealed class CrawlBackoffRegistry : ICrawlBackoffRegistry
{
    private readonly BilibiliCrawlerOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly Random _random;
    private readonly ConcurrentDictionary<string, BackoffEntry> _entries = new();

    public CrawlBackoffRegistry(
        IOptions<BilibiliCrawlerOptions> options,
        TimeProvider? timeProvider = null,
        Random? random = null)
    {
        _options = options.Value;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _random = random ?? Random.Shared;
    }

    public bool TryAcquire(string key, out TimeSpan wait)
    {
        var entry = _entries.GetOrAdd(key, _ => new BackoffEntry());

        lock (entry)
        {
            var now = _timeProvider.GetUtcNow();

            // In cooldown - reject
            if (entry.CooldownUntil.HasValue && now < entry.CooldownUntil.Value)
            {
                wait = entry.CooldownUntil.Value - now;
                return false;
            }

            // Clear expired cooldown
            if (entry.CooldownUntil.HasValue && now >= entry.CooldownUntil.Value)
            {
                entry.CooldownUntil = null;
                entry.ConsecutiveRiskControls = 0;
            }

            // Calculate backoff wait
            if (entry.Level == 0)
            {
                wait = TimeSpan.Zero;
                return true;
            }

            var backoffSeconds = CalculateBackoffSeconds(entry.Level);
            var elapsed = now - entry.LastRiskControlAt;
            var remaining = TimeSpan.FromSeconds(backoffSeconds) - elapsed;

            if (remaining <= TimeSpan.Zero)
            {
                wait = TimeSpan.Zero;
                return true;
            }

            wait = remaining;
            return false;
        }
    }

    public void ReportSuccess(string key)
    {
        var entry = _entries.GetOrAdd(key, _ => new BackoffEntry());

        lock (entry)
        {
            entry.Level = 0;
            entry.ConsecutiveRiskControls = 0;
            entry.CooldownUntil = null;
        }
    }

    public void ReportRiskControl(string key)
    {
        var entry = _entries.GetOrAdd(key, _ => new BackoffEntry());

        lock (entry)
        {
            var now = _timeProvider.GetUtcNow();

            // If in cooldown, reset cooldown from now (don't increase level)
            if (entry.CooldownUntil.HasValue && now < entry.CooldownUntil.Value)
            {
                entry.CooldownUntil = now + TimeSpan.FromMinutes(_options.CooldownMinutes);
                return;
            }

            entry.ConsecutiveRiskControls++;
            entry.LastRiskControlAt = now;

            // Check if we should enter cooldown
            if (entry.ConsecutiveRiskControls >= _options.RiskControlCooldownThreshold)
            {
                entry.CooldownUntil = now + TimeSpan.FromMinutes(_options.CooldownMinutes);
                entry.Level = 0;
                entry.ConsecutiveRiskControls = 0;
                return;
            }

            entry.Level++;
        }
    }

    private double CalculateBackoffSeconds(int level)
    {
        var baseSeconds = _options.BackoffBaseSeconds;
        var maxSeconds = _options.BackoffMaxMinutes * 60.0;

        // base * 2^(level-1), capped at max; level 1 = base, level 2 = 2*base, etc.
        var raw = baseSeconds * Math.Pow(2, Math.Max(0, level - 1));
        var capped = Math.Min(raw, maxSeconds);

        // ±25% jitter
        var jitterFactor = 1.0 + (_random.NextDouble() * 0.5 - 0.25);
        return capped * jitterFactor;
    }

    private sealed class BackoffEntry
    {
        public int Level;
        public int ConsecutiveRiskControls;
        public DateTimeOffset LastRiskControlAt;
        public DateTimeOffset? CooldownUntil;
    }
}
