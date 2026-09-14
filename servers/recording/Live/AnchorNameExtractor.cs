using System.Text.Json.Nodes;

namespace Z.Vtuber.Recording.Live;

/// <summary>从主播基础信息 Data（channel JSON）中提取昵称 uName。</summary>
public static class AnchorNameExtractor
{
    public static string? Extract(string? data)
    {
        if (string.IsNullOrEmpty(data))
        {
            return null;
        }

        try
        {
            var node = JsonNode.Parse(data);
            if (node is JsonObject obj
                && obj["uName"] is JsonValue value
                && value.TryGetValue<string>(out var name))
            {
                return name;
            }
        }
        catch
        {
            // 非法 JSON 视为无昵称
        }

        return null;
    }
}
