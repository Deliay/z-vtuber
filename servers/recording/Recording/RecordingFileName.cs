using System.Text;

namespace Z.Vtuber.Recording.Recording;

/// <summary>
/// 录播文件名模板纯函数：
/// [{encode}][{roomId}][{anchorName}] {yyyy-MM-dd}-{sanitize(sessionTitle)}.flv
/// </summary>
public static class RecordingFileName
{
    public const string RawEncode = "RAW";
    public const string Av1Encode = "AV1";

    private const int MaxTitleLength = 80;
    private const int MaxAnchorLength = 60;
    private static readonly char[] IllegalChars = ['\\', '/', ':', '*', '?', '"', '<', '>', '|'];

    public static string Render(string encode, long roomId, string? anchorName, DateTime date, string? sessionTitle)
    {
        var normalizedEncode = (encode ?? string.Empty).ToUpperInvariant();
        var anchor = Sanitize(anchorName, MaxAnchorLength, "未知主播");
        var title = Sanitize(sessionTitle, MaxTitleLength, "未命名场次");
        return $"[{normalizedEncode}][{roomId}][{anchor}]{date:yyyy-MM-dd-hh-mm-ss}-{title}.flv";
    }

    /// <summary>
    /// 清理非法文件名字符与控制字符，压缩空白并裁剪长度；空串回退 fallback。
    /// </summary>
    public static string Sanitize(string? input, int maxLength, string fallback)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return fallback;
        }

        var builder = new StringBuilder(input.Length);
        foreach (var ch in input)
        {
            if (char.IsControl(ch) || IllegalChars.Contains(ch))
            {
                continue;
            }

            builder.Append(ch);
        }

        var cleaned = CollapseWhitespace(builder.ToString()).Trim();
        if (cleaned.Length == 0)
        {
            return fallback;
        }

        return cleaned.Length > maxLength ? cleaned[..maxLength].TrimEnd() : cleaned;
    }

    private static string CollapseWhitespace(string value)
    {
        var builder = new StringBuilder(value.Length);
        var previousSpace = false;
        foreach (var ch in value)
        {
            var isSpace = char.IsWhiteSpace(ch);
            if (isSpace)
            {
                if (!previousSpace)
                {
                    builder.Append(' ');
                }
            }
            else
            {
                builder.Append(ch);
            }

            previousSpace = isSpace;
        }

        return builder.ToString();
    }
}
