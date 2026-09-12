using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.Options;
using Microsoft.Playwright;
using Z.Vtuber.Cookie.Configuration;
using Z.Vtuber.Cookie.Persistence;

namespace Z.Vtuber.Cookie.Browser;

public class BrowserSessionManager : IBrowserSessionManager, IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, IBrowserContext> _contexts = new();
    private readonly ConcurrentDictionary<string, Channel<string>> _navChannels = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<string>> _navResults = new();
    private readonly IBrowser _browser;
    private readonly CookieServiceOptions _options;
    private readonly ILogger<BrowserSessionManager> _logger;

    public BrowserSessionManager(IBrowser browser, IOptions<CookieServiceOptions> options, ILogger<BrowserSessionManager> logger)
    {
        _browser = browser;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<LoginSessionResult> CreateLoginSessionAsync(CancellationToken ct = default)
    {
        var accountId = Guid.NewGuid().ToString("N");
        var userDataDir = Path.Combine(_options.BrowserSessionDataDir, accountId);

        Directory.CreateDirectory(userDataDir);

        var context = await _browser.NewContextAsync(new BrowserNewContextOptions
        {
            StorageStatePath = null,
        });

        var page = await context.NewPageAsync();

        // Navigate to bilibili login page
        await page.GotoAsync("https://passport.bilibili.com/login");

        // Click the QR code login tab if not already selected
        try
        {
            var qrTab = page.Locator("[class*='login-scan-box'], [class*='qrcode']");
            if (await qrTab.CountAsync() > 0)
            {
                await qrTab.First.ClickAsync();
            }
        }
        catch
        {
            // QR code may already be visible
        }

        // Wait for QR code image to appear
        await page.WaitForSelectorAsync("img[class*='qr'], canvas[class*='qr'], [class*='login-scan-box'] img", new PageWaitForSelectorOptions
        {
            Timeout = 10000,
        });

        // Screenshot the QR code area
        var qrElement = await page.QuerySelectorAsync("[class*='login-scan-box'], [class*='qrcode']");
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

        // Set up nav response listener to detect login success
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
                    // Response body may not be available
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
            _logger.LogWarning("Cannot refresh session {AccountId}: no browser context", accountId);
            return;
        }

        var pages = context.Pages;
        if (pages.Count > 0)
        {
            await pages[0].GotoAsync("https://www.bilibili.com", new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });
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
