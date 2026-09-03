using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mikibot.Crawler.Http.Bilibili.Model;
using Z.Vtuber.Crawler.Configuration;
using Z.Vtuber.Common.Services;

namespace Z.Vtuber.Crawler.Crawlers;

public sealed class ResilientBiliApiClient : IBiliApiClient
{
    private readonly IBiliApiClient _direct;
    private readonly IBiliApiClient _browser;
    private readonly BilibiliCrawlerOptions _options;
    private readonly ICrawlBackoffRegistry _backoff;
    private readonly ICookiePoolService _cookiePool;
    private readonly ILogger<ResilientBiliApiClient> _logger;
    private readonly ConcurrentDictionary<string, ChannelState> _channelStates = new();

    public ResilientBiliApiClient(
        IBiliApiClient direct,
        IBiliApiClient browser,
        BilibiliCrawlerOptions options,
        ICookiePoolService cookiePool,
        ICrawlBackoffRegistry backoff,
        ILogger<ResilientBiliApiClient> logger)
    {
        _direct = direct;
        _browser = browser;
        _options = options;
        _backoff = backoff;
        _cookiePool = cookiePool;
        _logger = logger;
    }

    public Task<LiveRoomInfo> GetLiveRoomInfoAsync(string cookie, long roomId, CancellationToken ct = default)
        => ExecuteWithResilienceAsync(cookie, $"live:{roomId}", c => _direct.GetLiveRoomInfoAsync(c, roomId, ct),
            c => _browser.GetLiveRoomInfoAsync(c, roomId, ct));

    public Task<NavInfo> GetNavInfoV2Async(string cookie, CancellationToken ct = default)
        => ExecuteWithResilienceAsync(cookie, $"nav:{ShortHash(cookie)}", c => _direct.GetNavInfoV2Async(c, ct),
            c => _browser.GetNavInfoV2Async(c, ct));

    public Task<SpaceDynamicsPage> GetSpaceDynamicsAsync(string cookie, long mid, string offset, CancellationToken ct = default)
        => ExecuteWithResilienceAsync(cookie, $"space:{mid}", c => _direct.GetSpaceDynamicsAsync(c, mid, offset, ct),
            c => _browser.GetSpaceDynamicsAsync(c, mid, offset, ct));

    public Task<GuardInfo> GetRoomGuardListAsync(string cookie, long roomId, long bId, int page = 1, CancellationToken ct = default)
        => ExecuteWithResilienceAsync(cookie, $"guard:{roomId}", c => _direct.GetRoomGuardListAsync(c, roomId, bId, page, ct),
            c => _browser.GetRoomGuardListAsync(c, roomId, bId, page, ct));

    public Task<JsonElement> GetAccountInfoDataAsync(string cookie, long mid, CancellationToken ct = default)
        => _direct.GetAccountInfoDataAsync(cookie, mid, ct);

    private async Task<T> ExecuteWithResilienceAsync<T>(
        string cookie,
        string target,
        Func<string, Task<T>> directFunc,
        Func<string, Task<T>> browserFunc)
    {
        if (!_backoff.TryAcquire(target, out var wait))
        {
            _logger.LogDebug("Backoff active for {Target}, wait {Wait}", target, wait);
            throw new CrawlBackoffActiveException($"Backoff active for {target}", wait);
        }

        switch (_options.FetchMode)
        {
            case BiliFetchMode.Direct:
                try
                {
                    var result = await directFunc(cookie);
                    _backoff.ReportSuccess(target);
                    _logger.LogDebug("Direct call succeeded for {Target}", target);
                    return result;
                }
                catch (RiskControlException ex)
                {
                    _logger.LogWarning(ex, "Risk control on direct channel for {Target}", target);
                    _ = ReportRiskControl(target, cookie, ex);
                    throw;
                }

            case BiliFetchMode.Browser:
                try
                {
                    var result = await browserFunc(cookie);
                    _backoff.ReportSuccess(target);
                    _logger.LogDebug("Browser call succeeded for {Target}", target);
                    return result;
                }
                catch (RiskControlException ex)
                {
                    _logger.LogWarning(ex, "Risk control on browser channel for {Target}", target);
                    _ = ReportRiskControl(target, cookie, ex);
                    throw;
                }

            case BiliFetchMode.Auto:
            default:
                return await ExecuteAutoAsync(target, cookie, directFunc, browserFunc);
        }
    }

    private async Task<T> ExecuteAutoAsync<T>(
        string target,
        string cookie,
        Func<string, Task<T>> directFunc,
        Func<string, Task<T>> browserFunc)
    {
        var state = _channelStates.GetOrAdd(target, _ => new ChannelState());

        if (state.Channel == Channel.Browser)
        {
            _logger.LogDebug("Using browser channel for {Target} (recovery {Count}/{Threshold})",
                target, state.ConsecutiveBrowserSuccesses, _options.ChannelRecoverSuccessCount);
            return await ExecuteBrowserWithRecoveryAsync(target, cookie, browserFunc, state);
        }

        try
        {
            var result = await directFunc(cookie);
            _backoff.ReportSuccess(target);
            state.ConsecutiveBrowserSuccesses = 0;
            _logger.LogDebug("Direct call succeeded for {Target}", target);
            return result;
        }
        catch (RiskControlException ex)
        {
            _logger.LogWarning(ex, "Risk control on direct channel for {Target}, degrading to browser", target);
            _ = ReportRiskControl(target, cookie, ex);
            state.Channel = Channel.Browser;
            state.ConsecutiveBrowserSuccesses = 0;
            return await ExecuteBrowserWithRecoveryAsync(target, cookie, browserFunc, state);
        }
    }

    private async Task<T> ExecuteBrowserWithRecoveryAsync<T>(
        string target,
        string cookie,
        Func<string, Task<T>> browserFunc,
        ChannelState state)
    {
        try
        {
            var result = await browserFunc(cookie);
            _backoff.ReportSuccess(target);
            state.ConsecutiveBrowserSuccesses++;

            if (state.ConsecutiveBrowserSuccesses >= _options.ChannelRecoverSuccessCount)
            {
                state.Channel = Channel.Direct;
                state.ConsecutiveBrowserSuccesses = 0;
                _logger.LogInformation("Recovered to direct channel for {Target} after {Count} consecutive successes",
                    target, _options.ChannelRecoverSuccessCount);
            }

            return result;
        }
        catch (RiskControlException ex)
        {
            _logger.LogWarning(ex, "Risk control on browser channel for {Target}", target);
            _ = ReportRiskControl(target, cookie, ex);
            throw;
        }
    }

    private async Task ReportRiskControl(string target, string cookie, RiskControlException ex)
    {
        _backoff.ReportRiskControl(target);
        try
        {
            await _cookiePool.MarkCooldownAsync(cookie, TimeSpan.FromMinutes(_options.CooldownMinutes));
        }
        catch (Exception ex2)
        {
            _logger.LogError(ex2, "Failed to mark cookie cooldown for {Target}", target);
        }
        _logger.LogWarning("Risk control reported for {Target}, cookie {CookieTag} marked cooldown {Minutes}min",
            target, ShortHash(cookie), _options.CooldownMinutes);
    }

    private static string ShortHash(string cookie)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(cookie));
        return Convert.ToHexString(bytes[..4]).ToLowerInvariant();
    }

    private enum Channel { Direct, Browser }

    private sealed class ChannelState
    {
        public Channel Channel = Channel.Direct;
        public int ConsecutiveBrowserSuccesses;
    }
}
