using System.Globalization;
using Microsoft.AspNetCore.Http;

namespace Z.Vtuber.Crawler.Video;

/// <summary>视频缓存对象的逻辑键（不含存储前缀）：<c>videos/{bvid}/p{page:D2}/{video|audio}</c>。</summary>
public readonly record struct VideoCacheKey(string ObjectKey)
{
    /// <summary>按下载粒度（bvid + 分 P + 类型）生成缓存键；扩展名不入键，存于对象元数据。</summary>
    public static VideoCacheKey For(string bvid, int page, VideoStreamKind kind)
    {
        var kindName = kind == VideoStreamKind.Video ? "video" : "audio";
        return new VideoCacheKey($"videos/{bvid}/p{page:D2}/{kindName}");
    }
}

/// <summary>写入缓存对象时携带的元数据（供命中时还原响应头）。</summary>
public sealed record VideoCacheObjectMetadata(string ContentType, string ContentDisposition);

/// <summary>
/// 一次缓存命中的结果：状态码（200/206/416）、可流式返回的内容与响应头元数据。
/// <see cref="Content"/> 为 S3 对象流，由调用方随 <c>VideoDownloadResult</c> 释放；416 时为空。
/// </summary>
public sealed class VideoCacheRead
{
    public int StatusCode { get; init; } = StatusCodes.Status200OK;
    public Stream? Content { get; init; }
    public string ContentType { get; init; } = "application/octet-stream";
    public string? ContentDisposition { get; init; }
    public string? ContentRange { get; init; }
    public long? ContentLength { get; init; }
    public string? AcceptRanges { get; init; } = "bytes";
}

/// <summary>缓存命中路径的 Range/If-Range/416 纯函数语义（与 HTTP 对齐，便于单测）。</summary>
public static class VideoCacheRange
{
    /// <summary>
    /// 判定 If-Range 校验是否通过：日期形式要求资源未被修改（LastModified ≤ 该日期）；
    /// ETag 形式要求与当前对象 ETag 相等（忽略引号与弱校验前缀）。无法判定时返回 false（回退完整内容更安全）。
    /// </summary>
    public static bool IfRangeMatches(string? ifRange, string? etag, DateTimeOffset? lastModified)
    {
        if (string.IsNullOrWhiteSpace(ifRange))
        {
            return true;
        }

        var value = ifRange.Trim();

        if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date))
        {
            return lastModified is not null
                && lastModified.Value.ToUniversalTime() <= date.ToUniversalTime();
        }

        var normalized = NormalizeETag(value);
        return !string.IsNullOrEmpty(normalized)
            && lastModified is not null
            && string.Equals(normalized, NormalizeETag(etag), StringComparison.Ordinal);
    }

    /// <summary>解析单区间 <c>bytes=start-...</c> 的起始字节；多区间/后缀区间/非法格式返回 false。</summary>
    public static bool TryParseStart(string? range, out long start)
    {
        start = 0;
        if (string.IsNullOrWhiteSpace(range))
        {
            return false;
        }

        var value = range.Trim();
        if (!value.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var spec = value["bytes=".Length..].Trim();
        if (spec.Contains(',', StringComparison.Ordinal))
        {
            return false;
        }

        var dash = spec.IndexOf('-', StringComparison.Ordinal);
        if (dash <= 0)
        {
            return false;
        }

        return long.TryParse(spec[..dash], NumberStyles.None, CultureInfo.InvariantCulture, out start);
    }

    /// <summary>416 响应使用的 <c>Content-Range: bytes */{total}</c>。</summary>
    public static string NotSatisfiable(long total) => $"bytes */{total.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>按起始位置与分片长度拼装 <c>Content-Range</c>，如 <c>bytes 0-1023/20480</c>。</summary>
    public static string ContentRange(long start, long length, long total)
        => $"bytes {start.ToString(CultureInfo.InvariantCulture)}-{(start + length - 1).ToString(CultureInfo.InvariantCulture)}/{total.ToString(CultureInfo.InvariantCulture)}";

    private static string NormalizeETag(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var normalized = value.Trim();
        if (normalized.StartsWith("W/", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[2..];
        }

        return normalized.Trim().Trim('"').Trim();
    }
}
