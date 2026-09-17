using Microsoft.Extensions.Options;

namespace Z.Vtuber.Cookie.Configuration;

/// <summary>
/// <see cref="CookieServiceOptions"/> 校验：<c>BrowserSessionDataDir</c> 必填，
/// <c>PageIdleCloseDelaySeconds</c> 必须大于 0。
/// </summary>
public sealed class CookieServiceOptionsValidator : IValidateOptions<CookieServiceOptions>
{
    public ValidateOptionsResult Validate(string? name, CookieServiceOptions options)
    {
        var failures = new List<string>();

        if (string.IsNullOrWhiteSpace(options.BrowserSessionDataDir))
        {
            failures.Add("缺少必需配置 BrowserSessionDataDir");
        }

        if (options.PageIdleCloseDelaySeconds <= 0)
        {
            failures.Add("PageIdleCloseDelaySeconds 必须大于 0");
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }
}
