using System.Net;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Z.Vtuber.Crawler.Configuration;

namespace Z.Vtuber.Crawler.Video;

/// <summary>
/// 基于 S3 兼容对象存储的 <see cref="IVideoObjectCache"/>：读路径 Head + Get（Range/If-Range/416），
/// 写路径 multipart（按 5MB 分片）。所有异常均降级——读返回 null（未命中）、写由调用方捕获，
/// 保证缓存故障不影响下载主链路。
/// </summary>
public sealed class S3VideoObjectCache : IVideoObjectCache
{
    private readonly IAmazonS3 _s3;
    private readonly string _bucket;
    private readonly string _prefix;
    private readonly ILogger<S3VideoObjectCache> _logger;

    public S3VideoObjectCache(IAmazonS3 s3, S3Options options, ILogger<S3VideoObjectCache> logger)
    {
        _s3 = s3;
        _bucket = options.Bucket;
        _prefix = options.Prefix.Trim('/');
        _logger = logger;
    }

    public bool Enabled => true;

    public async Task<VideoCacheRead?> TryReadAsync(VideoCacheKey key, string? range, string? ifRange, CancellationToken ct)
    {
        var objectKey = ObjectKey(key);

        GetObjectMetadataResponse head;
        try
        {
            head = await _s3.GetObjectMetadataAsync(new GetObjectMetadataRequest { BucketName = _bucket, Key = objectKey }, ct);
        }
        catch (AmazonS3Exception ex) when (IsNotFound(ex))
        {
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Video cache read (head) failed for {Key}; serving from upstream.", objectKey);
            return null;
        }

        var total = head.Headers?.ContentLength ?? 0;
        var contentType = head.ContentType ?? "application/octet-stream";
        var disposition = ReadDisposition(head.Metadata);
        var effectiveRange = range;
        if (!string.IsNullOrEmpty(ifRange) && !VideoCacheRange.IfRangeMatches(ifRange, head.ETag, ToDateTimeOffset(head.LastModified)))
        {
            // If-Range 校验失败：丢弃 Range，回退完整内容
            effectiveRange = null;
        }

        if (!string.IsNullOrEmpty(effectiveRange) && VideoCacheRange.TryParseStart(effectiveRange, out var start) && start >= total)
        {
            return new VideoCacheRead
            {
                StatusCode = StatusCodes.Status416RangeNotSatisfiable,
                ContentType = contentType,
                ContentRange = VideoCacheRange.NotSatisfiable(total),
            };
        }

        GetObjectResponse obj;
        try
        {
            obj = await _s3.GetObjectAsync(new GetObjectRequest
            {
                BucketName = _bucket,
                Key = objectKey,
                ByteRange = string.IsNullOrEmpty(effectiveRange) ? null : new ByteRange(effectiveRange),
            }, ct);
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            return new VideoCacheRead
            {
                StatusCode = StatusCodes.Status416RangeNotSatisfiable,
                ContentType = contentType,
                ContentRange = VideoCacheRange.NotSatisfiable(total),
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Video cache read (get) failed for {Key}; serving from upstream.", objectKey);
            return null;
        }

        var partial = !string.IsNullOrEmpty(effectiveRange);
        var contentRange = obj.ContentRange;
        if (contentRange is null && partial && VideoCacheRange.TryParseStart(effectiveRange, out var rangeStart) && obj.ContentLength is long length)
        {
            contentRange = VideoCacheRange.ContentRange(rangeStart, length, total);
        }

        return new VideoCacheRead
        {
            StatusCode = partial ? StatusCodes.Status206PartialContent : StatusCodes.Status200OK,
            Content = obj.ResponseStream,
            ContentType = obj.Headers?.ContentType ?? contentType,
            ContentDisposition = ReadDisposition(obj.Metadata) ?? disposition,
            ContentRange = contentRange,
            ContentLength = obj.Headers?.ContentLength ?? obj.ContentLength,
            AcceptRanges = "bytes",
        };
    }

    public async Task<IVideoCacheUpload> BeginUploadAsync(VideoCacheKey key, VideoCacheObjectMetadata metadata, CancellationToken ct)
    {
        var objectKey = ObjectKey(key);
        var initiate = new InitiateMultipartUploadRequest
        {
            BucketName = _bucket,
            Key = objectKey,
            ContentType = metadata.ContentType,
        };
        initiate.Metadata["content-disposition"] = metadata.ContentDisposition;

        var response = await _s3.InitiateMultipartUploadAsync(initiate, ct);
        return new S3VideoCacheUpload(_s3, _bucket, objectKey, response.UploadId, _logger);
    }

    private string ObjectKey(VideoCacheKey key)
        => string.IsNullOrEmpty(_prefix) ? key.ObjectKey : $"{_prefix}/{key.ObjectKey}";

    private static string? ReadDisposition(MetadataCollection? metadata)
    {
        if (metadata is null)
        {
            return null;
        }

        // MetadataCollection 索引器内部统一按 x-amz-meta- 前缀存取，缺失时返回 null
        var value = metadata["content-disposition"];
        return string.IsNullOrEmpty(value) ? null : value;
    }

    private static DateTimeOffset? ToDateTimeOffset(DateTime? value)
        => value is null
            ? null
            : new DateTimeOffset(DateTime.SpecifyKind(value.Value, DateTimeKind.Utc));

    private static bool IsNotFound(AmazonS3Exception ex)
        => ex.StatusCode == HttpStatusCode.NotFound
            || string.Equals(ex.ErrorCode, "NotFound", StringComparison.OrdinalIgnoreCase)
            || string.Equals(ex.ErrorCode, "NoSuchKey", StringComparison.OrdinalIgnoreCase);

    /// <summary>multipart 上传句柄：内部按最小分片大小缓冲并上传，完成/中止幂等。</summary>
    private sealed class S3VideoCacheUpload : IVideoCacheUpload
    {
        private const long MinPartSize = 5L * 1024 * 1024;

        private readonly IAmazonS3 _s3;
        private readonly string _bucket;
        private readonly string _key;
        private readonly string _uploadId;
        private readonly ILogger _logger;
        private readonly MemoryStream _buffer = new();
        private readonly List<PartETag> _parts = [];
        private int _partNumber = 1;
        private bool _finished;

        public S3VideoCacheUpload(IAmazonS3 s3, string bucket, string key, string uploadId, ILogger logger)
        {
            _s3 = s3;
            _bucket = bucket;
            _key = key;
            _uploadId = uploadId;
            _logger = logger;
        }

        public async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct)
        {
            _buffer.Write(buffer.Span);
            while (_buffer.Length >= MinPartSize)
            {
                await UploadBufferedPartAsync(ct);
            }
        }

        public async Task CompleteAsync(CancellationToken ct)
        {
            if (_finished)
            {
                return;
            }

            if (_buffer.Length > 0)
            {
                await UploadBufferedPartAsync(ct);
            }

            if (_parts.Count == 0)
            {
                await AbortAsync(ct);
                return;
            }

            _finished = true;
            await _s3.CompleteMultipartUploadAsync(new CompleteMultipartUploadRequest
            {
                BucketName = _bucket,
                Key = _key,
                UploadId = _uploadId,
                PartETags = _parts,
            }, ct);
        }

        public async Task AbortAsync(CancellationToken ct)
        {
            if (_finished)
            {
                return;
            }

            _finished = true;
            await _s3.AbortMultipartUploadAsync(new AbortMultipartUploadRequest
            {
                BucketName = _bucket,
                Key = _key,
                UploadId = _uploadId,
            }, ct);
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                if (!_finished)
                {
                    await AbortAsync(CancellationToken.None);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Video cache abort on dispose failed for {Key}.", _key);
            }
            finally
            {
                _buffer.Dispose();
            }
        }

        private async Task UploadBufferedPartAsync(CancellationToken ct)
        {
            var length = _buffer.Length;
            _buffer.Position = 0;
            var response = await _s3.UploadPartAsync(new UploadPartRequest
            {
                BucketName = _bucket,
                Key = _key,
                UploadId = _uploadId,
                PartNumber = _partNumber,
                InputStream = _buffer,
                PartSize = length,
            }, ct);

            _parts.Add(new PartETag(_partNumber, response.ETag));
            _partNumber++;
            _buffer.SetLength(0);
        }
    }
}
