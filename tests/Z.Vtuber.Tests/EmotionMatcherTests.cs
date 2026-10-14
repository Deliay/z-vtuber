using MongoDB.Bson;
using Z.Vtuber.Training.Synthesis;

namespace Z.Vtuber.Tests;

/// <summary>情绪参考内存匹配：L2 top-1、同分 itemId 升序稳定 tie-break、空候选/维度不符 → null。</summary>
public class EmotionMatcherTests
{
    private static EmotionCandidate Candidate(ObjectId id, params float[] vector) =>
        new(id, $"key-{id}", vector);

    [Fact]
    public void SelectTop1_PicksSmallestL2Distance()
    {
        var near = Candidate(ObjectId.Parse("000000000000000000000002"), [0.1f, 0.1f]);
        var far = Candidate(ObjectId.Parse("000000000000000000000001"), [0.9f, 0.9f]);

        var match = EmotionMatcher.SelectTop1([far, near], [0f, 0f]);

        Assert.NotNull(match);
        Assert.Equal(near.ItemId, match!.ItemId);
        Assert.Equal(near.AudioKey, match.AudioKey);
    }

    [Fact]
    public void SelectTop1_EqualDistance_TieBreaksByItemIdAscending()
    {
        var high = Candidate(ObjectId.Parse("000000000000000000000009"), [1f, 0f]);
        var low = Candidate(ObjectId.Parse("000000000000000000000001"), [-1f, 0f]);

        var match = EmotionMatcher.SelectTop1([high, low], [0f, 0f]);

        Assert.NotNull(match);
        Assert.Equal(low.ItemId, match!.ItemId);
    }

    [Fact]
    public void SelectTop1_EmptyCandidates_ReturnsNull()
    {
        Assert.Null(EmotionMatcher.SelectTop1([], [0f, 0f]));
    }

    [Fact]
    public void SelectTop1_EmptyQuery_ReturnsNull()
    {
        Assert.Null(EmotionMatcher.SelectTop1([Candidate(ObjectId.GenerateNewId(), [0f, 0f])], []));
    }

    [Fact]
    public void SelectTop1_DimensionMismatch_AllSkipped_ReturnsNull()
    {
        var candidate = Candidate(ObjectId.GenerateNewId(), [0f, 0f, 0f]);

        Assert.Null(EmotionMatcher.SelectTop1([candidate], [0f, 0f]));
    }

    [Fact]
    public void SelectTop1_IsDeterministic()
    {
        var candidates = new List<EmotionCandidate>
        {
            Candidate(ObjectId.Parse("000000000000000000000003"), [0.5f, 0.5f, 0.5f, 0f, 0f, 0f, 0f, 0f]),
            Candidate(ObjectId.Parse("000000000000000000000002"), [0.5f, 0.5f, 0.5f, 0f, 0f, 0f, 0f, 0f]),
            Candidate(ObjectId.Parse("000000000000000000000001"), [0.5f, 0.5f, 0.5f, 0f, 0f, 0f, 0f, 0f]),
        };
        float[] query = [0.5f, 0.5f, 0.5f, 0f, 0f, 0f, 0f, 0f];

        for (var i = 0; i < 5; i++)
        {
            var match = EmotionMatcher.SelectTop1(candidates, query);
            Assert.Equal(ObjectId.Parse("000000000000000000000001"), match!.ItemId);
        }
    }
}
