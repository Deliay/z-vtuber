using Z.Vtuber.Cookie.Platform;

namespace Z.Vtuber.Tests;

public class WeiboCookiePlatformProfileTests
{
    private readonly WeiboCookiePlatformProfile _profile = new();

    [Fact]
    public void Config_IsWeibo()
    {
        Assert.Equal("weibo", _profile.Platform);
        Assert.Equal("https://passport.weibo.com/", _profile.LoginUrl);
        Assert.Equal("img.qrcode", _profile.QrCodeSelector);
        Assert.Equal("https://m.weibo.cn", _profile.CookieDomainUrl);
        Assert.Equal("https://m.weibo.cn", _profile.HomeUrl);
        Assert.Equal("https://m.weibo.cn/api/config", _profile.LoginStateProbeUrl);
        Assert.Null(_profile.LoginStateObservedUrlFragment);
        Assert.Equal("weibo:", _profile.LockKeyPrefix);
        Assert.Equal(["SUB", "SUBP"], _profile.RequiredCookieNames);
    }

    [Theory]
    [InlineData("""{"data":{"login":true,"uid":"12345"}}""", true)]
    [InlineData("""{"data":{"login":false}}""", false)]
    [InlineData("""{"data":{"login":"true"}}""", true)]
    [InlineData("""{"data":{"login":"false"}}""", false)]
    [InlineData("""{"data":{}}""", false)]
    [InlineData("""{"ok":1}""", false)]
    [InlineData("", false)]
    [InlineData("not-json", false)]
    public void IsLoggedIn_ParsesApiConfig(string body, bool expected)
    {
        Assert.Equal(expected, _profile.IsLoggedIn(body));
    }

    [Fact]
    public void ExtractIdentity_ReadsUidAndNickname()
    {
        var identity = _profile.ExtractIdentity("""{"data":{"uid":"12345","screen_name":"微博用户"}}""");

        Assert.Equal(12345L, identity.Mid);
        Assert.Equal("微博用户", identity.Uname);
    }

    [Fact]
    public void ExtractIdentity_ReadsNumericUid()
    {
        var identity = _profile.ExtractIdentity("""{"data":{"uid":67890,"nickname":"昵称"}}""");

        Assert.Equal(67890L, identity.Mid);
        Assert.Equal("昵称", identity.Uname);
    }

    [Fact]
    public void ExtractIdentity_MissingFields_ReturnsNulls()
    {
        var identity = _profile.ExtractIdentity("""{"data":{"login":true}}""");

        Assert.Null(identity.Mid);
        Assert.Null(identity.Uname);
    }

    [Fact]
    public void ExtractIdentity_NonNumericUid_ReturnsNullMid()
    {
        var identity = _profile.ExtractIdentity("""{"data":{"uid":"not-a-number"}}""");

        Assert.Null(identity.Mid);
    }
}
