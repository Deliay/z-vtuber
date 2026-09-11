using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Z.Vtuber.Notifier.Satori;

public sealed class SatoriApiClient : ISatoriApiClient
{
    private readonly HttpClient _httpClient;
    private readonly SatoriOptions _options;
    private readonly ILogger<SatoriApiClient> _logger;
    private LoginInfo? _cachedLoginInfo;
    private string? _platform;
    private string? _selfId;

    public SatoriApiClient(
        HttpClient httpClient,
        IOptions<SatoriOptions> options,
        ILogger<SatoriApiClient> logger)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _logger = logger;

        _httpClient.BaseAddress = new Uri(_options.Endpoint);
    }

    public async Task<LoginInfo> LoginGetAsync(CancellationToken ct = default)
    {
        if (_cachedLoginInfo is not null)
        {
            return _cachedLoginInfo;
        }

        try
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "/v1/login.get");
            if (!string.IsNullOrWhiteSpace(_options.Token))
            {
                request.Headers.Add("Authorization", $"Bearer {_options.Token}");
            }

            var response = await _httpClient.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();

            // Satori 协议直接返回 Login 对象，不是 { "data": Login }
            var loginInfo = await response.Content.ReadFromJsonAsync<LoginInfo>(cancellationToken: ct);
            if (loginInfo is null)
            {
                throw new InvalidOperationException("login.get returned null");
            }

            // status 是数值枚举：OFFLINE=0, ONLINE=1
            if (loginInfo.Status != 1) // ONLINE = 1
            {
                throw new InvalidOperationException($"login.get returned status {loginInfo.Status}, expected ONLINE (1)");
            }

            _cachedLoginInfo = loginInfo;
            _platform = loginInfo.Platform;
            _selfId = loginInfo.User.Id;

            _logger.LogInformation("Login discovery successful: platform={Platform}, selfId={SelfId}", _platform, _selfId);
            return _cachedLoginInfo;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Login discovery failed");
            throw;
        }
    }

    public string? GetCachedPlatform() => _platform;
    public string? GetCachedSelfId() => _selfId;

    public async Task<IReadOnlyList<Guild>> GetGuildListAsync(CancellationToken ct = default)
    {
        await EnsureLoginInfoAsync(ct);

        var allGuilds = new List<Guild>();
        string? next = null;

        do
        {
            var request = CreateRequest("/v1/guild.list");
            if (next is not null)
            {
                request.Content = JsonContent.Create(new { next });
            }

            var response = await _httpClient.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<GuildListResponse>(cancellationToken: ct);
            if (result?.Data is not null)
            {
                allGuilds.AddRange(result.Data);
            }

            next = result?.Next;
        } while (next is not null);

        return allGuilds;
    }

    public async Task<IReadOnlyList<Channel>> GetChannelListAsync(string guildId, CancellationToken ct = default)
    {
        await EnsureLoginInfoAsync(ct);

        var allChannels = new List<Channel>();
        string? next = null;

        do
        {
            var request = CreateRequest("/v1/channel.list");
            object body;
            if (next is not null)
            {
                body = new { guild_id = guildId, next };
            }
            else
            {
                body = new { guild_id = guildId };
            }
            request.Content = JsonContent.Create(body);

            var response = await _httpClient.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<ChannelListResponse>(cancellationToken: ct);
            if (result?.Data is not null)
            {
                allChannels.AddRange(result.Data);
            }

            next = result?.Next;
        } while (next is not null);

        return allChannels;
    }

    public async Task SendMessageAsync(string channelId, string content, CancellationToken ct = default)
    {
        await EnsureLoginInfoAsync(ct);

        var request = CreateRequest("/v1/message.create");
        request.Content = JsonContent.Create(new
        {
            channel_id = channelId,
            content = EscapeXml(content),
        });

        var response = await _httpClient.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
    }

    private HttpRequestMessage CreateRequest(string path)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path);
        if (!string.IsNullOrWhiteSpace(_options.Token))
        {
            request.Headers.Add("Authorization", $"Bearer {_options.Token}");
        }

        if (_platform is not null)
        {
            request.Headers.Add("Satori-Platform", _platform);
        }

        if (_selfId is not null)
        {
            request.Headers.Add("Satori-User-ID", _selfId);
        }

        return request;
    }

    private async Task EnsureLoginInfoAsync(CancellationToken ct)
    {
        if (_platform is not null && _selfId is not null)
        {
            return;
        }

        // 如果 env 已配置，直接使用
        if (!string.IsNullOrWhiteSpace(_options.Platform) && !string.IsNullOrWhiteSpace(_options.SelfId))
        {
            _platform = _options.Platform;
            _selfId = _options.SelfId;
            _logger.LogInformation("Using env-configured platform={Platform}, selfId={SelfId}", _platform, _selfId);
            return;
        }

        // 否则通过 login.get 发现
        await LoginGetAsync(ct);
    }

    private static string EscapeXml(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            switch (c)
            {
                case '&': sb.Append("&amp;"); break;
                case '<': sb.Append("&lt;"); break;
                case '>': sb.Append("&gt;"); break;
                case '"': sb.Append("&quot;"); break;
                default: sb.Append(c); break;
            }
        }
        return sb.ToString();
    }
}

internal sealed class GuildListResponse
{
    public List<Guild>? Data { get; set; }
    public string? Next { get; set; }
}

internal sealed class ChannelListResponse
{
    public List<Channel>? Data { get; set; }
    public string? Next { get; set; }
}
