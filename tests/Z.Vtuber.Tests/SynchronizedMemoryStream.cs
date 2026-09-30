using System.Text;

namespace Z.Vtuber.Tests;

/// <summary>
/// 线程安全的内存响应体：SSE 用例可在被测流写入的同时轮询快照，
/// 从而以「等到期望内容出现」替代固定 <c>Task.Delay</c>。
/// </summary>
internal sealed class SynchronizedMemoryStream : Stream
{
    private readonly MemoryStream _inner = new();
    private readonly object _gate = new();

    public string Snapshot()
    {
        lock (_gate)
        {
            return Encoding.UTF8.GetString(_inner.ToArray());
        }
    }

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => true;

    public override long Length
    {
        get
        {
            lock (_gate)
            {
                return _inner.Length;
            }
        }
    }

    public override long Position
    {
        get
        {
            lock (_gate)
            {
                return _inner.Position;
            }
        }
        set
        {
            lock (_gate)
            {
                _inner.Position = value;
            }
        }
    }

    public override void Flush()
    {
    }

    public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public override int Read(byte[] buffer, int offset, int count)
    {
        lock (_gate)
        {
            return _inner.Read(buffer, offset, count);
        }
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        lock (_gate)
        {
            return _inner.Seek(offset, origin);
        }
    }

    public override void SetLength(long value)
    {
        lock (_gate)
        {
            _inner.SetLength(value);
        }
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        lock (_gate)
        {
            _inner.Write(buffer, offset, count);
        }
    }

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        lock (_gate)
        {
            _inner.Write(buffer);
        }
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _inner.Write(buffer, offset, count);
        }

        return Task.CompletedTask;
    }

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            _inner.Write(buffer.Span);
        }

        return ValueTask.CompletedTask;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            lock (_gate)
            {
                _inner.Dispose();
            }
        }

        base.Dispose(disposing);
    }
}
