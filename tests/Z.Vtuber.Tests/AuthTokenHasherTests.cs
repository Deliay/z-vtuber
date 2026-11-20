using Z.Vtuber.Auth.Auth;

namespace Z.Vtuber.Tests;

public sealed class AuthTokenHasherTests
{
    [Fact]
    public void Hash_IsStableForSamePepper()
    {
        var hasher = new TokenHasher(AuthTestOptions.Pepper);
        Assert.Equal(hasher.Hash("A1b2C3d4E5"), hasher.Hash("A1b2C3d4E5"));
    }

    [Fact]
    public void Hash_DiffersForDifferentPepper()
    {
        var first = new TokenHasher(AuthTestOptions.Pepper);
        var second = new TokenHasher("another-pepper-0123456789abcdef0123456789");
        Assert.NotEqual(first.Hash("A1b2C3d4E5"), second.Hash("A1b2C3d4E5"));
    }

    [Fact]
    public void Hash_DoesNotContainPlaintext_AndIsUrlSafe()
    {
        var hasher = new TokenHasher(AuthTestOptions.Pepper);
        var hash = hasher.Hash("A1b2C3d4E5");
        Assert.DoesNotContain("A1b2C3d4E5", hash);
        Assert.Matches("^[A-Za-z0-9_-]+$", hash);
    }

    [Fact]
    public void Verify_AndFixedTimeEquals_Work()
    {
        var hasher = new TokenHasher(AuthTestOptions.Pepper);
        var hash = hasher.Hash("A1b2C3d4E5");
        Assert.True(hasher.Verify("A1b2C3d4E5", hash));
        Assert.False(hasher.Verify("A1b2C3d4E6", hash));
        Assert.True(TokenHasher.FixedTimeEquals("abc", "abc"));
        Assert.False(TokenHasher.FixedTimeEquals("abc", "abd"));
        Assert.False(TokenHasher.FixedTimeEquals("abc", "ab"));
        Assert.False(TokenHasher.FixedTimeEquals(null, "abc"));
    }
}
