using System.Text;
using System.Text.Json;

namespace Z.Vtuber.Auth.Danmaku;

public sealed record DanmakuFrame(string Event, string Data);

/// <summary>SSE 帧解析：空行分帧、<c>:</c> 注释忽略、多行 <c>data:</c> 拼接、缺省 event=message。</summary>
public static class DanmakuFrameParser
{
    public const string DefaultEvent = "message";

    public static IReadOnlyList<DanmakuFrame> Parse(string content)
    {
        var frames = new List<DanmakuFrame>();
        var lines = content.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        var eventName = DefaultEvent;
        var data = new List<string>();
        var hasField = false;

        foreach (var rawLine in lines)
        {
            var line = rawLine;
            if (line.Length == 0)
            {
                if (hasField)
                {
                    frames.Add(new DanmakuFrame(eventName, string.Join('\n', data)));
                }

                eventName = DefaultEvent;
                data.Clear();
                hasField = false;
                continue;
            }

            if (line[0] == ':')
            {
                continue;
            }

            var colon = line.IndexOf(':');
            var field = colon >= 0 ? line[..colon] : line;
            var value = colon >= 0 ? line[(colon + 1)..] : string.Empty;
            if (value.StartsWith(' '))
            {
                value = value[1..];
            }

            switch (field)
            {
                case "event":
                    eventName = value;
                    hasField = true;
                    break;
                case "data":
                    data.Add(value);
                    hasField = true;
                    break;
                case "id":
                case "retry":
                    hasField = true;
                    break;
            }
        }

        if (hasField)
        {
            frames.Add(new DanmakuFrame(eventName, string.Join('\n', data)));
        }

        return frames;
    }

    /// <summary>从连接流读取一帧；<paramref name="timeout"/> 内无完整帧则抛 <see cref="TimeoutException"/>。</summary>
    public static async Task<DanmakuFrame?> ReadFrameAsync(
        StreamReader reader,
        TimeSpan timeout,
        CancellationToken ct)
    {
        var eventName = DefaultEvent;
        var data = new List<string>();
        var hasField = false;

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);

        while (!timeoutCts.IsCancellationRequested)
        {
            string? rawLine;
            try
            {
                rawLine = await reader.ReadLineAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException("No SSE frame received within the configured read timeout.");
            }

            if (rawLine is null)
            {
                return hasField ? new DanmakuFrame(eventName, string.Join('\n', data)) : null;
            }

            if (rawLine.Length == 0)
            {
                if (!hasField)
                {
                    timeoutCts.CancelAfter(timeout);
                    continue;
                }

                return new DanmakuFrame(eventName, string.Join('\n', data));
            }

            if (rawLine[0] == ':')
            {
                continue;
            }

            var colon = rawLine.IndexOf(':');
            var field = colon >= 0 ? rawLine[..colon] : rawLine;
            var value = colon >= 0 ? rawLine[(colon + 1)..] : string.Empty;
            if (value.StartsWith(' '))
            {
                value = value[1..];
            }

            switch (field)
            {
                case "event":
                    eventName = value;
                    hasField = true;
                    break;
                case "data":
                    data.Add(value);
                    hasField = true;
                    break;
                case "id":
                case "retry":
                    hasField = true;
                    break;
            }

            timeoutCts.CancelAfter(timeout);
        }

        throw new TimeoutException("No SSE frame received within the configured read timeout.");
    }
}

/// <summary>单条弹幕事件（payload 已克隆，脱离 JsonDocument 生命周期）。</summary>
public sealed record DanmakuEvent(
    string RoomId,
    string Type,
    DateTime OccurredAt,
    JsonElement Payload)
{
    public string PayloadJson => Payload.ValueKind == JsonValueKind.Undefined ? "{}" : Payload.GetRawText();
}

public static class DanmakuPayloadReader
{
    public static bool TryRead(JsonElement root, out DanmakuEvent? result)
    {
        result = null;
        if (root.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var roomId = ReadRoomId(root);
        if (string.IsNullOrEmpty(roomId))
        {
            return false;
        }

        var type = ReadString(root, "type") ?? ReadString(root, "event") ?? "danmaku";
        var occurredAt = ReadOccurredAt(root);

        if (root.TryGetProperty("payload", out var payloadElement))
        {
            var payload = TryClonePayload(payloadElement);
            if (payload is null)
            {
                return false;
            }

            result = new DanmakuEvent(roomId, type, occurredAt, payload.Value);
            return true;
        }

        // 兼容直接传 B 站 payload（含 info）的场景。
        if (root.TryGetProperty("info", out _))
        {
            result = new DanmakuEvent(roomId, type, occurredAt, root.Clone());
            return true;
        }

        return false;
    }

    public static string? ReadRoomId(JsonElement root)
    {
        if (!root.TryGetProperty("roomId", out var roomId) &&
            !root.TryGetProperty("room_id", out roomId))
        {
            return null;
        }

        return roomId.ValueKind switch
        {
            JsonValueKind.String => roomId.GetString(),
            JsonValueKind.Number => roomId.GetRawText(),
            _ => null
        };
    }

    private static JsonElement? TryClonePayload(JsonElement payload)
    {
        if (payload.ValueKind == JsonValueKind.Object)
        {
            return payload.Clone();
        }

        if (payload.ValueKind == JsonValueKind.String)
        {
            var text = payload.GetString();
            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }

            try
            {
                using var doc = JsonDocument.Parse(text);
                return doc.RootElement.Clone();
            }
            catch (JsonException)
            {
                return null;
            }
        }

        return null;
    }

    private static string? ReadString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static DateTime ReadOccurredAt(JsonElement root)
    {
        if ((root.TryGetProperty("occurredAt", out var value) ||
             root.TryGetProperty("occurred_at", out value)) &&
            value.ValueKind == JsonValueKind.String &&
            DateTime.TryParse(value.GetString(), null, System.Globalization.DateTimeStyles.RoundtripKind, out var parsed))
        {
            return parsed.Kind == DateTimeKind.Utc ? parsed : parsed.ToUniversalTime();
        }

        return DateTime.UtcNow;
    }
}
