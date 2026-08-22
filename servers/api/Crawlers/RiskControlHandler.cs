using System.Net;

namespace Z.Vtuber.Crawlers;

public sealed class RiskControlHandler : DelegatingHandler
{
    public RiskControlHandler(HttpMessageHandler innerHandler)
        : base(innerHandler)
    {
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        // WebSocket 握手（弹幕长连）直接放行：ReadAsStringAsync 会把响应内容缓冲成只读
        // MemoryStream，导致 ClientWebSocket 拿不到可写的原始连接流而握手失败
        if (IsWebSocketUpgrade(request))
        {
            return await base.SendAsync(request, cancellationToken);
        }

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

    private static bool IsWebSocketUpgrade(HttpRequestMessage request)
        => request.Headers.TryGetValues("Upgrade", out var values)
            && values.Any(value => value.Equals("websocket", StringComparison.OrdinalIgnoreCase));
}
