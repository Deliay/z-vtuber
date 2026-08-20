using System.Net;

namespace Z.Vtuber.Crawlers;

public sealed class RiskControlHandler : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var response = await base.SendAsync(request, cancellationToken);

        var contentType = response.Content.Headers.ContentType?.MediaType;
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        var result = RiskControlClassifier.Classify(response.StatusCode, contentType, body);

        if (result.IsMatch)
        {
            var target = request.RequestUri?.ToString() ?? "unknown";
            throw new RiskControlException(
                $"Risk control detected on {target} (signal={result.Signal})",
                target,
                result.CookieTag,
                result.RetryAfter);
        }

        return response;
    }
}
