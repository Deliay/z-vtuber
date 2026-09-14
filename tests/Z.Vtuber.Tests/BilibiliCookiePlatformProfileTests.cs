using Z.Vtuber.Cookie.Platform;

namespace Z.Vtuber.Tests;

public class BilibiliCookiePlatformProfileTests
{
    private readonly BilibiliCookiePlatformProfile _profile = new();

    [Fact]
    public void Config_IsBilibili()
    {
        Assert.Equal("bilibili", _profile.Platform);
        Assert.Equal("https://passport.bilibili.com/login", _profile.LoginUrl);
        Assert.Equal("/x/web-interface/nav", _profile.LoginStateObservedUrlFragment);
        Assert.Null(_profile.LoginStateProbeUrl);
        Assert.Equal(string.Empty, _profile.LockKeyPrefix);
    }

    [Theory]
    [InlineData("""{"code":0,"message":"0","ttl":1,"data":{"isLogin":true,"mid":123,"uname":"用户"}}""", true)]
    [InlineData("""{"code":-101,"message":"账号未登录","ttl":1,"data":{"isLogin":false,"wbi_img":{}}}""", false)]
    [InlineData("""{"code":0,"data":{}}""", false)]
    [InlineData("""{"isLogin":true}""", false)]
    [InlineData("", false)]
    [InlineData("not-json", false)]
    public void IsLoggedIn_ReadsDataIsLogin(string body, bool expected)
    {
        Assert.Equal(expected, _profile.IsLoggedIn(body));
    }

    [Fact]
    public void ExtractIdentity_ReadsMidAndUname()
    {
        var identity = _profile.ExtractIdentity("""{"code":0,"data":{"mid":123456,"uname":"测试用户"}}""");

        Assert.Equal(123456L, identity.Mid);
        Assert.Equal("测试用户", identity.Uname);
    }

    [Fact]
    public void ExtractIdentity_MissingFields_ReturnsNulls()
    {
        var identity = _profile.ExtractIdentity("""{"code":-101,"data":{"isLogin":false}}""");

        Assert.Null(identity.Mid);
        Assert.Null(identity.Uname);
    }
}
