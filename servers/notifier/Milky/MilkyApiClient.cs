using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Milky.Net.Client;

namespace Z.Vtuber.Notifier.Milky;

public sealed class MilkyApiClient : IMilkyApiClient
{
    private readonly HttpClient _httpClient;
    private readonly MilkyOptions _options;
    private readonly ILogger<MilkyApiClient> _logger;

    public MilkyApiClient(
        HttpClient httpClient,
        IOptions<MilkyOptions> options,
        ILogger<MilkyApiClient> logger)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _logger = logger;

        _httpClient.BaseAddress = new Uri(_options.Endpoint);
        if (!string.IsNullOrWhiteSpace(_options.AccessToken))
        {
            _httpClient.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _options.AccessToken);
        }
    }

    public async Task<LoginInfo> GetLoginInfoAsync(CancellationToken ct = default)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/get_login_info");
        var response = await _httpClient.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
        var data = json.GetProperty("data");
        return new LoginInfo
        {
            UserId = data.GetProperty("user_id").GetInt64(),
            Nickname = data.TryGetProperty("nickname", out var nickname) ? nickname.GetString() : null,
        };
    }

    public async Task<IReadOnlyList<GroupInfo>> GetGroupListAsync(CancellationToken ct = default)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/get_group_list");
        var response = await _httpClient.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
        var data = json.GetProperty("data");
        var groups = new List<GroupInfo>();
        foreach (var item in data.EnumerateArray())
        {
            groups.Add(new GroupInfo
            {
                GroupId = item.GetProperty("group_id").GetInt64(),
                GroupName = item.TryGetProperty("group_name", out var groupName) ? groupName.GetString() : null,
            });
        }
        return groups;
    }

    public async Task SendGroupMessageAsync(long groupId, string text, CancellationToken ct = default)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/send_group_message");
        var body = new
        {
            group_id = groupId,
            message = new[]
            {
                new
                {
                    type = "text",
                    data = new { text }
                }
            }
        };
        request.Content = JsonContent.Create(body);
        var response = await _httpClient.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
    }
}