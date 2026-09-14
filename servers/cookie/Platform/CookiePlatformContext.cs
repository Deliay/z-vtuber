using Z.Vtuber.Common.Persistence;
using Z.Vtuber.Common.Services;
using Z.Vtuber.Cookie.Browser;
using Z.Vtuber.Cookie.Refresh;

namespace Z.Vtuber.Cookie.Platform;

/// <summary>某一平台下平台隔离服务的集合（供后台任务覆盖双平台使用）。</summary>
public sealed class CookiePlatformContext
{
    public required ICookiePlatformProfile Profile { get; init; }
    public required ICookieSessionRepository SessionRepository { get; init; }
    public required ICookiePoolService CookiePool { get; init; }
    public required IBrowserSessionManager BrowserSessionManager { get; init; }
    public required ICookieInvalidEventPublisher EventPublisher { get; init; }

    public string Platform => Profile.Platform;
}

/// <summary>平台上下文注册表，供后台任务覆盖所有已配置平台。</summary>
public sealed class CookiePlatformRegistry
{
    public CookiePlatformRegistry(params CookiePlatformContext[] contexts)
    {
        All = contexts;
    }

    public IReadOnlyList<CookiePlatformContext> All { get; }

    public CookiePlatformContext Get(string platform)
        => All.FirstOrDefault(c => string.Equals(c.Platform, platform, StringComparison.Ordinal))
            ?? throw new InvalidOperationException($"Unknown cookie platform '{platform}'.");
}
