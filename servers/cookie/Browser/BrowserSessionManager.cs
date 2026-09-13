using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using Microsoft.Playwright;
using Z.Vtuber.Cookie.Configuration;
using Z.Vtuber.Cookie.Platform;

namespace Z.Vtuber.Cookie.Browser;

/// <summary>
/// 基于 Playwright 的浏览器 session 管理，按 <see cref="ICookiePlatformProfile"/> 驱动登录/登录态检测/cookie 提取。
/// </summary>
public class BrowserSessionManager : IBrowserSessionManager, IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, IBrowserContext> _contexts = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<string>> _navResults = new();
    private readonly ICookiePlatformProfile _profile;
    private readonly IBrowserType _browserType;
    private readonly CookieServiceOptions _options;
    private readonly ILogger<BrowserSessionManager> _logger;

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
    }

    public async Task<LoginSessionResult> CreateLoginSessionAsync(string accountId, CancellationToken ct = default)
    {
        var userDataDir = Path.Combine(_options.BrowserSessionDataDir, accountId);

        Directory.CreateDirectory(userDataDir);

        var context = await _browserType.LaunchPersistentContextAsync(userDataDir, new BrowserTypeLaunchPersistentContextOptions
        {
            Headless = true,
        });

        var page = await context.NewPageAsync();

        await page.GotoAsync(_profile.LoginUrl);

        string qrCodeBase64;
        if (!string.IsNullOrEmpty(_profile.QrCodeSelector))
        {
            try
            {
                await page.WaitForSelectorAsync(_profile.QrCodeSelector, new PageWaitForSelectorOptions
                {
                    Timeout = 10000,
                });
            }
            catch (TimeoutException)
            {
                _logger.LogWarning("QR selector {Selector} not found for platform {Platform}; falling back to full page screenshot.",
                    _profile.QrCodeSelector, _profile.Platform);
            }
        }

        var qrElement = string.IsNullOrEmpty(_profile.QrCodeSelector)
            ? null
            : await page.QuerySelectorAsync(_profile.QrCodeSelector);

        if (qrElement != null)
        {
            var screenshot = await qrElement.ScreenshotAsync();
            qrCodeBase64 = Convert.ToBase64String(screenshot);
        }
        else
        {
            var screenshot = await page.ScreenshotAsync();
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

        _contexts[accountId] = context;

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
        if (!_contexts.TryGetValue(accountId, out var context))
        {
            throw new InvalidOperationException($"No browser context for account {accountId}");
        }

        var cookies = await context.CookiesAsync([_profile.CookieDomainUrl]);
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

    public async Task DeleteSessionAsync(string accountId, CancellationToken ct = default)
    {
        if (_navResults.TryRemove(accountId, out var tcs))
        {
            tcs.TrySetCanceled();
        }

        if (_contexts.TryRemove(accountId, out var context))
        {
            await context.CloseAsync();
            await context.DisposeAsync();
        }
    }

    public async Task RefreshSessionAsync(string accountId, CancellationToken ct = default)
    {
        if (_profile.LoginStateProbeUrl is { } probeUrl)
        {
            if (!_contexts.TryGetValue(accountId, out var probeContext))
            {
                throw new InvalidOperationException($"No browser context for account {accountId}");
            }

            string body;
            try
            {
                var response = await probeContext.APIRequest.GetAsync(probeUrl);
                body = await response.TextAsync();
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

        if (!_contexts.TryGetValue(accountId, out var context))
        {
            throw new InvalidOperationException($"No browser context for account {accountId}");
        }

        var pages = context.Pages;
        if (pages.Count == 0)
        {
            throw new InvalidOperationException($"No pages in browser context for account {accountId}");
        }

        var page = pages[0];
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
            await page.GotoAsync(_profile.HomeUrl, new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(_options.RefreshNavTimeoutSeconds));

            var result = await navTcs.Task.WaitAsync(cts.Token);

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

    public async ValueTask DisposeAsync()
    {
        foreach (var kvp in _contexts)
        {
            try
            {
                await kvp.Value.CloseAsync();
                await kvp.Value.DisposeAsync();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error disposing browser context {AccountId}", kvp.Key);
            }
        }

        _contexts.Clear();

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
