using Microsoft.Extensions.Logging;

namespace Z.Vtuber.Crawler.Video;

/// <summary>
/// 边读上游原始流边写入缓存的包装流：每次读到字节即追加写入 <see cref="IVideoCacheUpload"/>，
/// 读到 EOF 时完成对象写入；读异常/取消时中止写入。缓存写入失败仅告警并停止写入，不影响向调用方透传。
/// 未完成写入在释放时兜底中止，避免残留未完成 multipart。
/// </summary>
public sealed class CachingReadStream : Stream, IAsyncDisposable
{
    private readonly Stream _source;
    private readonly IVideoCacheUpload _upload;
    private readonly ILogger _logger;
    private bool _finished;
    private bool _disposed;

    public CachingReadStream(Stream source, IVideoCacheUpload upload, ILogger logger)
    {
        _source = source;
        _upload = upload;
        _logger = logger;
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        int read;
        try
        {
            read = await _source.ReadAsync(buffer, cancellationToken);
        }
        catch
        {
            await AbortAsync();
            throw;
        }

        if (read == 0)
        {
            await CompleteAsync(cancellationToken);
            return 0;
        }

        if (!_finished)
        {
            try
            {
                await _upload.WriteAsync(buffer[..read], cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Video cache write failed; continuing to stream without cache.");
                await AbortAsync();
            }
        }

        return read;
    }

    public override int Read(byte[] buffer, int offset, int count)
        => ReadAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await AbortAsync();
        _source.Dispose();
        await _upload.DisposeAsync();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        base.Dispose(disposing);
    }

    private async Task CompleteAsync(CancellationToken ct)
    {
        if (_finished)
        {
            return;
        }

        _finished = true;
        try
        {
            await _upload.CompleteAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Video cache complete failed; object not cached.");
        }
    }

    private async Task AbortAsync()
    {
        if (_finished)
        {
            return;
        }

        _finished = true;
        try
        {
            await _upload.AbortAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Video cache abort failed; an incomplete upload may remain.");
        }
    }
}
