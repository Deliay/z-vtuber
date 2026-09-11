using Microsoft.Extensions.Options;
using Milky.Net.Client;
using Milky.Net.Model;

namespace Z.Vtuber.Notifier.Milky;

public sealed class MilkyApiClient : IMilkyApiClient
{
    private readonly MilkyClient _milky;
    private readonly ILogger<MilkyApiClient> _logger;

    public MilkyApiClient(
        MilkyHttpClient httpClient,
        ILogger<MilkyApiClient> logger)
    {
        _logger = logger;
        _milky = new MilkyClient(httpClient);
    }

    public async Task<LoginInfo> GetLoginInfoAsync(CancellationToken ct = default)
    {
        try
        {
            var result = await _milky.System.GetLoginInfoAsync(ct);
            return new LoginInfo
            {
                UserId = result.Uin,
                Nickname = result.Nickname,
            };
        }
        catch (MilkyException ex)
        {
            _logger.LogError(ex, "Failed to get login info from Milky");
            throw;
        }
    }

    public async Task<IReadOnlyList<GroupInfo>> GetGroupListAsync(CancellationToken ct = default)
    {
        try
        {
            var result = await _milky.System.GetGroupListAsync(new GetGroupListRequest(), ct);
            return result.Groups.Select(g => new GroupInfo
            {
                GroupId = g.GroupId,
                GroupName = g.GroupName,
            }).ToList();
        }
        catch (MilkyException ex)
        {
            _logger.LogError(ex, "Failed to get group list from Milky");
            throw;
        }
    }

    public async Task SendGroupMessageAsync(long groupId, string text, CancellationToken ct = default)
    {
        try
        {
            var request = new SendGroupMessageRequest(groupId, new TextOutgoingSegment[]
            {
                new(text),
            });
            await _milky.Message.SendGroupMessageAsync(request, ct);
        }
        catch (MilkyException ex)
        {
            _logger.LogError(ex, "Failed to send group message to Milky. GroupId: {GroupId}", groupId);
            throw;
        }
    }
}
