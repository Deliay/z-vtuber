using System.Security.Cryptography;
using System.Text;

namespace Z.Vtuber.Auth.Auth;

/// <summary>token / poll token / ticket / code / access token 的 HMAC-SHA256 摘要。</summary>
public sealed class TokenHasher
{
    private readonly byte[] _pepper;

    public TokenHasher(IOptions<Configuration.AuthOptions> options)
        : this(options.Value.TokenPepper)
    {
    }

    public TokenHasher(string pepper)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pepper);
        _pepper = Encoding.UTF8.GetBytes(pepper);
    }

    public string Hash(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var digest = HMACSHA256.HashData(_pepper, Encoding.UTF8.GetBytes(value));
        return TokenGenerator.Base64UrlEncode(digest);
    }

    /// <summary>恒定时间比较两个摘要/明文；长度不同直接返回 false。</summary>
    public static bool FixedTimeEquals(string? left, string? right)
    {
        if (left is null || right is null)
        {
            return false;
        }

        var leftBytes = Encoding.UTF8.GetBytes(left);
        var rightBytes = Encoding.UTF8.GetBytes(right);
        return leftBytes.Length == rightBytes.Length &&
               CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
    }

    public bool Verify(string value, string expectedHash) =>
        FixedTimeEquals(Hash(value), expectedHash);
}
