using Z.Vtuber.Cookie.Browser;

namespace Z.Vtuber.Cookie.Platform;

public interface ICookiePlatformProfile
{
    string Platform { get; }
    string LoginUrl { get; }
    string QrCodeSelector { get; }
    string HomeUrl { get; }
    string CookieDomainUrl { get; }
    Task<bool> IsLoggedInAsync(IBrowserSessionManager sessionManager, string accountId, CancellationToken ct = default);
    Task<IdentityExtractionResult> ExtractIdentityAsync(IBrowserSessionManager sessionManager, string accountId, CancellationToken ct = default);
}

public record IdentityExtractionResult
{
    public long? Mid { get; init; }
    public string? Uname { get; init; }
}