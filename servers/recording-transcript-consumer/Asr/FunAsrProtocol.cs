using System.Text.Json;

namespace Z.Vtuber.RecordingTranscript.Consumer.Asr;

/// <summary>
/// FunASR realtime websocket 协议构造与解析（纯函数，便于针对假 websocket server 做契约测试）。
/// 参考：https://platform.qianwenai.com/docs/api-reference/speech-recognition/fun-asr-realtime/websocket-api.md
/// 注意：客户端→服务端【指令】用 <c>header.action</c>（run-task/finish-task）；
/// 服务端→客户端【事件】用 <c>header.event</c>（task-started/result-generated/task-finished/task-failed）。
/// </summary>
internal static class FunAsrProtocol
{
    // 客户端指令（header.action）
    public const string ActionRunTask = "run-task";
    public const string ActionFinishTask = "finish-task";

    // 服务端事件（header.event）
    public const string EventTaskStarted = "task-started";
    public const string EventResultGenerated = "result-generated";
    public const string EventTaskFinished = "task-finished";
    public const string EventTaskFailed = "task-failed";

    public static string BuildRunTask(string taskId, string model)
    {
        var payload = new
        {
            header = new { action = ActionRunTask, task_id = taskId, streaming = "duplex" },
            payload = new
            {
                task_group = "audio",
                task = "asr",
                function = "recognition",
                model,
                parameters = new { format = "pcm", sample_rate = 16000 },
                input = new { },
            },
        };
        return JsonSerializer.Serialize(payload);
    }

    public static string BuildFinishTask(string taskId)
    {
        var payload = new
        {
            header = new { action = ActionFinishTask, task_id = taskId, streaming = "duplex" },
            payload = new { input = new { } },
        };
        return JsonSerializer.Serialize(payload);
    }

    /// <summary>解析客户端指令（header.action）。</summary>
    public static bool TryParseAction(string json, out string? action)
    {
        action = null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("header", out var header)
                && header.TryGetProperty("action", out var actionProp))
            {
                action = actionProp.GetString();
                return action is not null;
            }
        }
        catch (JsonException)
        {
            // 非 JSON 消息（例如服务端心跳）忽略
        }

        return false;
    }

    /// <summary>解析服务端事件（header.event）。</summary>
    public static bool TryParseEvent(string json, out string? @event)
    {
        @event = null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("header", out var header)
                && header.TryGetProperty("event", out var eventProp))
            {
                @event = eventProp.GetString();
                return @event is not null;
            }
        }
        catch (JsonException)
        {
            // 非 JSON 消息忽略
        }

        return false;
    }

    public static string? ParseErrorMessage(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("header", out var header))
            {
                var code = header.TryGetProperty("error_code", out var c) ? c.ToString() : null;
                var message = header.TryGetProperty("error_message", out var m) ? m.GetString() : null;
                return $"FunASR task failed ({code}): {message}";
            }
        }
        catch (JsonException)
        {
            // ignore
        }

        return "FunASR task failed";
    }

    /// <summary>解析 result 事件；仅当 sentence_end == true 且文本非空时返回成句片段，时间为返回时间 + offset。</summary>
    public static bool TryParseSentence(string json, long offset, out StreamingAsrSegment? segment)
    {
        segment = null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("payload", out var payload)
                || !payload.TryGetProperty("output", out var output)
                || !output.TryGetProperty("sentence", out var sentence))
            {
                return false;
            }

            var sentenceEnd = sentence.TryGetProperty("sentence_end", out var se) && se.ValueKind == JsonValueKind.True;
            if (!sentenceEnd)
            {
                return false;
            }

            var text = sentence.TryGetProperty("text", out var t) ? t.GetString() ?? string.Empty : string.Empty;
            if (string.IsNullOrEmpty(text))
            {
                return false;
            }

            var begin = sentence.TryGetProperty("begin_time", out var b) ? b.GetInt64() : 0L;
            var end = sentence.TryGetProperty("end_time", out var e) ? e.GetInt64() : begin;
            segment = new StreamingAsrSegment(begin + offset, end + offset, text);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
