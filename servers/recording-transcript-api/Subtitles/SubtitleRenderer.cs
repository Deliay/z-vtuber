using System.Text;
using Z.Vtuber.RecordingTranscript.Api.Contracts;

namespace Z.Vtuber.RecordingTranscript.Api.Subtitles;

/// <summary>
/// SRT / ASS 字幕纯函数渲染（无 IO、可单测）。
/// 格式细节即契约：golden 断言锁定（SRT 小时两位起超 99 小时自然进位、
/// ASS 厘秒向下取整、<c>{</c>/<c>}</c> 与换行转义、固定文件头）。
/// 输出为字符串文本；UTF-8 with BOM 编码在字幕下载端点落字节时统一附加。
/// </summary>
public static class SubtitleRenderer
{
    /// <summary>ASS v4+ 固定文件头（Script Info / V4+ Styles / Events 表头）。</summary>
    private const string AssHeader =
        "[Script Info]\n" +
        "ScriptType: v4.00+\n" +
        "PlayResX: 1920\n" +
        "PlayResY: 1080\n" +
        "WrapStyle: 2\n" +
        "ScaledBorderAndShadow: yes\n" +
        "\n" +
        "[V4+ Styles]\n" +
        "Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding\n" +
        "Style: Default,Microsoft YaHei,64,&H00FFFFFF,&H000000FF,&H00000000,&H00000000,0,0,0,0,100,100,0,0,1,2,0,2,10,10,60,1\n" +
        "\n" +
        "[Events]\n" +
        "Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text\n";

    /// <summary>SubRip：条目 <c>序号\nHH:MM:SS,mmm --&gt; HH:MM:SS,mmm\n文本\n\n</c>，序号从 1 起。</summary>
    public static string RenderSrt(IReadOnlyList<TranscriptSegmentDto> segments)
    {
        var builder = new StringBuilder();
        for (var i = 0; i < segments.Count; i++)
        {
            var segment = segments[i];
            builder.Append(i + 1).Append('\n')
                .Append(FormatSrtTimestamp(segment.Start)).Append(" --> ")
                .Append(FormatSrtTimestamp(segment.End)).Append('\n')
                .Append(segment.Text).Append("\n\n");
        }

        return builder.ToString();
    }

    /// <summary>ASS v4+：固定文件头 + 逐条 Dialogue；空片段列表仅返回文件头。</summary>
    public static string RenderAss(IReadOnlyList<TranscriptSegmentDto> segments)
    {
        var builder = new StringBuilder(AssHeader);
        foreach (var segment in segments)
        {
            builder.Append("Dialogue: 0,")
                .Append(FormatAssTimestamp(segment.Start)).Append(',')
                .Append(FormatAssTimestamp(segment.End))
                .Append(",Default,,0,0,0,,")
                .Append(EscapeAss(segment.Text)).Append('\n');
        }

        return builder.ToString();
    }

    /// <summary>SRT 时间戳：<c>HH:MM:SS,mmm</c>（小时两位起，超 99 小时自然进位）。</summary>
    public static string FormatSrtTimestamp(long ms)
    {
        var total = Math.Max(0, ms);
        var hours = total / 3_600_000;
        var minutes = total / 60_000 % 60;
        var seconds = total / 1000 % 60;
        var millis = total % 1000;
        return FormattableString.Invariant($"{hours:00}:{minutes:00}:{seconds:00},{millis:000}");
    }

    /// <summary>ASS 时间戳：<c>H:MM:SS.cc</c>（厘秒向下取整）。</summary>
    public static string FormatAssTimestamp(long ms)
    {
        var total = Math.Max(0, ms);
        var hours = total / 3_600_000;
        var minutes = total / 60_000 % 60;
        var seconds = total / 1000 % 60;
        var centiseconds = total % 1000 / 10;
        return FormattableString.Invariant($"{hours}:{minutes:00}:{seconds:00}.{centiseconds:00}");
    }

    /// <summary>ASS 文本转义：<c>{</c>→<c>\{</c>、<c>}</c>→<c>\}</c>、换行→<c>\N</c>。</summary>
    private static string EscapeAss(string text) =>
        text.Replace("{", "\\{", StringComparison.Ordinal)
            .Replace("}", "\\}", StringComparison.Ordinal)
            .Replace("\r\n", "\\N", StringComparison.Ordinal)
            .Replace("\n", "\\N", StringComparison.Ordinal)
            .Replace("\r", "\\N", StringComparison.Ordinal);
}
