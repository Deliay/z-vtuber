using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.Options;
using Microsoft.Playwright;
using Z.Vtuber.Cookie.Configuration;

namespace Z.Vtuber.Cookie.Browser;

public class BrowserSessionManager : IBrowserSessionManager, IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, IBrowserContext> _contexts = new();
    private readonly ConcurrentDictionary<string, Channel<string>> _navChannels = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<string>> _navResults = new();
    private readonly IBrowserType _browserType;
    private readonly CookieServiceOptions _options;
    private readonly ILogger<BrowserSessionManager> _logger;

    public BrowserSessionManager(IBrowserType browserType, IOptions<CookieServiceOptions> options, ILogger<BrowserSessionManager> logger)
    {
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

        await page.GotoAsync("https://passport.bilibili.com/login");

        await page.WaitForSelectorAsync("[class*='login-scan__qrcode'] img", new PageWaitForSelectorOptions
        {
            Timeout = 10000,
        });

        var qrElement = await page.QuerySelectorAsync("[class*='login-scan__qrcode']");
        string qrCodeBase64;
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

        page.Response += async (_, response) =>
        {
            if (response.Url.Contains("/x/web-interface/nav"))
            {
                try
                {
                    var body = await response.TextAsync();
                    if (body.Contains("\"isLogin\":true") || body.Contains("\"isLogin\": true"))
                    {
                        navTcs.TrySetResult(body);
                    }
                }
                catch
                {
                }
            }
        };

        _contexts[accountId] = context;

        var expiresAt = DateTime.UtcNow.AddSeconds(_options.QrCodeExpiresSeconds);

        _logger.LogInformation("Created login session {AccountId}, QR code expires at {ExpiresAt}", accountId, expiresAt);

        return new LoginSessionResult
        {
            AccountId = accountId,
            QrCodeImage = qrCodeBase64,
            ExpiresAt = expiresAt,
        };
    }

    public Task<string?> WaitForNavResponseAsync(string accountId, TimeSpan timeout, CancellationToken ct = default)
    {
        if (!_navResults.TryGetValue(accountId, out var tcs))
        {
            return Task.FromResult<string?>(null);
        }

        return WaitForNavInternalAsync(tcs, timeout, ct);
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

        var cookies = await context.CookiesAsync(["https://www.bilibili.com"]);
        var cookieHeader = string.Join("; ", cookies.Select(c => $"{c.Name}={c.Value}"));
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

        var navTcs = new TaskCompletionSource<string>();

        EventHandler<IResponse> handler = null!;
        handler = async (_, response) =>
        {
            if (response.Url.Contains("/x/web-interface/nav"))
            {
                try
                {
                    var body = await response.TextAsync();
                    if (body.Contains("\"isLogin\":true") || body.Contains("\"isLogin\": true"))
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
            await page.GotoAsync("https://www.bilibili.com", new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });

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
