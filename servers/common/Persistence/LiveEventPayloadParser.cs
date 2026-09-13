using System.Text.Json;
using MongoDB.Bson;

namespace Z.Vtuber.Common.Persistence;

public static class LiveEventPayloadParser
{
    public static BsonDocument ToBsonDocument(string payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            return new BsonDocument();
        }

        try
        {
            using var doc = JsonDocument.Parse(payload);
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                return BsonDocument.Parse(payload);
            }
            else
            {
                return new BsonDocument { { "raw", payload } };
            }
        }
        catch (JsonException)
        {
            return new BsonDocument { { "raw", payload } };
        }
        catch (FormatException)
        {
            return new BsonDocument { { "raw", payload } };
        }
    }

    public static bool TryToBsonDocument(string payload, out BsonDocument result)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            result = new BsonDocument();
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(payload);
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                result = BsonDocument.Parse(payload);
                return true;
            }
            else
            {
                result = new BsonDocument { { "raw", payload } };
                return false;
            }
        }
        catch (JsonException)
        {
            result = new BsonDocument { { "raw", payload } };
            return false;
        }
        catch (FormatException)
        {
            result = new BsonDocument { { "raw", payload } };
            return false;
        }
    }
}
