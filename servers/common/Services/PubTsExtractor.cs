using System.Text.Json;

namespace Z.Vtuber.Common.Services;

public static class PubTsExtractor
{
    public static long? FromRawJson(string data)
    {
        try
        {
            using var doc = JsonDocument.Parse(data);
            return FromJsonElement(doc.RootElement);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static long? FromJsonElement(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty("modules", out var modules) ||
            modules.ValueKind != JsonValueKind.Object ||
            !modules.TryGetProperty("module_author", out var author) ||
            author.ValueKind != JsonValueKind.Object ||
            !author.TryGetProperty("pub_ts", out var pubTs))
        {
            return null;
        }

        if (pubTs.ValueKind == JsonValueKind.Number && pubTs.TryGetInt64(out var intValue))
        {
            return intValue;
        }

        if (pubTs.ValueKind == JsonValueKind.String &&
            long.TryParse(pubTs.GetString(), out var strValue))
        {
            return strValue;
        }

        return null;
    }
}
