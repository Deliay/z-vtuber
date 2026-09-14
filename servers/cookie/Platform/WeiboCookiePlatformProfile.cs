using System.Globalization;
using System.Text.Json;

namespace Z.Vtuber.Cookie.Platform;

/// <summary>
/// 微博平台差异：passport.weibo.com 扫码登录，
/// 登录态以 m.weibo.cn/api/config 的 data.login == true 为准，
/// 身份取该响应的 data.uid / 昵称，cookie 域为 m.weibo.cn（核心 SUB/SUBP）。
/// </summary>
public sealed class WeiboCookiePlatformProfile : ICookiePlatformProfile
{
    public string Platform => "weibo";
    public string LoginUrl => "https://passport.weibo.com/";
    public string QrCodeSelector => "img.qrcode";
    public string HomeUrl => "https://m.weibo.cn";
    public string CookieDomainUrl => "https://m.weibo.cn";
    public string? LoginStateObservedUrlFragment => null;
    public string? LoginStateProbeUrl => "https://m.weibo.cn/api/config";
    public string LockKeyPrefix => "weibo:";
    public IReadOnlyList<string> RequiredCookieNames => ["SUB", "SUBP"];

    public bool IsLoggedIn(string responseBody)
    {
        if (string.IsNullOrWhiteSpace(responseBody))
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(responseBody);
            if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            if (!data.TryGetProperty("login", out var login))
            {
                return false;
            }

            return login.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.String => bool.TryParse(login.GetString(), out var parsed) && parsed,
                _ => false,
            };
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public IdentityExtractionResult ExtractIdentity(string responseBody)
    {
        if (string.IsNullOrWhiteSpace(responseBody))
        {
            return new IdentityExtractionResult();
        }

        try
        {
            using var doc = JsonDocument.Parse(responseBody);
            if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
            {
                return new IdentityExtractionResult();
            }

            return new IdentityExtractionResult
            {
                Mid = ReadLong(data, "uid"),
                Uname = ReadString(data, "screen_name") ?? ReadString(data, "nickname") ?? ReadString(data, "name"),
            };
        }
        catch (JsonException)
        {
            return new IdentityExtractionResult();
        }
    }

    private static long? ReadLong(JsonElement obj, string property)
    {
        if (!obj.TryGetProperty(property, out var element))
        {
            return null;
        }

        return element.ValueKind switch
        {
            JsonValueKind.Number when element.TryGetInt64(out var number) => number,
            JsonValueKind.String when long.TryParse(element.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => null,
        };
    }

    private static string? ReadString(JsonElement obj, string property)
        => obj.TryGetProperty(property, out var element) && element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : null;
}
