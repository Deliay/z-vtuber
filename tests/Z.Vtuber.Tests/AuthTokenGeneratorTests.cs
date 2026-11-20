using Z.Vtuber.Auth.Auth;

namespace Z.Vtuber.Tests;

public sealed class AuthTokenGeneratorTests
{
    [Fact]
    public void DanmakuToken_IsTenCharsAndBelongsToUnambiguousAlphabet()
    {
        for (var i = 0; i < 200; i++)
        {
            var token = TokenGenerator.CreateDanmakuToken();
            Assert.Equal(10, token.Length);
            Assert.All(token, c => Assert.Contains(c, TokenGenerator.DanmakuAlphabet));
            Assert.Matches("^[A-Za-z0-9]{10}$", token);
        }
    }

    [Fact]
    public void DanmakuToken_AvoidsConfusingCharacters()
    {
        Assert.Equal(56, TokenGenerator.DanmakuAlphabet.Length);
        Assert.Equal(56, TokenGenerator.DanmakuAlphabet.Distinct().Count());
        var tokens = string.Concat(Enumerable.Range(0, 200).Select(_ => TokenGenerator.CreateDanmakuToken()));
        Assert.DoesNotContain('0', tokens);
        Assert.DoesNotContain('O', tokens);
        Assert.DoesNotContain('1', tokens);
        Assert.DoesNotContain('l', tokens);
        Assert.DoesNotContain('I', tokens);
        Assert.DoesNotContain('o', tokens);
    }

    [Fact]
    public void Tokens_AreUniqueEnough()
    {
        var tokens = Enumerable.Range(0, 500).Select(_ => TokenGenerator.CreateDanmakuToken()).ToHashSet();
        Assert.True(tokens.Count > 495);
    }

    [Fact]
    public void PollToken_HasPrefixAndBase64UrlPayload()
    {
        var token = TokenGenerator.CreatePollToken();
        Assert.StartsWith("p_", token);
        Assert.Equal(45, token.Length);
        Assert.Matches("^p_[A-Za-z0-9_-]+$", token);
    }

    [Fact]
    public void RequestId_IsParseableGuid()
    {
        var id = TokenGenerator.CreateRequestId();
        Assert.True(Guid.TryParse(id, out _));
        Assert.NotEqual(id, TokenGenerator.CreateRequestId());
    }

    [Fact]
    public void OpaqueToken_IsUrlSafe()
    {
        var token = TokenGenerator.CreateOpaqueToken();
        Assert.Equal(43, token.Length);
        Assert.Matches("^[A-Za-z0-9_-]+$", token);
    }
}
