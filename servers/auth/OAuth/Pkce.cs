using System.Security.Cryptography;
using System.Text;
using Z.Vtuber.Auth.Auth;

namespace Z.Vtuber.Auth.OAuth;

/// <summary>PKCE S256 校验；不接受 plain。</summary>
public static class Pkce
{
    public const string MethodS256 = "S256";

    public static string CreateChallenge(string verifier) =>
        TokenGenerator.Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    public static bool Validate(string? verifier, string? challenge, string? method, bool required = false)
    {
        if (string.IsNullOrEmpty(challenge))
        {
            return !required;
        }

        if (!string.Equals(method, MethodS256, StringComparison.Ordinal))
        {
            return false;
        }

        if (string.IsNullOrEmpty(verifier) || verifier.Length is < 43 or > 128)
        {
            return false;
        }

        if (!verifier.All(IsValidVerifierChar))
        {
            return false;
        }

        var computed = CreateChallenge(verifier);
        return TokenHasher.FixedTimeEquals(computed, challenge);
    }

    private static bool IsValidVerifierChar(char c) =>
        char.IsAsciiLetterOrDigit(c) || c is '-' or '.' or '_' or '~';
}
