using Microsoft.Extensions.Logging.Abstractions;
using Z.Vtuber.Configuration;
using Z.Vtuber.Jobs;
using Z.Vtuber.Persistence.Entities;
using Z.Vtuber.Services;

namespace Z.Vtuber.Tests;

public sealed class CookieProbeJobTests
{
    private static BilibiliCrawlerOptions DefaultOptions() => new()
    {
        CookieProbeIntervalMinutes = 5,
        CooldownMinutes = 30,
    };

    private static CookieProbeJob CreateJob(
        ICookiePoolService cookiePool,
        ICookieNavProbeService probe) =>
        new(cookiePool, probe, DefaultOptions(), NullLogger<CookieProbeJob>.Instance);

    private static MockCookiePoolService Pool(params string[] cookies) =>
        new()
        {
            ActiveCookies = cookies
                .Select(c => new CookieRecord
                {
                    Id = MongoDB.Bson.ObjectId.GenerateNewId(),
                    Cookie = c,
                    Status = CookieStatus.Active,
                })
                .ToList(),
        };

    [Fact]
    public async Task RunOnceAsync_ProbesEveryActiveCookie()
    {
        var probe = new FakeCookieNavProbeService();
        var job = CreateJob(Pool("cookie1", "cookie2"), probe);

        await job.RunOnceAsync(CancellationToken.None);

        Assert.Equal(2, probe.CallCount);
    }

    [Fact]
    public async Task RunOnceAsync_NoActiveCookies_DoesNotProbe()
    {
        var probe = new FakeCookieNavProbeService();
        var job = CreateJob(new MockCookiePoolService(), probe);

        await job.RunOnceAsync(CancellationToken.None);

        Assert.Equal(0, probe.CallCount);
    }

    [Fact]
    public async Task RunOnceAsync_OperationCanceledException_Propagates()
    {
        var probe = new FakeCookieNavProbeService
        {
            ProbeFunc = (_, ct) => Task.FromException<CookieProbeOutcome>(new OperationCanceledException(ct)),
        };
        var job = CreateJob(Pool("cookie1"), probe);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => job.RunOnceAsync(CancellationToken.None));
    }

    [Fact]
    public async Task RunOnceAsync_MixedOutcomes_DoesNotThrow()
    {
        var outcomes = new[]
        {
            CookieProbeOutcome.Success,
            CookieProbeOutcome.Cooldown,
            CookieProbeOutcome.Offline,
            CookieProbeOutcome.Backoff,
        };
        var probe = new FakeCookieNavProbeService
        {
            ProbeFunc = (_, _) => Task.FromResult(outcomes[Random.Shared.Next(outcomes.Length)]),
        };
        var job = CreateJob(Pool("cookie1", "cookie2"), probe);

        await job.RunOnceAsync(CancellationToken.None);

        Assert.Equal(2, probe.CallCount);
    }
}