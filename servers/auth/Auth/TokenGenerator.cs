using System.Security.Cryptography;

namespace Z.Vtuber.Auth.Auth;

/// <summary>
/// 一次性口令与 opaque token 生成器。
/// 弹幕 token：10 位、去混淆 56 字符集（<c>[A-Za-z0-9]</c> 子集），约 58 bit 熵。
/// </summary>
public static class TokenGenerator
{
    /// <summary>去混淆字符集（去掉 0/O/1/l/I），仍属于 [A-Za-z0-9]。</summary>
    public const string DanmakuAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789";

    public const int DanmakuTokenLength = 10;

    public const string PollTokenPrefix = "p_";

    public static string CreateDanmakuToken()
    {
        Span<char> chars = stackalloc char[DanmakuTokenLength];
        for (var i = 0; i < chars.Length; i++)
        {
            chars[i] = DanmakuAlphabet[RandomNumberGenerator.GetInt32(DanmakuAlphabet.Length)];
        }

        return new string(chars);
    }

    /// <summary>32 字节随机数的 base64url 编码（无填充）。</summary>
    public static string CreateOpaqueToken() => Base64UrlEncode(RandomNumberGenerator.GetBytes(32));

    /// <summary>轮询凭证：<c>p_</c> + 43 字符 base64url。</summary>
    public static string CreatePollToken() => PollTokenPrefix + CreateOpaqueToken();

    /// <summary>UUIDv7 形态的请求 ID。</summary>
    public static string CreateRequestId() => Guid.CreateVersion7().ToString("D");

    public static string Base64UrlEncode(ReadOnlySpan<byte> bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
