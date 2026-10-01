using System.Text;
using TinyPinyin;

namespace Z.Vtuber.Training.Synthesis;

/// <summary>
/// 文本 → 无声调拼音音节序列：逐字符处理——汉字转拼音（无声调、小写，一个汉字 = 一个音节 token）；
/// ASCII 字母/数字按连续段小写为 1 个 token；其余字符（标点等）丢弃。
/// 落库形式为音节序列单空格 join（加载后 split 还原）。同一文本 → 同一序列、同音字同音节。
/// </summary>
public static class PinyinConverter
{
    /// <summary>转换为小写无声调音节 token 序列。</summary>
    public static IReadOnlyList<string> ToSyllables(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var tokens = new List<string>();
        var asciiRun = new StringBuilder();

        foreach (var c in text)
        {
            if (PinyinHelper.IsChinese(c))
            {
                FlushAscii(asciiRun, tokens);
                var pinyin = PinyinHelper.GetPinyin(c);
                if (!string.IsNullOrEmpty(pinyin))
                {
                    tokens.Add(pinyin.ToLowerInvariant());
                }
            }
            else if (IsAsciiLetterOrDigit(c))
            {
                asciiRun.Append(char.ToLowerInvariant(c));
            }
            else
            {
                // 标点/空白/其他：终结当前 ASCII 连续段并丢弃该字符
                FlushAscii(asciiRun, tokens);
            }
        }

        FlushAscii(asciiRun, tokens);
        return tokens;
    }

    /// <summary>转换为落库形式：音节序列单空格 join。</summary>
    public static string ToPinyin(string text) => string.Join(' ', ToSyllables(text));

    /// <summary>把落库的拼音字符串还原为音节序列（空串 → 空序列）。</summary>
    public static IReadOnlyList<string> Split(string pinyin) =>
        string.IsNullOrWhiteSpace(pinyin)
            ? []
            : pinyin.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static void FlushAscii(StringBuilder run, List<string> tokens)
    {
        if (run.Length == 0)
        {
            return;
        }

        tokens.Add(run.ToString());
        run.Clear();
    }

    private static bool IsAsciiLetterOrDigit(char c) =>
        (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9');
}
