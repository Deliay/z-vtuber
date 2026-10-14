using MongoDB.Bson;

namespace Z.Vtuber.Training.Synthesis;

/// <summary>情绪候选：条目 id/audioKey + 该条目八维情绪向量（<see cref="EmotionVector.FromEmotion"/> 向量化）。</summary>
public sealed record EmotionCandidate(ObjectId ItemId, string AudioKey, float[] Vector);

/// <summary>情绪匹配命中：条目 id + audioKey（带情绪标注的命中条目）。</summary>
public sealed record EmotionMatch(ObjectId ItemId, string AudioKey);

/// <summary>
/// 情绪参考内存匹配（纯函数，Milvus 移除后的替代）：候选八维向量中按 L2（平方欧氏距离）取 top-1，
/// 同分按 itemId（<see cref="ObjectId"/>）升序稳定 tie-break；空候选或长度不符 → null（调用方降级中性）。
/// 数据集千级 × 八维内存精确计算，与 FLAT 等价且结果确定。
/// </summary>
public static class EmotionMatcher
{
    public static EmotionMatch? SelectTop1(IReadOnlyList<EmotionCandidate> candidates, float[] query)
    {
        if (candidates.Count == 0 || query.Length == 0)
        {
            return null;
        }

        EmotionCandidate? best = null;
        var bestDistance = double.MaxValue;
        foreach (var candidate in candidates)
        {
            if (candidate.Vector.Length != query.Length)
            {
                continue;
            }

            double distance = 0;
            for (var i = 0; i < query.Length; i++)
            {
                var diff = candidate.Vector[i] - query[i];
                distance += diff * diff;
            }

            if (best is null || distance < bestDistance || (distance == bestDistance && candidate.ItemId < best.ItemId))
            {
                best = candidate;
                bestDistance = distance;
            }
        }

        return best is null ? null : new EmotionMatch(best.ItemId, best.AudioKey);
    }
}
