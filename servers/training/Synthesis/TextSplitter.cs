using System.Text.RegularExpressions;

namespace Z.Vtuber.Training.Synthesis;

/// <summary>
/// 断句：按固定规则 <c>[，。,.\s]+</c> 拆分文本，逐段 <c>Trim</c> 后丢弃空段并保序。
/// 纯函数、确定性；空白/纯标点输入返回空列表。
/// </summary>
public static partial class TextSplitter
{
    /// <summary>需求固定断句分隔符：中英文逗号/句号与任意空白。</summary>
    [GeneratedRegex(@"[，。,.\s]+")]
    private static partial Regex Separator();

    /// <summary>拆分文本为候选短句（保序、非空）。</summary>
    public static IReadOnlyList<string> Split(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length == 0)
        {
            return [];
        }

        var result = new List<string>();
        foreach (var part in Separator().Split(text))
        {
            var trimmed = part.Trim();
            if (trimmed.Length > 0)
            {
                result.Add(trimmed);
            }
        }

        return result;
    }
}
