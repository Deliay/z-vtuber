using System.Net;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Logging.Abstractions;
using Z.Vtuber.Crawler.Configuration;
using Z.Vtuber.Crawler.Video;

namespace Z.Vtuber.Tests;

/// <summary>
/// <see cref="S3VideoObjectCache"/> 的读/写语义测试：以内存 S3 桩（继承 <see cref="AmazonS3Client"/> 覆写请求级方法）替代真实对象存储。
/// </summary>
public sealed class S3VideoObjectCacheTests
{
    private static readonly VideoCacheKey Key = VideoCacheKey.For("BV1xx411c7mD", 1, VideoStreamKind.Video);

    private static S3VideoObjectCache CreateCache(StubS3Client s3, string prefix = "video-cache")
        => new(s3, new S3Options { Enabled = true, Bucket = "bucket", Prefix = prefix }, NullLogger<S3VideoObjectCache>.Instance);

    [Fact]
    public async Task TryReadAsync_MissingObject_ReturnsNull()
    {
        var cache = CreateCache(new StubS3Client());

        Assert.Null(await cache.TryReadAsync(Key, null, null, CancellationToken.None));
    }

    [Fact]
    public async Task TryReadAsync_FullHit_ReturnsObjectWithMetadata()
    {
        var s3 = new StubS3Client();
        s3.Objects["video-cache/" + Key.ObjectKey] = new StubObject(
            [1, 2, 3], "video/mp4", "attachment; filename*=UTF-8''x.p01.video.m4v", "\"etag-1\"", DateTime.UtcNow);

        var read = await CreateCache(s3).TryReadAsync(Key, null, null, CancellationToken.None);

        Assert.NotNull(read);
        Assert.Equal(200, read!.StatusCode);
        Assert.Equal("video/mp4", read.ContentType);
        Assert.Equal("attachment; filename*=UTF-8''x.p01.video.m4v", read.ContentDisposition);
        Assert.Equal(3, read.ContentLength);
        using var buffer = new MemoryStream();
        await read.Content!.CopyToAsync(buffer);
        Assert.Equal(new byte[] { 1, 2, 3 }, buffer.ToArray());
    }

    [Fact]
    public async Task TryReadAsync_RangeHit_Returns206WithContentRange()
    {
        var s3 = new StubS3Client();
        s3.Objects["video-cache/" + Key.ObjectKey] = new StubObject(
            [0, 1, 2, 3, 4], "video/mp4", "disp", "\"etag-1\"", DateTime.UtcNow);

        var read = await CreateCache(s3).TryReadAsync(Key, "bytes=1-2", null, CancellationToken.None);

        Assert.NotNull(read);
        Assert.Equal(206, read!.StatusCode);
        Assert.Equal("bytes 1-2/5", read.ContentRange);
        Assert.Equal(2, read.ContentLength);
    }

    [Fact]
    public async Task TryReadAsync_RangeStartBeyondTotal_Returns416()
    {
        var s3 = new StubS3Client();
        s3.Objects["video-cache/" + Key.ObjectKey] = new StubObject([0, 1, 2], "video/mp4", "disp", "\"etag-1\"", DateTime.UtcNow);

        var read = await CreateCache(s3).TryReadAsync(Key, "bytes=99-", null, CancellationToken.None);

        Assert.NotNull(read);
        Assert.Equal(416, read!.StatusCode);
        Assert.Equal("bytes */3", read.ContentRange);
        Assert.Null(read.Content);
    }

    [Fact]
    public async Task TryReadAsync_IfRangeMismatch_DropsRangeAndReturnsFull()
    {
        var s3 = new StubS3Client();
        s3.Objects["video-cache/" + Key.ObjectKey] = new StubObject([0, 1, 2, 3, 4], "video/mp4", "disp", "\"etag-1\"", DateTime.UtcNow);

        var read = await CreateCache(s3).TryReadAsync(Key, "bytes=1-2", "\"stale\"", CancellationToken.None);

        Assert.NotNull(read);
        Assert.Equal(200, read!.StatusCode);
        Assert.Null(read.ContentRange);
        Assert.Equal(5, read.ContentLength);
    }

    [Fact]
    public async Task Upload_CompleteStoresObjectReadableByNextHit()
    {
        var s3 = new StubS3Client();
        var cache = CreateCache(s3);
        var payload = new byte[] { 10, 20, 30, 40 };

        await using (var upload = await cache.BeginUploadAsync(
            Key,
            new VideoCacheObjectMetadata("video/mp4", "attachment; filename*=UTF-8''x.p01.video.m4v"),
            CancellationToken.None))
        {
            await upload.WriteAsync(payload, CancellationToken.None);
            await upload.CompleteAsync(CancellationToken.None);
        }

        var read = await cache.TryReadAsync(Key, null, null, CancellationToken.None);

        Assert.NotNull(read);
        Assert.Equal(200, read!.StatusCode);
        Assert.Equal("video/mp4", read.ContentType);
        Assert.Contains(".p01.video.m4v", read.ContentDisposition);
        using var buffer = new MemoryStream();
        await read.Content!.CopyToAsync(buffer);
        Assert.Equal(payload, buffer.ToArray());
    }

    [Fact]
    public async Task Upload_AbortRemovesIncompleteUpload()
    {
        var s3 = new StubS3Client();
        var cache = CreateCache(s3);

        var upload = await cache.BeginUploadAsync(Key, new VideoCacheObjectMetadata("video/mp4", "disp"), CancellationToken.None);
        await upload.WriteAsync(new byte[] { 1, 2 }, CancellationToken.None);
        await upload.AbortAsync(CancellationToken.None);

        Assert.Empty(s3.Objects);
        Assert.Single(s3.Aborted);
    }

    private sealed record StubObject(byte[] Bytes, string ContentType, string Disposition, string ETag, DateTime LastModified);

    private sealed class StubS3Client : AmazonS3Client
    {
        private int _sequence;

        public StubS3Client()
            : base(new BasicAWSCredentials("key", "secret"), new AmazonS3Config
            {
                ServiceURL = "http://localhost:9000",
                ForcePathStyle = true,
                AuthenticationRegion = "us-east-1",
            })
        {
        }

        public Dictionary<string, StubObject> Objects { get; } = [];

        public Dictionary<string, UploadState> Uploads { get; } = [];

        public List<string> Aborted { get; } = [];

        public override Task<GetObjectMetadataResponse> GetObjectMetadataAsync(GetObjectMetadataRequest request, CancellationToken cancellationToken)
        {
            if (!Objects.TryGetValue(request.Key, out var obj))
            {
                throw new AmazonS3Exception("not found") { StatusCode = HttpStatusCode.NotFound, ErrorCode = "NoSuchKey" };
            }

            var response = new GetObjectMetadataResponse
            {
                ETag = obj.ETag,
                LastModified = obj.LastModified,
                ContentType = obj.ContentType,
            };
            response.Headers.ContentLength = obj.Bytes.Length;
            response.Metadata["content-disposition"] = obj.Disposition;
            return Task.FromResult(response);
        }

        public override Task<GetObjectResponse> GetObjectAsync(GetObjectRequest request, CancellationToken cancellationToken)
        {
            var obj = Objects[request.Key];
            var start = 0L;
            var end = (long)obj.Bytes.Length - 1;
            if (request.ByteRange is not null)
            {
                var spec = request.ByteRange.FormattedByteRange["bytes=".Length..];
                var dash = spec.IndexOf('-');
                start = long.Parse(spec[..dash]);
                end = dash < spec.Length - 1 ? long.Parse(spec[(dash + 1)..]) : end;
            }

            var slice = obj.Bytes[(int)start..(int)(end + 1)];
            var response = new GetObjectResponse
            {
                ResponseStream = new MemoryStream(slice),
                ContentRange = request.ByteRange is null ? null : $"bytes {start}-{end}/{obj.Bytes.Length}",
            };
            response.Headers.ContentLength = slice.Length;
            response.Headers.ContentType = obj.ContentType;
            response.Metadata["content-disposition"] = obj.Disposition;
            return Task.FromResult(response);
        }

        public override Task<InitiateMultipartUploadResponse> InitiateMultipartUploadAsync(InitiateMultipartUploadRequest request, CancellationToken cancellationToken)
        {
            var uploadId = $"upload-{Interlocked.Increment(ref _sequence)}";
            Uploads[uploadId] = new UploadState
            {
                Key = request.Key,
                ContentType = request.ContentType,
                Disposition = request.Metadata["content-disposition"],
            };
            return Task.FromResult(new InitiateMultipartUploadResponse { UploadId = uploadId });
        }

        public override async Task<UploadPartResponse> UploadPartAsync(UploadPartRequest request, CancellationToken cancellationToken)
        {
            var state = Uploads[request.UploadId];
            var buffer = new byte[(int)(request.PartSize ?? 0)];
            var offset = 0;
            while (offset < buffer.Length)
            {
                var read = await request.InputStream.ReadAsync(buffer.AsMemory(offset), cancellationToken);
                if (read == 0)
                {
                    break;
                }

                offset += read;
            }

            state.Parts[request.PartNumber ?? 0] = buffer[..offset];
            return new UploadPartResponse { ETag = $"part-{request.PartNumber}" };
        }

        public override Task<CompleteMultipartUploadResponse> CompleteMultipartUploadAsync(CompleteMultipartUploadRequest request, CancellationToken cancellationToken)
        {
            var state = Uploads[request.UploadId];
            var bytes = state.Parts.OrderBy(pair => pair.Key).SelectMany(pair => pair.Value).ToArray();
            Objects[state.Key] = new StubObject(bytes, state.ContentType, state.Disposition, $"\"{request.UploadId}\"", DateTime.UtcNow);
            Uploads.Remove(request.UploadId);
            return Task.FromResult(new CompleteMultipartUploadResponse());
        }

        public override Task<AbortMultipartUploadResponse> AbortMultipartUploadAsync(AbortMultipartUploadRequest request, CancellationToken cancellationToken)
        {
            Uploads.Remove(request.UploadId);
            Aborted.Add(request.UploadId);
            return Task.FromResult(new AbortMultipartUploadResponse());
        }

        public sealed class UploadState
        {
            public string Key { get; init; } = string.Empty;
            public string ContentType { get; init; } = string.Empty;
            public string? Disposition { get; init; }
            public SortedDictionary<int, byte[]> Parts { get; } = [];
        }
    }
}
