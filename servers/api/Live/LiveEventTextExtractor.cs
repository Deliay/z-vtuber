using System.Text.Json;

namespace Z.Vtuber.Live;

public static class LiveEventTextExtractor
{
    public static string? ExtractText(string type, string payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(payload);
            string? text = null;

            if (type == "danmaku")
            {
                // 弹幕：取 info[1]（string）
                if (doc.RootElement.TryGetProperty("info", out var info) &&
                    info.ValueKind == JsonValueKind.Array &&
                    info.GetArrayLength() > 1)
                {
                    var secondElement = info[1];
                    if (secondElement.ValueKind == JsonValueKind.String)
                    {
                        text = secondElement.GetString();
                    }
                }
            }
            else if (type == "super_chat")
            {
                // 醒目留言：取 data.message（string）
                if (doc.RootElement.TryGetProperty("data", out var data) &&
                    data.ValueKind == JsonValueKind.Object &&
                    data.TryGetProperty("message", out var message) &&
                    message.ValueKind == JsonValueKind.String)
                {
                    text = message.GetString();
                }
            }

            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }

            return text;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
