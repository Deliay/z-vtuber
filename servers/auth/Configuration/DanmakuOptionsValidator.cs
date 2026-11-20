using Microsoft.Extensions.Options;

namespace Z.Vtuber.Auth.Configuration;

public sealed class DanmakuOptionsValidator : IValidateOptions<DanmakuOptions>
{
    public ValidateOptionsResult Validate(string? name, DanmakuOptions options)
    {
        var errors = new List<string>();

        if (options.RoomIds.Length == 0)
        {
            errors.Add("Auth:Danmaku:RoomIds must contain at least one room.");
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var roomId in options.RoomIds)
        {
            if (string.IsNullOrWhiteSpace(roomId) ||
                !roomId.All(char.IsAsciiDigit) ||
                roomId.All(c => c == '0'))
            {
                errors.Add($"Auth:Danmaku:RoomIds contains an invalid room id: '{roomId}'.");
                continue;
            }

            if (!seen.Add(roomId))
            {
                errors.Add($"Auth:Danmaku:RoomIds contains a duplicate room id: '{roomId}'.");
            }
        }

        if (!AuthOptionsValidator.IsAbsoluteHttpUrl(options.BaseUrl))
        {
            errors.Add("Auth:Danmaku:BaseUrl must be an absolute http(s) URL.");
        }

        if (!options.SubscribePath.Contains("{roomId}", StringComparison.Ordinal))
        {
            errors.Add("Auth:Danmaku:SubscribePath must contain the {roomId} placeholder.");
        }

        if (!options.RangePath.Contains("{roomId}", StringComparison.Ordinal))
        {
            errors.Add("Auth:Danmaku:RangePath must contain the {roomId} placeholder.");
        }

        if (options.BackfillWindowSeconds <= 0 ||
            options.NotReadyGraceSeconds <= 0 ||
            options.ReadTimeoutSeconds <= 0 ||
            options.MaxBackfillPages <= 0 ||
            options.LeaderLockTtlSeconds <= 0 ||
            options.ReconnectMaxSeconds <= 0)
        {
            errors.Add("Auth:Danmaku numeric options must all be positive.");
        }

        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}
