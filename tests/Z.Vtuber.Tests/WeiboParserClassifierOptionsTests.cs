using System.Text.Json;
using Z.Vtuber.Crawler.Configuration;
using Z.Vtuber.Crawler.Weibo;

namespace Z.Vtuber.Tests;

public sealed class WeiboCrawlerOptionsTests
{
    [Fact]
    public void Options_HaveExpectedDefaults()
    {
        var options = new WeiboCrawlerOptions();

        Assert.Equal(10, options.TimelineIntervalSeconds);
        Assert.Equal(10, options.HttpTimeoutSeconds);
        Assert.Equal(60, options.CookieRefreshIntervalMinutes);
        Assert.Equal(5, options.MaxPagesPerCrawl);
        Assert.Equal(30, options.BackoffBaseSeconds);
        Assert.Equal(15, options.BackoffMaxMinutes);
        Assert.Equal(10, options.CooldownMinutes);
        Assert.Equal(5, options.RiskControlCooldownThreshold);
        Assert.True(options.UseLoginCookie);
        Assert.True(options.LoginCookieRetryOnce);
        Assert.Contains("iPhone", options.UserAgent);
    }
}

public sealed class WeiboMblogParserTests
{
    [Theory]
    [InlineData("Wed Oct 10 00:00:00 +0800 2023", 1696867200)]
    [InlineData("Tue Oct 10 00:00:00 +0800 2023", 1696867200)]
    [InlineData("Sat Mar 04 12:30:45 +0800 2023", 1677904245)]
    public void ParseCreatedAtSeconds_ParsesRubyFormat(string input, long expected)
    {
        var result = WeiboMblogParser.ParseCreatedAtSeconds(input);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not a date")]
    [InlineData("Wednesday Oct 10 2023")]
    public void ParseCreatedAtSeconds_ReturnsNull_OnGarbage(string? input)
    {
        Assert.Null(WeiboMblogParser.ParseCreatedAtSeconds(input));
    }

    [Fact]
    public void ParsePage_ExtractsOnlyMblogCards()
    {
        var root = JsonDocument.Parse("""
            {"ok":1,"data":{"cardlistInfo":{"since_id":"s2","has_more":true},"cards":[
              {"card_type":9,"mblog":{"id":"1","created_at":"Sat Mar 04 12:30:45 +0800 2023","text":"a"}},
              {"card_type":11,"mblog":{"id":"skip","text":"b"}},
              {"card_type":9,"mblog":{"id":"2","created_at":"garbage"}}
            ]}}
            """).RootElement;

        var page = WeiboMblogParser.ParsePage(root);

        Assert.True(page.HasMore);
        Assert.Equal("s2", page.SinceId);
        Assert.Equal(2, page.Items.Count);
        Assert.Equal("1", page.Items[0].MblogId);
        Assert.Equal(1677904245, page.Items[0].PubTs);
        Assert.Equal("2", page.Items[1].MblogId);
        Assert.Null(page.Items[1].PubTs);
    }

    [Fact]
    public void ParsePage_NonCardType_MissingFields_DoesNotThrow()
    {
        var root = JsonDocument.Parse("""{"ok":1,"data":{"cards":[{"card_type":9}]}}""").RootElement;

        var page = WeiboMblogParser.ParsePage(root);

        Assert.Empty(page.Items);
    }

    [Fact]
    public void ParsePage_MissingData_ReturnsEmpty()
    {
        var root = JsonDocument.Parse("""{"ok":0,"msg":"error"}""").RootElement;

        var page = WeiboMblogParser.ParsePage(root);

        Assert.Empty(page.Items);
        Assert.False(page.HasMore);
    }
}

public sealed class WeiboRiskControlClassifierTests
{
    private static readonly WeiboRiskControlClassifier Classifier = new();

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    public void IsRiskControl_True_On401Or403(int status)
    {
        Assert.True(Classifier.IsRiskControl((System.Net.HttpStatusCode)status, "application/json", "{}"));
    }

    [Fact]
    public void IsRiskControl_True_OnRedirectToPassport()
    {
        Assert.True(Classifier.IsRiskControl(
            System.Net.HttpStatusCode.Found, "text/html", "<html></html>", "https://passport.weibo.com/signin/login"));
    }

    [Fact]
    public void IsRiskControl_True_OnRedirectToLoginSina()
    {
        Assert.True(Classifier.IsRiskControl(
            System.Net.HttpStatusCode.Found, "text/html", "<html></html>", "https://login.sina.com.cn/sso/login.php"));
    }

    [Fact]
    public void IsRiskControl_True_OnNonJsonHtml_SuspiciousStatus()
    {
        // 预期 JSON 却返回 HTML 且状态码非 2xx（可疑路径）→ 判风控
        Assert.True(Classifier.IsRiskControl(
            System.Net.HttpStatusCode.InternalServerError, "text/html", "<html>captcha</html>"));
        Assert.True(Classifier.IsRiskControl(
            System.Net.HttpStatusCode.BadGateway, "text/html", "<html>proxy error</html>"));
    }

    [Fact]
    public void IsRiskControl_True_OnOkNotOne()
    {
        Assert.True(Classifier.IsRiskControl(
            System.Net.HttpStatusCode.OK, "application/json", """{"ok":0,"msg":"cookie error"}"""));
    }

    [Fact]
    public void IsRiskControl_True_OnLoginKeywordInBody()
    {
        Assert.True(Classifier.IsRiskControl(
            System.Net.HttpStatusCode.OK, "application/json", """{"result":true,"msg":"请重新登录"}"""));
    }

    [Fact]
    public void IsRiskControl_False_OnNormalResponse()
    {
        Assert.False(Classifier.IsRiskControl(
            System.Net.HttpStatusCode.OK, "application/json", """{"ok":1,"data":{}}"""));
    }

    [Fact]
    public void IsRiskControl_False_OnMblogTextContainingKeywords()
    {
        // 回归：关键词仅在 msg/error 字段判断，不得扫描正文（避免误杀正常微博）
        Assert.False(Classifier.IsRiskControl(
            System.Net.HttpStatusCode.OK,
            "application/json",
            """{"ok":1,"data":{"text":"please login with your auth cookie to continue","user":{"id":1}}}"""));
    }

    [Fact]
    public void IsRiskControl_False_OnNonJsonHtmlSuccessBody()
    {
        // 非 JSON body 不做关键词判断，避免把含 login 等词的正常 HTML 判为风控
        Assert.False(Classifier.IsRiskControl(
            System.Net.HttpStatusCode.OK, "text/html", "<html>login form</html>"));
    }

    [Fact]
    public void IsRiskControl_True_OnMsgKeyword_WithOkOne()
    {
        // ok==1 但 msg 明示登录态失效 → 判风控
        Assert.True(Classifier.IsRiskControl(
            System.Net.HttpStatusCode.OK, "application/json", """{"ok":1,"msg":"cookie 已失效，请重新登录"}"""));
    }

    [Fact]
    public void IsRiskControl_False_OnEmptyBody()
    {
        Assert.False(Classifier.IsRiskControl(System.Net.HttpStatusCode.OK, null, ""));
    }
}
