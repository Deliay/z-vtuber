namespace Z.Vtuber.Crawler.Video;

/// <summary>
/// 视频对象缓存端口（cache-aside）。实现须「尽力而为」：读取不可用返回 null（按未命中处理），
/// 不得因缓存故障向调用方抛错阻断下载。
/// </summary>
public interface IVideoObjectCache
{
    /// <summary>是否启用缓存；false 时服务跳过所有缓存读写。</summary>
    bool Enabled { get; }

    /// <summary>
    /// 尝试读取缓存对象。命中返回 <see cref="VideoCacheRead"/>（200/206/416）；
    /// 未命中或缓存不可用返回 null。
    /// </summary>
    Task<VideoCacheRead?> TryReadAsync(VideoCacheKey key, string? range, string? ifRange, CancellationToken ct);

    /// <summary>开启一次完整对象写入（S3 multipart）。失败抛异常，由调用方降级为无缓存。</summary>
    Task<IVideoCacheUpload> BeginUploadAsync(VideoCacheKey key, VideoCacheObjectMetadata metadata, CancellationToken ct);
}

/// <summary>一次缓存对象写入的句柄：按块写入，读到完整内容后 Complete，异常/中断时 Abort。</summary>
public interface IVideoCacheUpload : IAsyncDisposable
{
    ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct);

    Task CompleteAsync(CancellationToken ct);

    Task AbortAsync(CancellationToken ct);
}
