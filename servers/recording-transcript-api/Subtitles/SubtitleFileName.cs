using System.Text;

namespace Z.Vtuber.RecordingTranscript.Api.Subtitles;

/// <summary>
/// 字幕下载文件名：{sanitize(场次标题)}-{sanitize(模型名)}.{ext}，
/// 任一段为空兜底 transcript-{taskId 前 8 位}.{ext}。
/// sanitize 仅保留中英文/数字/-/_，其余折叠为单个 _（结果不含 CR/LF，防 Content-Disposition 头注入）。
/// </summary>
public static class SubtitleFileName
{
    public static string Build(string? title, string? modelName, string taskId, string extension)
    {
        var safeTitle = Sanitize(title);
        var safeModel = Sanitize(modelName);
        if (safeTitle.Length == 0 || safeModel.Length == 0)
        {
            return $"transcript-{TaskIdPrefix(taskId)}.{extension}";
        }

        return $"{safeTitle}-{safeModel}.{extension}";
    }

    /// <summary>仅保留中英文/数字/-/_，连续非法字符折叠为单个 _ 并去除两端 _。</summary>
    internal static string Sanitize(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            if (IsAllowed(ch))
            {
                builder.Append(ch);
            }
            else if (builder.Length > 0 && builder[^1] != '_')
            {
                builder.Append('_');
            }
        }

        return builder.ToString().Trim('_');
    }

    private static bool IsAllowed(char ch) =>
        ch is >= 'a' and <= 'z'
            or >= 'A' and <= 'Z'
            or >= '0' and <= '9'
            or '-'
            or '_'
            || IsCjkIdeograph(ch);

    /// <summary>CJK 汉字（基本区 + 扩展区 A + 兼容表意文字）。</summary>
    private static bool IsCjkIdeograph(char ch) =>
        ch is >= '\u3400' and <= '\u4DBF'
            or >= '\u4E00' and <= '\u9FFF'
            or >= '\uF900' and <= '\uFAFF';

    private static string TaskIdPrefix(string taskId) =>
        string.IsNullOrEmpty(taskId)
            ? string.Empty
            : taskId.Length <= 8 ? taskId : taskId[..8];
}
