using System.Text.Json;

namespace Z.Vtuber.Persistence;

public static class BsonConvert
{
    public static string FromJsonElement(JsonElement element) =>
        element.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null
            ? "{}"
            : element.GetRawText();
}
