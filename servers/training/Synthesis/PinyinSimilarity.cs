namespace Z.Vtuber.Training.Synthesis;

/// <summary>
/// 拼音相似度：音节 token 序列的归一化编辑距离（Levenshtein ratio）。
/// <c>Ratio(a, b) = 1 - Distance(a, b) / Max(Len(a), Len(b))</c>，取值 <c>[0, 1]</c>，1 = 完全相同。
/// 编辑操作（插入/删除/替换）以**音节**为单位、代价均 1，分母取较长序列长度——同时反映发音内容差异与长度差异。
/// 与 <see cref="TextSimilarity"/> 同算法形状（两行滚动数组，空间 O(min(m, n))）。
/// 空序列口径：两者皆空 → 1；仅一方为空 → 0。确定性、对称。
/// </summary>
public static class PinyinSimilarity
{
    /// <summary>归一化编辑距离相似度（0~1，越大越相似）。</summary>
    public static double Ratio(IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);

        if (a.Count == 0 && b.Count == 0)
        {
            return 1d;
        }

        if (a.Count == 0 || b.Count == 0)
        {
            return 0d;
        }

        var distance = LevenshteinDistance(a, b);
        return 1d - (double)distance / Math.Max(a.Count, b.Count);
    }

    /// <summary>Levenshtein 编辑距离（插入/删除/替换代价均为 1）；两行滚动数组，空间 O(min(m, n))。</summary>
    private static int LevenshteinDistance(IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        // 让 b 成为较短序列以压缩空间（编辑距离对称）。
        if (a.Count < b.Count)
        {
            (a, b) = (b, a);
        }

        var previous = new int[b.Count + 1];
        var current = new int[b.Count + 1];
        for (var j = 0; j <= b.Count; j++)
        {
            previous[j] = j;
        }

        for (var i = 1; i <= a.Count; i++)
        {
            current[0] = i;
            var ca = a[i - 1];
            for (var j = 1; j <= b.Count; j++)
            {
                var cost = string.Equals(ca, b[j - 1], StringComparison.Ordinal) ? 0 : 1;
                current[j] = Math.Min(
                    Math.Min(current[j - 1] + 1, previous[j] + 1),
                    previous[j - 1] + cost);
            }

            (previous, current) = (current, previous);
        }

        return previous[b.Count];
    }
}
