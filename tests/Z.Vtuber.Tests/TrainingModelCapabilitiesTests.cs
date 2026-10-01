using Z.Vtuber.Common.Contracts;

namespace Z.Vtuber.Tests;

/// <summary>
/// Plan 2.1：能力纯函数 <see cref="TrainingModelCapabilities"/>——
/// <c>Normalize</c>（trim/小写/去重/空白忽略/非法值抛异常）与 <c>Matches</c>（集合覆盖、空集语义）。
/// </summary>
public class TrainingModelCapabilitiesTests
{
    [Fact]
    public void Normalize_NullOrEmpty_ReturnsEmpty()
    {
        Assert.Empty(TrainingModelCapabilities.Normalize(null));
        Assert.Empty(TrainingModelCapabilities.Normalize(Array.Empty<string>()));
        Assert.Empty(TrainingModelCapabilities.Normalize(["", "  "]));
    }

    [Fact]
    public void Normalize_TrimsLowercasesDedupes_PreservingFirstOrder()
    {
        var result = TrainingModelCapabilities.Normalize([" Audio ", "TEXT", "audio", "text"]);

        Assert.Equal(new[] { "audio", "text" }, result.ToArray());
    }

    [Fact]
    public void Normalize_UnknownValue_ThrowsCapabilityException()
    {
        var ex = Assert.Throws<CapabilityException>(
            () => TrainingModelCapabilities.Normalize(["audio", "bogus"]));

        Assert.Equal("unsupported capability", ex.Message);
    }

    [Fact]
    public void Matches_EmptyRequirement_AlwaysSatisfied()
    {
        Assert.True(TrainingModelCapabilities.Matches([], []));
        Assert.True(TrainingModelCapabilities.Matches(["text"], []));
    }

    [Theory]
    [InlineData(new[] { "audio", "text" }, new[] { "audio", "text" }, true)]
    [InlineData(new[] { "audio", "video", "text" }, new[] { "audio", "text" }, true)]
    [InlineData(new[] { "audio" }, new[] { "audio", "text" }, false)]
    [InlineData(new[] { "text" }, new[] { "audio" }, false)]
    public void Matches_Coverage(string[] model, string[] required, bool expected)
    {
        Assert.Equal(expected, TrainingModelCapabilities.Matches(model, required));
    }

    [Fact]
    public void Matches_EmptyModelCapabilities_DoesNotMatchNonEmptyRequirement()
    {
        Assert.False(TrainingModelCapabilities.Matches([], ["text"]));
    }
}
