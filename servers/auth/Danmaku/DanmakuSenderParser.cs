using System.Text.Json;
using Z.Vtuber.Common.Live;

namespace Z.Vtuber.Auth.Danmaku;

public sealed record DanmakuSender(
    string Text,
    long Uid,
    string Name,
    string? AvatarUrl,
    string Medal);

/// <summary>弹幕发送者解析：文本/UID/昵称/粉丝牌复用 common，头像由 auth 私有提取。</summary>
public static class DanmakuSenderParser
{
    public static DanmakuSender? Parse(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var extraction = LiveEventTextExtractor.Extract(LiveEventTextTypes.Danmaku, payload.GetRawText());
        if (extraction is null)
        {
            return null;
        }

        var text = extraction.Text.Trim();
        if (text.Length == 0)
        {
            return null;
        }

        if (extraction.SenderUid <= 0)
        {
            return null;
        }

        var name = string.IsNullOrWhiteSpace(extraction.SenderName)
            ? $"UID:{extraction.SenderUid}"
            : extraction.SenderName;

        return new DanmakuSender(
            text,
            extraction.SenderUid,
            name,
            DanmakuAvatarExtractor.Extract(payload),
            extraction.SenderMedal);
    }
}

/// <summary>头像解析：<c>payload.info[0][15].user.base.face</c>，回退 <c>origin_info.face</c>。</summary>
public static class DanmakuAvatarExtractor
{
    public static string? Extract(JsonElement payload)
    {
        try
        {
            if (!payload.TryGetProperty("info", out var info) ||
                info.ValueKind != JsonValueKind.Array ||
                info.GetArrayLength() == 0)
            {
                return null;
            }

            var meta = info[0];
            if (meta.ValueKind != JsonValueKind.Array || meta.GetArrayLength() <= 15)
            {
                return null;
            }

            var container = meta[15];
            if (container.ValueKind != JsonValueKind.Object ||
                !container.TryGetProperty("user", out var user) ||
                user.ValueKind != JsonValueKind.Object ||
                !user.TryGetProperty("base", out var baseInfo) ||
                baseInfo.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var face = ReadString(baseInfo, "face");
            if (!string.IsNullOrWhiteSpace(face))
            {
                return face;
            }

            if (baseInfo.TryGetProperty("origin_info", out var origin) &&
                origin.ValueKind == JsonValueKind.Object)
            {
                face = ReadString(origin, "face");
                if (!string.IsNullOrWhiteSpace(face))
                {
                    return face;
                }
            }

            return null;
        }
        catch (Exception ex) when (ex is InvalidOperationException or JsonException)
        {
            return null;
        }
    }

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
