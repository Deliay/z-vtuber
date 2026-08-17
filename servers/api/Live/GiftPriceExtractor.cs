using System.Text.Json;

namespace Z.Vtuber.Live;

public static class GiftPriceExtractor
{
    public static long ExtractGoldSeeds(string type, string payload)
    {
        if (type != "gift")
        {
            return 0;
        }

        try
        {
            using var doc = JsonDocument.Parse(payload);
            if (doc.RootElement.TryGetProperty("data", out var data) &&
                data.ValueKind == JsonValueKind.Object)
            {
                if (data.TryGetProperty("coin_type", out var coinType) &&
                    coinType.ValueKind == JsonValueKind.String &&
                    coinType.GetString() == "gold" &&
                    data.TryGetProperty("price", out var price) &&
                    price.ValueKind == JsonValueKind.Number &&
                    price.TryGetInt64(out var priceValue))
                {
                    return priceValue;
                }
            }
        }
        catch (JsonException)
        {
        }

        return 0;
    }
}
