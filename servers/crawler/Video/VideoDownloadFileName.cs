using System.Text;

namespace Z.Vtuber.Crawler.Video;

/// <summary>
/// 视频下载文件名纯函数：{Sanitize(标题,80,"未命名视频")}.p{page:D2}.{kind}.{ext}，
/// 并生成 <c>Content-Disposition: attachment; filename*=UTF-8''...</c>（RFC 5987 编码）。
/// 清理规则与 recording <c>RecordingFileName.Sanitize</c> 同款（服务间不互引，此处复制同款规则）。
/// </summary>
public static class VideoDownloadFileName
{
    public const int MaxTitleLength = 80;
    public const string DefaultTitle = "未命名视频";

    private static readonly char[] IllegalChars = ['\\', '/', ':', '*', '?', '"', '<', '>', '|'];

    /// <summary>按模板生成含标题、分 P、类型与扩展名的文件名（不含 Content-Disposition 包装）。</summary>
    public static string Build(string? title, int page, string kind, string extension)
    {
        var safeTitle = Sanitize(title, MaxTitleLength, DefaultTitle);
        return $"{safeTitle}.p{page:D2}.{kind}.{extension}";
    }

    /// <summary>生成 <c>Content-Disposition</c> 头值，使用 <c>filename*</c> 承载 UTF-8 文件名。</summary>
    public static string FormatContentDisposition(string fileName)
        => $"attachment; filename*=UTF-8''{Uri.EscapeDataString(fileName)}";

    /// <summary>清理非法文件名字符与控制字符，压缩空白并裁剪长度；空串回退 fallback。</summary>
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
