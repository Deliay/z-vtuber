using Z.Vtuber.Cookie.Browser;

namespace Z.Vtuber.Cookie.Platform;

public sealed class WeiboCookiePlatformProfile : ICookiePlatformProfile
{
    public string Platform => "weibo";
    public string LoginUrl => "https://passport.weibo.com/";
    public string QrCodeSelector => "img.qrcode"; // 需要根据实际页面调整
    public string HomeUrl => "https://m.weibo.cn";
    public string CookieDomainUrl => "https://m.weibo.cn";

    public async Task<bool> IsLoggedInAsync(IBrowserSessionManager sessionManager, string accountId, CancellationToken ct = default)
    {
        // 实际实现需要访问 m.weibo.cn/api/config 并检查 data.login
        // 这里简化为模拟实现
        await Task.Delay(1, ct);
        return false;
    }

    public async Task<IdentityExtractionResult> ExtractIdentityAsync(IBrowserSessionManager sessionManager, string accountId, CancellationToken ct = default)
    {
        // 实际实现需要从 api/config 响应中提取 uid 和昵称
        await Task.Delay(1, ct);
        return new IdentityExtractionResult();
    }
}