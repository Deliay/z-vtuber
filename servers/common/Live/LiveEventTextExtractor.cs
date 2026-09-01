using System.Text.Json;

namespace Z.Vtuber.Common.Live;

public sealed record LiveEventTextExtraction(
    string Text,
    string SenderName,
    long SenderUid,
    string SenderMedal
);

public static class LiveEventTextExtractor
{
    public static LiveEventTextExtraction? Extract(string type, string payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(payload);
            string? text = null;
            string senderName = string.Empty;
            long senderUid = 0;
            string senderMedal = string.Empty;

            if (type == "danmaku")
            {
                // 弹幕：取 info[1]（text）、info[2][0]（uid）、info[2][1]（uname）、info[3][1]（medal）
                if (doc.RootElement.TryGetProperty("info", out var info) &&
                    info.ValueKind == JsonValueKind.Array &&
                    info.GetArrayLength() > 1)
                {
                    var textElement = info[1];
                    if (textElement.ValueKind == JsonValueKind.String)
                    {
                        text = textElement.GetString();
                    }

                    // info[2] = [uid, uname, ...]
                    if (info.GetArrayLength() > 2 && info[2].ValueKind == JsonValueKind.Array)
                    {
                        var senderInfo = info[2];
                        if (senderInfo.GetArrayLength() > 0 && senderInfo[0].ValueKind == JsonValueKind.Number)
                        {
                            senderUid = senderInfo[0].GetInt64();
                        }

                        if (senderInfo.GetArrayLength() > 1 && senderInfo[1].ValueKind == JsonValueKind.String)
                        {
                            senderName = senderInfo[1].GetString() ?? string.Empty;
                        }
                    }

                    // info[3] = [medalLevel, medalName, ...]（空数组表示未佩戴）
                    if (info.GetArrayLength() > 3 && info[3].ValueKind == JsonValueKind.Array)
                    {
                        var medalInfo = info[3];
                        if (medalInfo.GetArrayLength() > 1 && medalInfo[1].ValueKind == JsonValueKind.String)
                        {
                            senderMedal = medalInfo[1].GetString() ?? string.Empty;
                        }
                    }
                }
            }
            else if (type == "super_chat")
            {
                // 醒目留言：取 data.message（text）、data.uid、data.user_info.uname、data.medal_info.medal_name
                if (doc.RootElement.TryGetProperty("data", out var data) &&
                    data.ValueKind == JsonValueKind.Object)
                {
                    if (data.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String)
                    {
                        text = message.GetString();
                    }

                    if (data.TryGetProperty("uid", out var uid) && uid.ValueKind == JsonValueKind.Number)
                    {
                        senderUid = uid.GetInt64();
                    }

                    if (data.TryGetProperty("user_info", out var userInfo) && userInfo.ValueKind == JsonValueKind.Object)
                    {
                        if (userInfo.TryGetProperty("uname", out var uname) && uname.ValueKind == JsonValueKind.String)
                        {
                            senderName = uname.GetString() ?? string.Empty;
                        }
                    }

                    if (data.TryGetProperty("medal_info", out var medalInfo) && medalInfo.ValueKind == JsonValueKind.Object)
                    {
                        if (medalInfo.TryGetProperty("medal_name", out var medalName) && medalName.ValueKind == JsonValueKind.String)
                        {
                            senderMedal = medalName.GetString() ?? string.Empty;
                        }
                    }
                }
            }

            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }

            return new LiveEventTextExtraction(text, senderName, senderUid, senderMedal);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
