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
    }
}