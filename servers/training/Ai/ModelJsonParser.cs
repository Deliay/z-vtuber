using System.Text.Json;
using Z.Vtuber.Training.Contracts;

namespace Z.Vtuber.Training.Ai;

/// <summary>
/// 模型 JSON 输出解析（Plan 2.4 / 提案 §5.2 实现点 3）：
/// 剥离 <c>```json</c> / <c>```</c> 围栏 → 取首个平衡 <c>{...}</c> 对象（嵌套括号/字符串内括号安全）
/// → 大小写不敏感取字段 → 数值存在性校验与范围钳制；任何失败抛 <see cref="ModelOutputException"/>。
/// 纯函数，单测重点。
/// </summary>
public static class ModelJsonParser
{
    /// <summary>情绪维度/权重上限（<c>emotionControlWeight</c> 允许超过 1，上限 10）。</summary>
    public const double MaxEmotionControlWeight = 10d;

    public static double Clamp01(double value) => Clamp(value, 0, 1);

    /// <summary>钳制到 [min, max]；NaN → min（Infinity 由 Min/Max 自然收敛到边界）。</summary>
    public static double Clamp(double value, double min, double max) =>
        double.IsNaN(value) ? min : Math.Min(Math.Max(value, min), max);

    /// <summary>识别文本：要求字符串字段 <c>text</c>（大小写不敏感）；失败抛 <see cref="ModelOutputException"/>。</summary>
    public static string ParseRecognizeText(string raw)
    {
        var element = ExtractObject(raw);
        if (!TryGetProperty(element, "text", out var text) || text.ValueKind != JsonValueKind.String)
        {
            throw new ModelOutputException("model output is missing a string 'text' field");
        }

        return text.GetString() ?? string.Empty;
    }

    /// <summary>
    /// 情绪结果：要求 <c>emotionControlWeight</c> 与八个维度均为存在数值（大小写不敏感），
    /// <c>emotionDescription</c> 可选（默认空串）；八维钳 0~1、权重钳 0~10；失败抛 <see cref="ModelOutputException"/>。
    /// </summary>
    public static EmotionResult ParseEmotion(string raw)
    {
        var element = ExtractObject(raw);
        var weight = RequiredNumber(element, "emotionControlWeight");

        return new EmotionResult(
            Clamp(weight, 0, MaxEmotionControlWeight),
            OptionalString(element, "emotionDescription"),
            Clamp01(RequiredNumber(element, "happy")),
            Clamp01(RequiredNumber(element, "angry")),
            Clamp01(RequiredNumber(element, "sad")),
            Clamp01(RequiredNumber(element, "afraid")),
            Clamp01(RequiredNumber(element, "disgusted")),
            Clamp01(RequiredNumber(element, "melancholic")),
            Clamp01(RequiredNumber(element, "surprised")),
            Clamp01(RequiredNumber(element, "calm")));
    }

    /// <summary>剥离围栏 → 定位首个平衡对象 → 反序列化为可复用的 <see cref="JsonElement"/>。</summary>
    private static JsonElement ExtractObject(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            throw new ModelOutputException("model output is empty");
        }

        var content = StripFences(raw);
        var start = content.IndexOf('{');
        if (start < 0)
        {
            throw new ModelOutputException("model output contains no JSON object");
        }

        var end = FindBalancedEnd(content, start);
        if (end < 0)
        {
            throw new ModelOutputException("model output JSON object is not balanced");
        }

        var json = content[start..(end + 1)];
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            throw new ModelOutputException($"invalid model JSON output: {ex.Message}");
        }
    }

    /// <summary>去掉首尾 <c>```json</c> / <c>```</c> 围栏（仅当确实位于首/尾时）。</summary>
    private static string StripFences(string raw)
    {
        var text = raw.Trim();
        if (text.StartsWith("```", StringComparison.Ordinal))
        {
            var firstNewline = text.IndexOf('\n');
            text = firstNewline >= 0 ? text[(firstNewline + 1)..] : text[3..];
        }

        if (text.EndsWith("```", StringComparison.Ordinal))
        {
            text = text[..^3];
        }

        return text.Trim();
    }

    /// <summary>从 <paramref name="start"/>（应为 <c>{</c>）起找首个深度归零的 <c>}</c>；忽略字符串内括号与转义。</summary>
    private static int FindBalancedEnd(string text, int start)
    {
        var depth = 0;
        var inString = false;
        var escaped = false;
        for (var i = start; i < text.Length; i++)
        {
            var c = text[i];
            if (inString)
            {
                if (escaped)
                {
                    escaped = false;
                }
                else if (c == '\\')
                {
                    escaped = true;
                }
                else if (c == '"')
                {
                    inString = false;
                }

                continue;
            }

            switch (c)
            {
                case '"':
                    inString = true;
                    break;
                case '{':
                    depth++;
                    break;
                case '}':
                    depth--;
                    if (depth == 0)
                    {
                        return i;
                    }

                    break;
            }
        }

        return -1;
    }

    private static double RequiredNumber(JsonElement element, string name)
    {
        if (!TryGetNumber(element, name, out var value))
        {
            throw new ModelOutputException($"model output is missing a numeric '{name}' field");
        }

        return value;
    }

    private static string OptionalString(JsonElement element, string name) =>
        TryGetProperty(element, name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static bool TryGetNumber(JsonElement element, string name, out double value)
    {
        value = 0;
        return TryGetProperty(element, name, out var property)
            && property.ValueKind == JsonValueKind.Number
            && property.TryGetDouble(out value);
    }

    /// <summary>大小写不敏感取字段（LLM 输出键名大小写不稳定）。</summary>
    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }
}
