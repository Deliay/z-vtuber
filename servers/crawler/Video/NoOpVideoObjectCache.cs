namespace Z.Vtuber.Crawler.Video;

/// <summary>未启用 S3 缓存时的空实现；<see cref="Enabled"/> 恒为 false，方法不参与主链路。</summary>
public sealed class NoOpVideoObjectCache : IVideoObjectCache
{
    public bool Enabled => false;

    public Task<VideoCacheRead?> TryReadAsync(VideoCacheKey key, string? range, string? ifRange, CancellationToken ct)
        => Task.FromResult<VideoCacheRead?>(null);

    public Task<IVideoCacheUpload> BeginUploadAsync(VideoCacheKey key, VideoCacheObjectMetadata metadata, CancellationToken ct)
        => throw new NotSupportedException("Video cache is disabled.");
}
