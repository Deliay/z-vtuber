namespace Z.Vtuber.Training.Synthesis;

/// <summary>
/// 音色参考匹配用的字符相似度：归一化编辑距离（Levenshtein ratio）。
/// <c>Ratio(a, b) = 1 - Distance(a, b) / Max(Len(a), Len(b))</c>，取值 <c>[0, 1]</c>，1 = 完全相同。
/// 分母取较长串长度，使分数同时反映**内容差异**（编辑操作数）与**长度差异**（长度比）；
/// 逐字符比较（UTF-16 code unit），中文按字符计。
/// 空串口径：两者皆空 → 1；仅一方为空 → 0（音频条目文本为空时不参与匹配，由调用方过滤）。
/// </summary>
public static class TextSimilarity
{
    /// <summary>归一化编辑距离相似度（0~1，越大越相似）。</summary>
    public static double Ratio(string a, string b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);

        if (a.Length == 0 && b.Length == 0)
        {
            return 1d;
        }

        if (a.Length == 0 || b.Length == 0)
        {
            return 0d;
        }

        if (string.Equals(a, b, StringComparison.Ordinal))
        {
            return 1d;
        }

        var distance = LevenshteinDistance(a, b);
        return 1d - (double)distance / Math.Max(a.Length, b.Length);
    }

    /// <summary>Levenshtein 编辑距离（插入/删除/替换代价均为 1）；两行滚动数组，空间 O(min(m, n))。</summary>
    private static int LevenshteinDistance(string a, string b)
    {
        // 让 b 成为较短串以压缩空间（编辑距离对称）。
        if (a.Length < b.Length)
        {
            (a, b) = (b, a);
        }

        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++)
        {
            previous[j] = j;
        }

        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            var ca = a[i - 1];
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = ca == b[j - 1] ? 0 : 1;
                current[j] = Math.Min(
                    Math.Min(current[j - 1] + 1, previous[j] + 1),
                    previous[j - 1] + cost);
            }

            (previous, current) = (current, previous);
        }

        return previous[b.Length];
    }
}
