using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using Microsoft.Playwright;
using Z.Vtuber.Cookie.Configuration;
using Z.Vtuber.Cookie.Platform;

namespace Z.Vtuber.Cookie.Browser;

/// <summary>
/// 基于 Playwright 的浏览器 session 管理，按 <see cref="ICookiePlatformProfile"/> 驱动登录/登录态检测/cookie 提取。
/// context 采用「按需惰性打开 + 用后空闲延时关闭」：登录（pending）阶段保持打开，
/// 刷新/提取 cookie 时按需冷启动（依赖 userDataDir 磁盘持久化恢复登录态），完成后 <c>PageIdleCloseDelaySeconds</c> 内无新使用则关闭。
/// </summary>
public class BrowserSessionManager : IBrowserSessionManager, IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, IBrowserContext> _contexts = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new();
    private readonly ConcurrentDictionary<string, long> _idleVersions = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<string>> _navResults = new();
    private readonly ICookiePlatformProfile _profile;
    private readonly IBrowserType _browserType;
    private readonly CookieServiceOptions _options;
    private readonly ILogger<BrowserSessionManager> _logger;
    private readonly IdleCloseScheduler _idleCloseScheduler;

    public BrowserSessionManager(
        ICookiePlatformProfile profile,
        IBrowserType browserType,
        IOptions<CookieServiceOptions> options,
        ILogger<BrowserSessionManager> logger)
    {
        _profile = profile;
        _browserType = browserType;
        _options = options.Value;
        _logger = logger;
        _idleCloseScheduler = new IdleCloseScheduler(
            TimeSpan.FromSeconds(_options.PageIdleCloseDelaySeconds),
            CloseIdleContextAsync,
            logger);
    }

    public async Task<LoginSessionResult> CreateLoginSessionAsync(string accountId, CancellationToken ct = default)
    {
        var context = await GetOrLaunchContextAsync(accountId).ConfigureAwait(false);

        var page = await context.NewPageAsync().ConfigureAwait(false);

        await page.GotoAsync(_profile.LoginUrl).ConfigureAwait(false);

        string qrCodeBase64;
        if (!string.IsNullOrEmpty(_profile.QrCodeSelector))
        {
            try
            {
                await page.WaitForSelectorAsync(_profile.QrCodeSelector, new PageWaitForSelectorOptions
                {
                    Timeout = 10000,
                }).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                _logger.LogWarning("QR selector {Selector} not found for platform {Platform}; falling back to full page screenshot.",
                    _profile.QrCodeSelector, _profile.Platform);
            }
        }

        var qrElement = string.IsNullOrEmpty(_profile.QrCodeSelector)
            ? null
            : await page.QuerySelectorAsync(_profile.QrCodeSelector).ConfigureAwait(false);

        if (qrElement != null)
        {
            var screenshot = await qrElement.ScreenshotAsync().ConfigureAwait(false);
            qrCodeBase64 = Convert.ToBase64String(screenshot);
        }
        else
        {
            var screenshot = await page.ScreenshotAsync().ConfigureAwait(false);
            qrCodeBase64 = Convert.ToBase64String(screenshot);
        }

        var navTcs = new TaskCompletionSource<string>();
        _navResults[accountId] = navTcs;

        if (_profile.LoginStateObservedUrlFragment is { } fragment)
        {
            page.Response += async (_, response) =>
            {
                if (!response.Url.Contains(fragment, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                try
                {
                    var body = await response.TextAsync();
                    if (_profile.IsLoggedIn(body))
                    {
                        navTcs.TrySetResult(body);
                    }
                }
                catch
                {
                    // 响应读取失败不影响后续登录轮询
                }
            };
        }

        var expiresAt = DateTime.UtcNow.AddSeconds(_options.QrCodeExpiresSeconds);

        _logger.LogInformation("Created login session {AccountId} for platform {Platform}, QR code expires at {ExpiresAt}",
            accountId, _profile.Platform, expiresAt);

        return new LoginSessionResult
        {
            AccountId = accountId,
            QrCodeImage = qrCodeBase64,
            ExpiresAt = expiresAt,
        };
    }

    public Task<string?> WaitForNavResponseAsync(string accountId, TimeSpan timeout, CancellationToken ct = default)
    {
        if (_profile.LoginStateProbeUrl is { } probeUrl)
        {
            return ProbeLoginStateAsync(accountId, probeUrl, timeout, ct);
        }

        if (!_navResults.TryGetValue(accountId, out var tcs))
        {
            return Task.FromResult<string?>(null);
        }

        return WaitForNavInternalAsync(tcs, timeout, ct);
    }

    private async Task<string?> ProbeLoginStateAsync(string accountId, string probeUrl, TimeSpan timeout, CancellationToken ct)
    {
        if (!_contexts.TryGetValue(accountId, out var context))
        {
            return null;
        }

        var deadline = DateTime.UtcNow + timeout;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var response = await context.APIRequest.GetAsync(probeUrl);
                var body = await response.TextAsync();
                if (_profile.IsLoggedIn(body))
                {
                    return body;
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Login state probe failed for session {AccountId}", accountId);
            }

            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                break;
            }

            var delay = remaining < TimeSpan.FromSeconds(2) ? remaining : TimeSpan.FromSeconds(2);
            try
            {
                await Task.Delay(delay, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        return null;
    }

    private async Task<string?> WaitForNavInternalAsync(TaskCompletionSource<string> tcs, TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);

        try
        {
            var result = await tcs.Task.WaitAsync(cts.Token);
            return result;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    public async Task<string> ExtractCookiesAsync(string accountId, CancellationToken ct = default)
    {
        var gate = GetGate(accountId);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _idleCloseScheduler.Cancel(accountId);
            _idleVersions.TryRemove(accountId, out _);

            var context = await GetOrLaunchContextAsync(accountId).ConfigureAwait(false);

            var cookies = await context.CookiesAsync([_profile.CookieDomainUrl]).ConfigureAwait(false);
            var cookieHeader = string.Join("; ", cookies.Select(c => $"{c.Name}={c.Value}"));

            if (_profile.RequiredCookieNames.Count > 0)
            {
                var present = cookies.Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var missing = _profile.RequiredCookieNames.Where(n => !present.Contains(n)).ToList();
                if (missing.Count > 0)
                {
                    _logger.LogWarning("Extracted {Platform} cookie for account {AccountId} is missing required cookies: {Missing}",
                        _profile.Platform, accountId, string.Join(", ", missing));
                }
            }

            return cookieHeader;
        }
        finally
        {
            ScheduleIdleClose(accountId);
            gate.Release();
        }
    }

    public async Task DeleteSessionAsync(string accountId, CancellationToken ct = default)
    {
        if (_navResults.TryRemove(accountId, out var tcs))
        {
            tcs.TrySetCanceled();
        }

        _idleCloseScheduler.Cancel(accountId);
        _idleVersions.TryRemove(accountId, out _);

        var gate = GetGate(accountId);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_contexts.TryRemove(accountId, out var context))
            {
                await CloseContextAsync(accountId, context).ConfigureAwait(false);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task RefreshSessionAsync(string accountId, CancellationToken ct = default)
    {
        var gate = GetGate(accountId);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _idleCloseScheduler.Cancel(accountId);
            _idleVersions.TryRemove(accountId, out _);

            var context = await GetOrLaunchContextAsync(accountId).ConfigureAwait(false);

            if (_profile.LoginStateProbeUrl is { } probeUrl)
            {
                string body;
                try
                {
                    var response = await context.APIRequest.GetAsync(probeUrl).ConfigureAwait(false);
                    body = await response.TextAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    throw new TimeoutException($"Login state check failed for session {accountId}", ex);
                }

                if (!_profile.IsLoggedIn(body))
                {
                    throw new InvalidOperationException($"Session {accountId} is not logged in after refresh");
                }

                return;
            }

            var pages = context.Pages;
            var page = pages.Count > 0 ? pages[0] : await context.NewPageAsync().ConfigureAwait(false);
            var fragment = _profile.LoginStateObservedUrlFragment
                ?? throw new InvalidOperationException($"Platform {_profile.Platform} has no login-state signal configured");

            var navTcs = new TaskCompletionSource<string>();

            EventHandler<IResponse> handler = null!;
            handler = async (_, response) =>
            {
                if (response.Url.Contains(fragment, StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        var body = await response.TextAsync();
                        if (_profile.IsLoggedIn(body))
                        {
                            navTcs.TrySetResult(body);
                        }
                        else
                        {
                            navTcs.TrySetResult(string.Empty);
                        }
                    }
                    catch
                    {
                        navTcs.TrySetResult(string.Empty);
                    }
                }
            };

            page.Response += handler;

            try
            {
                await page.GotoAsync(_profile.HomeUrl, new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle }).ConfigureAwait(false);

                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(_options.RefreshNavTimeoutSeconds));

                var result = await navTcs.Task.WaitAsync(cts.Token).ConfigureAwait(false);

                if (string.IsNullOrEmpty(result))
                {
                    throw new InvalidOperationException($"Session {accountId} is not logged in after refresh");
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException($"Login state check timed out for session {accountId}");
            }
            finally
            {
                page.Response -= handler;
            }
        }
        finally
        {
            ScheduleIdleClose(accountId);
            gate.Release();
        }
    }

    private SemaphoreSlim GetGate(string accountId)
        => _gates.GetOrAdd(accountId, _ => new SemaphoreSlim(1, 1));

    private async Task<IBrowserContext> GetOrLaunchContextAsync(string accountId)
    {
        if (_contexts.TryGetValue(accountId, out var existing))
        {
            return existing;
        }

        var userDataDir = Path.Combine(_options.BrowserSessionDataDir, accountId);
        Directory.CreateDirectory(userDataDir);

        var context = await _browserType.LaunchPersistentContextAsync(userDataDir, new BrowserTypeLaunchPersistentContextOptions
        {
            Headless = true,
        }).ConfigureAwait(false);

        _contexts[accountId] = context;

        _logger.LogDebug("Launched persistent context for {Platform} account {AccountId}", _profile.Platform, accountId);

        return context;
    }

    private void ScheduleIdleClose(string accountId)
    {
        if (_contexts.ContainsKey(accountId))
        {
            _idleVersions[accountId] = _idleCloseScheduler.Touch(accountId);
        }
    }

    private async Task CloseIdleContextAsync(string accountId, long version, CancellationToken ct)
    {
        var gate = GetGate(accountId);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!_idleVersions.TryGetValue(accountId, out var current) || current != version)
            {
                // 计时到期后又被新使用顺延，不再关闭。
                return;
            }

            _idleCloseScheduler.Cancel(accountId);
            _idleVersions.TryRemove(accountId, out _);

            if (_contexts.TryRemove(accountId, out var context))
            {
                _logger.LogInformation("Closing idle browser context for {Platform} account {AccountId}", _profile.Platform, accountId);
                await CloseContextAsync(accountId, context).ConfigureAwait(false);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task CloseContextAsync(string accountId, IBrowserContext context)
    {
        try
        {
            await context.CloseAsync().ConfigureAwait(false);
            await context.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error closing browser context {AccountId}", accountId);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _idleCloseScheduler.DisposeAsync().ConfigureAwait(false);

        foreach (var kvp in _contexts)
        {
            if (_contexts.TryRemove(kvp.Key, out var context))
            {
                await CloseContextAsync(kvp.Key, context).ConfigureAwait(false);
            }
        }

        foreach (var kvp in _navResults)
        {
            kvp.Value.TrySetCanceled();
        }

        _navResults.Clear();
    }
}

public class LoginSessionResult
{
    public string AccountId { get; set; } = string.Empty;
    public string QrCodeImage { get; set; } = string.Empty;
    public DateTime ExpiresAt { get; set; }
}
