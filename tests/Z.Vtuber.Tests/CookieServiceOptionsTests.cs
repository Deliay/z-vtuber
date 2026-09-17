using Z.Vtuber.Cookie.Configuration;

namespace Z.Vtuber.Tests;

public sealed class CookieServiceOptionsTests
{
    [Fact]
    public void PageIdleCloseDelaySeconds_DefaultsTo60()
    {
        var options = new CookieServiceOptions();

        Assert.Equal(60, options.PageIdleCloseDelaySeconds);
    }

    [Fact]
    public void Validator_SucceedsWithDefaults()
    {
        var result = Validate(new CookieServiceOptions { BrowserSessionDataDir = "/data/browser-sessions" });

        Assert.True(result.Succeeded);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-60)]
    public void Validator_FailsWhenPageIdleCloseDelayNotPositive(int seconds)
    {
        var result = Validate(new CookieServiceOptions
        {
            BrowserSessionDataDir = "/data/browser-sessions",
            PageIdleCloseDelaySeconds = seconds,
        });

        Assert.False(result.Succeeded);
        Assert.Contains(result.Failures, f => f.Contains("PageIdleCloseDelaySeconds"));
    }

    [Fact]
    public void Validator_FailsWhenBrowserSessionDataDirMissing()
    {
        var result = Validate(new CookieServiceOptions { BrowserSessionDataDir = "  " });

        Assert.False(result.Succeeded);
        Assert.Contains(result.Failures, f => f.Contains("BrowserSessionDataDir"));
    }

    private static Microsoft.Extensions.Options.ValidateOptionsResult Validate(CookieServiceOptions options)
        => new CookieServiceOptionsValidator().Validate(null, options);
}
