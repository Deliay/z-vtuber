using System.Net;
using System.Text.Json;
using Z.Vtuber.Crawlers;

namespace Z.Vtuber.Tests;

public class RiskControlClassifierTests
{
    private static RiskControlResult Classify(
        HttpStatusCode statusCode,
        string? contentType,
        string? body)
    {
        return RiskControlClassifier.Classify(statusCode, contentType, body);
    }

    [Fact]
    public void Http200_WithNullBody_IsNotMatch()
    {
        var result = Classify(HttpStatusCode.OK, "application/json", null);
        Assert.False(result.IsMatch);
    }

    [Fact]
    public void Http412_WithHtmlBody_IsMatch()
    {
        var result = Classify(HttpStatusCode.PreconditionFailed, "text/html", "<html></html>");
        Assert.True(result.IsMatch);
        Assert.Equal(RiskSignal.Http412, result.Signal);
    }

    [Fact]
    public void JsonCodeMinus412_IsMatch()
    {
        var body = JsonSerializer.Serialize(new { code = -412, message = "安全风控" });
        var result = Classify(HttpStatusCode.OK, "application/json", body);
        Assert.True(result.IsMatch);
        Assert.Equal(RiskSignal.JsonCodeMinus412, result.Signal);
    }

    [Fact]
    public void HtmlWithRiskControlKeyword_IsMatch()
    {
        var body = "<html><body><h1>安全风控</h1><p>错误号: 412</p></body></html>";
        var result = Classify(HttpStatusCode.OK, "text/html", body);
        Assert.True(result.IsMatch);
        Assert.Equal(RiskSignal.HtmlRiskControlPage, result.Signal);
    }

    [Fact]
    public void HtmlWithSafetyKeywordOnly_IsMatch()
    {
        var body = "<html><body>安全风控 - 请稍后再试</body></html>";
        var result = Classify(HttpStatusCode.OK, "text/html", body);
        Assert.True(result.IsMatch);
        Assert.Equal(RiskSignal.HtmlRiskControlPage, result.Signal);
    }

    [Fact]
    public void HtmlWithError412Only_IsMatch()
    {
        var body = "<html><body>错误号: 412</body></html>";
        var result = Classify(HttpStatusCode.OK, "text/html", body);
        Assert.True(result.IsMatch);
        Assert.Equal(RiskSignal.HtmlRiskControlPage, result.Signal);
    }

    [Fact]
    public void Http200_NormalJson_IsNotMatch()
    {
        var body = JsonSerializer.Serialize(new { code = 0, message = "ok" });
        var result = Classify(HttpStatusCode.OK, "application/json", body);
        Assert.False(result.IsMatch);
    }

    [Fact]
    public void Http404_IsNotMatch()
    {
        var result = Classify(HttpStatusCode.NotFound, "application/json", null);
        Assert.False(result.IsMatch);
    }

    [Fact]
    public void Http500_IsNotMatch()
    {
        var result = Classify(HttpStatusCode.InternalServerError, "text/html", "Server Error");
        Assert.False(result.IsMatch);
    }

    [Fact]
    public void BusinessCodeMinus403_IsNotMatch()
    {
        var body = JsonSerializer.Serialize(new { code = -403, message = "forbidden" });
        var result = Classify(HttpStatusCode.OK, "application/json", body);
        Assert.False(result.IsMatch);
    }

    [Fact]
    public void NullBody_IsNotMatch()
    {
        var result = Classify(HttpStatusCode.OK, "application/json", null);
        Assert.False(result.IsMatch);
    }

    [Fact]
    public void EmptyBody_IsNotMatch()
    {
        var result = Classify(HttpStatusCode.OK, "application/json", "");
        Assert.False(result.IsMatch);
    }

    [Fact]
    public void MalformedJson_IsNotMatch()
    {
        var result = Classify(HttpStatusCode.OK, "application/json", "{broken json");
        Assert.False(result.IsMatch);
    }

    [Fact]
    public void Http412_WithNullContentType_IsMatch()
    {
        var result = Classify(HttpStatusCode.PreconditionFailed, null, null);
        Assert.True(result.IsMatch);
        Assert.Equal(RiskSignal.Http412, result.Signal);
    }

    [Fact]
    public void Http412_WithJsonBody_CodeNotMinus412_IsMatch()
    {
        var body = JsonSerializer.Serialize(new { code = 0, message = "ok" });
        var result = Classify(HttpStatusCode.PreconditionFailed, "application/json", body);
        Assert.True(result.IsMatch);
        Assert.Equal(RiskSignal.Http412, result.Signal);
    }

    [Fact]
    public void RetryAfter_FromHtmlBody_IsParsed()
    {
        var body = "<html><body>安全风控</body></html>";
        var result = Classify(HttpStatusCode.OK, "text/html", body);
        Assert.Null(result.RetryAfter);
    }

    [Fact]
    public void CookieTag_IsNullByDefault()
    {
        var result = Classify(HttpStatusCode.PreconditionFailed, null, null);
        Assert.Null(result.CookieTag);
    }
}
