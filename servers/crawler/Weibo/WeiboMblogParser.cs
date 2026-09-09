using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Z.Vtuber.Crawler.Weibo;

public static class WeiboMblogParser
{
    // 例: "Wed Oct 10 00:00:00 +0800 2023"（微博 Ruby 格式，英文缩写星期/月份在前，偏移在时间与年份之间）
    private static readonly Regex RubyCreatedAtRegex = new(
        @"^\s*[A-Za-z]{3}\s+(?<mon>[A-Za-z]{3})\s+(?<day>\d{1,2})\s+(?<time>\d{1,2}:\d{2}:\d{2})\s+(?<off>[+-]\d{2}:?\d{2})\s+(?<year>\d{4})\s*$",
        RegexOptions.Compiled);

    private const string NormalizedFormat = "d MMM yyyy HH:mm:ss zzz";

    public static long? ParseCreatedAtSeconds(string? createdAt)
    {
        if (string.IsNullOrWhiteSpace(createdAt))
        {
            return null;
        }

        var match = RubyCreatedAtRegex.Match(createdAt);
        if (match.Success)
        {
            // 归一化为 .NET 可解析的顺序：日 月 年 时:分:秒 偏移（zzz 允许无冒号偏移）
            var offset = match.Groups["off"].Value;
            offset = Regex.Replace(offset, @"([+-]\d{2})(\d{2})$", "$1:$2");
            var normalized = $"{match.Groups["day"].Value} {match.Groups["mon"].Value} {match.Groups["year"].Value} {match.Groups["time"].Value} {offset}";

            if (DateTimeOffset.TryParseExact(
                    normalized,
                    NormalizedFormat,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AllowWhiteSpaces,
                    out var parsed))
            {
                return parsed.ToUnixTimeSeconds();
            }
        }

        // 兜底：宽松解析任意能识别的日期格式
        if (DateTimeOffset.TryParse(
                createdAt,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces,
                out var relaxed))
        {
            return relaxed.ToUnixTimeSeconds();
        }

        return null;
    }

    /// <summary>从 getIndex 响应根元素提取卡片 mblog（card_type == 9），并解析游标信息。</summary>
    public static WeiboTimelinePage ParsePage(JsonElement root)
    {
        var hasMore = false;
        var sinceId = string.Empty;
        var items = new List<WeiboMblogItem>();

        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
        {
            return new WeiboTimelinePage { HasMore = false, SinceId = string.Empty, Items = items };
        }

        if (data.TryGetProperty("cardlistInfo", out var cardlistInfo) && cardlistInfo.ValueKind == JsonValueKind.Object)
        {
            if (cardlistInfo.TryGetProperty("has_more", out var hasMoreEl) && hasMoreEl.ValueKind == JsonValueKind.True)
            {
                hasMore = true;
            }

            if (cardlistInfo.TryGetProperty("since_id", out var sinceIdEl) && sinceIdEl.ValueKind == JsonValueKind.String)
            {
                sinceId = sinceIdEl.GetString() ?? string.Empty;
            }
        }

        if (data.TryGetProperty("cards", out var cards) && cards.ValueKind == JsonValueKind.Array)
        {
            foreach (var card in cards.EnumerateArray())
            {
                if (card.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                // 仅取 card_type == 9 的微博卡片
                var isMblogCard = card.TryGetProperty("card_type", out var cardTypeEl)
                    && cardTypeEl.ValueKind == JsonValueKind.Number
                    && cardTypeEl.GetInt32() == 9;
                if (!isMblogCard)
                {
                    continue;
                }

                if (!card.TryGetProperty("mblog", out var mblog) || mblog.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var mblogId = ReadMblogId(mblog);
                if (string.IsNullOrEmpty(mblogId))
                {
                    continue;
                }

                var createdAt = mblog.TryGetProperty("created_at", out var createdAtEl) && createdAtEl.ValueKind == JsonValueKind.String
                    ? createdAtEl.GetString()
                    : null;

                items.Add(new WeiboMblogItem
                {
                    MblogId = mblogId,
                    PubTs = ParseCreatedAtSeconds(createdAt),
                    Data = mblog.Clone(),
                });
            }
        }

        return new WeiboTimelinePage { HasMore = hasMore, SinceId = sinceId, Items = items };
    }

    private static string ReadMblogId(JsonElement mblog)
    {
        if (mblog.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String)
        {
            return idEl.GetString() ?? string.Empty;
        }

        if (mblog.TryGetProperty("idstr", out var idstrEl) && idstrEl.ValueKind == JsonValueKind.String)
        {
            return idstrEl.GetString() ?? string.Empty;
        }

        return string.Empty;
    }
}

public sealed class WeiboMblogItem
{
    public string MblogId { get; set; } = string.Empty;
    public long? PubTs { get; set; }
    public JsonElement Data { get; set; }
}

public sealed class WeiboTimelinePage
{
    public bool HasMore { get; set; }
    public string SinceId { get; set; } = string.Empty;
    public IReadOnlyList<WeiboMblogItem> Items { get; set; } = [];
}
