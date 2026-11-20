using Z.Vtuber.Auth.OAuth;

namespace Z.Vtuber.Tests;

public sealed class PkceTests
{
    private static readonly string Verifier = new('a', 43);

    [Fact]
    public void S256_RoundTrips()
    {
        var challenge = Pkce.CreateChallenge(Verifier);
        Assert.True(Pkce.Validate(Verifier, challenge, Pkce.MethodS256));
    }

    [Fact]
    public void WrongVerifier_Fails()
    {
        var challenge = Pkce.CreateChallenge(Verifier);
        Assert.False(Pkce.Validate(new string('b', 43), challenge, Pkce.MethodS256));
    }

    [Fact]
    public void Plain_IsRejected()
    {
        Assert.False(Pkce.Validate(Verifier, Verifier, "plain"));
    }

    [Fact]
    public void MissingChallenge_IsAllowedOnlyWhenOptional()
    {
        Assert.True(Pkce.Validate(null, null, null));
        Assert.False(Pkce.Validate(null, null, null, required: true));
    }

    [Fact]
    public void VerifierLengthAndCharset_AreValidated()
    {
        var challenge = Pkce.CreateChallenge(Verifier);
        Assert.False(Pkce.Validate(new string('a', 42), challenge, Pkce.MethodS256));
        Assert.False(Pkce.Validate(new string('a', 42) + "!", challenge, Pkce.MethodS256));
    }
}
