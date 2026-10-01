namespace Z.Vtuber.Common.Contracts;

/// <summary>能力取值非法（白名单校验失败）——控制器映射 400 <c>unsupported capability</c>。</summary>
public sealed class CapabilityException(string message) : Exception(message);

/// <summary>
/// 模型能力纯函数（纯计算、无 IO）：固定四值白名单（语音/视频/文本/图片）+
/// <see cref="Normalize"/>（trim + 小写 + 去重 + 白名单）与 <see cref="Matches"/>（集合包含判定）。
/// </summary>
public static class TrainingModelCapabilities
{
    /// <summary>语音。</summary>
    public const string Audio = "audio";

    /// <summary>视频。</summary>
    public const string Video = "video";

    /// <summary>文本。</summary>
    public const string Text = "text";

    /// <summary>图片。</summary>
    public const string Image = "image";

    /// <summary>全部能力（固定顺序：语音、视频、文本、图片）。</summary>
    public static readonly IReadOnlyList<string> All = new[] { Audio, Video, Text, Image };

    private static readonly HashSet<string> Whitelist = new(All, StringComparer.Ordinal);

    /// <summary>
    /// 归一化能力集合：逐项 trim + 小写，去重并保持首次出现顺序；空白项忽略；
    /// 非白名单值抛 <see cref="CapabilityException"/>。入参 null/空集合均为合法空集。
    /// </summary>
    public static IReadOnlyList<string> Normalize(IEnumerable<string?>? raw)
    {
        if (raw is null)
        {
            return Array.Empty<string>();
        }

        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in raw)
        {
            var value = item?.Trim().ToLowerInvariant() ?? string.Empty;
            if (value.Length == 0)
            {
                continue;
            }

            if (!Whitelist.Contains(value))
            {
                throw new CapabilityException("unsupported capability");
            }

            if (seen.Add(value))
            {
                result.Add(value);
            }
        }

        return result;
    }

    /// <summary>
    /// <paramref name="required"/> ⊆ <paramref name="modelCapabilities"/> 判定（输入、输出分别调用）：
    /// 空要求恒满足；模型能力为空集时不匹配任何非空要求。
    /// </summary>
    public static bool Matches(IReadOnlyList<string> modelCapabilities, IReadOnlyList<string> required)
    {
        if (required.Count == 0)
        {
            return true;
        }

        if (modelCapabilities.Count == 0)
        {
            return false;
        }

        var set = new HashSet<string>(modelCapabilities, StringComparer.Ordinal);
        return required.All(set.Contains);
    }
}
