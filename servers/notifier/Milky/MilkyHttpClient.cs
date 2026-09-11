using System.Net.Http.Headers;
using System.Text;

namespace Z.Vtuber.Notifier.Milky;

public class MilkyHttpClient : HttpClient
{
    public MilkyHttpClient(MilkyOptions options)
    {
        BaseAddress = new Uri(options.Endpoint);
        if (!string.IsNullOrWhiteSpace(options.AccessToken))
        {
            DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", options.AccessToken);
        }
    }

    internal MilkyHttpClient(HttpMessageHandler handler, MilkyOptions options)
        : base(handler)
    {
        BaseAddress = new Uri(options.Endpoint);
        if (!string.IsNullOrWhiteSpace(options.AccessToken))
        {
            DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", options.AccessToken);
        }
    }

    private static void HandleMessageContentType(HttpRequestMessage request)
    {
        request.Content ??= new StringContent("", Encoding.UTF8, "application/json");
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
    }

    public override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        HandleMessageContentType(request);
        return base.Send(request, cancellationToken);
    }

    public override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        HandleMessageContentType(request);
        return base.SendAsync(request, cancellationToken);
    }
}
