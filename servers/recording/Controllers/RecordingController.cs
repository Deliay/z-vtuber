using Microsoft.AspNetCore.Mvc;
using Z.Vtuber.Common.Persistence;
using Z.Vtuber.Common.Persistence.Entities;
using Z.Vtuber.Recording.Audio;
using Z.Vtuber.Recording.Configuration;
using Z.Vtuber.Recording.Contracts;
using Z.Vtuber.Recording.Video;

namespace Z.Vtuber.Recording.Controllers;

[ApiController]
[Route("api")]
public sealed class RecordingController(
    RecordingStatusRepository statusRepository,
    RecordingRepository recordings,
    AudioStreamHub audio,
    RecordingOptions options,
    IAudioClipExtractor? clipExtractor = null,
    IAudioClipMerger? clipMerger = null,
    IVideoClipExtractor? videoClipExtractor = null) : ControllerBase
{
    internal const int MaxFileStatsItems = 100;

    [HttpGet("recording/status")]
    public async Task<IActionResult> GetStatus(CancellationToken ct)
    {
        var records = await statusRepository.GetAllAsync(ct);
        var items = records.Select(ToStatusDto).ToList();
        return Ok(new { items });
    }

    [HttpPost("recordings/file-stats")]
    public IActionResult GetFileStats([FromBody] RecordingFileStatsRequest? request)
    {
        if (request?.Items is null)
        {
            return BadRequest(new { error = "items is required" });
        }

        if (request.Items.Count > MaxFileStatsItems)
        {
            return BadRequest(new { error = $"items exceeds the limit of {MaxFileStatsItems}" });
        }

        var items = request.Items
            .Select(item =>
            {
                var directory = Path.GetFullPath(RoomDirectory(item.RoomId));
                var files = (item.Files ?? [])
                    .Select(name => ToFileStat(directory, name))
                    .ToArray();
                return new { roomId = item.RoomId, files };
            })
            .ToArray();

        return Ok(new { items });
    }

    [HttpGet("{roomId:long}/recordings")]
    public async Task<IActionResult> ListRecordings(long roomId, CancellationToken ct)
    {
        var directory = RoomDirectory(roomId);
        var sessions = await recordings.ListByRoomAsync(roomId, ct);
        var recordingFiles = await GetRecordingFilesAsync(roomId, ct);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var entries = new List<(string Name, long Size, DateTime CreatedAt, object Info)>();

        foreach (var session in sessions)
        {
            var info = new
            {
                recordingId = session.Id.ToString(),
                title = session.Title,
                mid = session.Mid,
                startedAt = session.StartedAt,
                endedAt = session.EndedAt,
            };

            foreach (var name in session.Files.OrderBy(f => f, StringComparer.Ordinal))
            {
                if (string.IsNullOrEmpty(name) || seen.Contains(name) || recordingFiles.Contains(name))
                {
                    continue;
                }

                var path = Path.Combine(directory, name);
                try
                {
                    var fileInfo = new FileInfo(path);
                    if (!fileInfo.Exists)
                    {
                        // 归档文件磁盘上已不存在（外部清理）：跳过
                        continue;
                    }

                    var size = fileInfo.Length;
                    var createdAt = fileInfo.CreationTimeUtc;
                    if (createdAt == default)
                    {
                        createdAt = fileInfo.LastWriteTimeUtc;
                    }

                    entries.Add((name, size, createdAt, info));
                    seen.Add(name);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // 查询与读取之间文件被清理：跳过该文件，不影响整体响应
                }
            }
        }

        var files = entries
            .Select(e => new { name = e.Name, size = e.Size, createdAt = e.CreatedAt, info = e.Info })
            .ToArray();

        return Ok(new { files });
    }

    [HttpGet("{roomId:long}/recordings/{fileName}")]
    public async Task<IActionResult> DownloadRecording(long roomId, string fileName, CancellationToken ct)
    {
        var (error, fullPath) = await ResolveRecordingFileAsync(roomId, fileName, ct);
        if (error is not null)
        {
            return error;
        }

        return PhysicalFile(fullPath, "application/octet-stream", fileName, enableRangeProcessing: true);
    }

    [HttpGet("{roomId:long}/recordings/{fileName}/audio")]
    public async Task<IActionResult> DownloadRecordingAudioClip(
        long roomId, string fileName, long? startMs, long? endMs, CancellationToken ct)
    {
        if (startMs is null || endMs is null)
        {
            return BadRequest(new { error = "startMs and endMs are required" });
        }

        if (startMs < 0)
        {
            return BadRequest(new { error = "startMs must be >= 0" });
        }

        if (endMs <= startMs)
        {
            return BadRequest(new { error = "endMs must be greater than startMs" });
        }

        if (endMs - startMs > options.AudioClipMaxDurationMs)
        {
            return BadRequest(new { error = $"clip duration must be <= {options.AudioClipMaxDurationMs} ms" });
        }

        var (error, fullPath) = await ResolveRecordingFileAsync(roomId, fileName, ct);
        if (error is not null)
        {
            return error;
        }

        var extractor = clipExtractor
            ?? throw new InvalidOperationException("IAudioClipExtractor is not configured");

        // 片段即最小播放单元：不支持 Range（忽略请求头，始终 200 整片段），每次现算不缓存
        Response.ContentType = extractor.ContentType;
        Response.Headers.CacheControl = "no-store";
        try
        {
            // -ss/-t 输入 seek + pipe:1 直写响应体；endMs 超文件长度由 ffmpeg 自然输出到结尾
            await extractor.ExtractAsync(fullPath, startMs.Value, endMs.Value, Response.Body, ct);
        }
        catch (AudioClipFailedException)
        {
            // 零字节失败说明尚未写出任何响应体：清掉音频头信息后按契约返回 500
            Response.ContentType = null;
            return StatusCode(StatusCodes.Status500InternalServerError, new { error = "audio clip failed" });
        }

        return new EmptyResult();
    }

    [HttpGet("{roomId:long}/recordings/{fileName}/clip")]
    public async Task<IActionResult> DownloadRecordingClip(
        long roomId, string fileName, long? startMs, long? endMs, CancellationToken ct)
    {
        if (startMs is null || endMs is null)
        {
            return BadRequest(new { error = "startMs and endMs are required" });
        }

        if (startMs < 0)
        {
            return BadRequest(new { error = "startMs must be >= 0" });
        }

        if (endMs <= startMs)
        {
            return BadRequest(new { error = "endMs must be greater than startMs" });
        }

        if (endMs - startMs > options.VideoClipMaxDurationMs)
        {
            return BadRequest(new { error = $"clip duration must be <= {options.VideoClipMaxDurationMs} ms" });
        }

        var (error, fullPath) = await ResolveRecordingFileAsync(roomId, fileName, ct);
        if (error is not null)
        {
            return error;
        }

        var extractor = videoClipExtractor
            ?? throw new InvalidOperationException("IVideoClipExtractor is not configured");

        // 片段即最小下载单元：不支持 Range（忽略请求头，始终 200 整片段），每次现算不缓存。
        // 文件名可能含中文/空格：ASCII FileName 兜底 + RFC 5987 FileNameStar 保留原名。
        var contentDisposition = new System.Net.Http.Headers.ContentDispositionHeaderValue("attachment")
        {
            FileName = $"clip-{startMs}-{endMs}.mp4",
            FileNameStar = $"{fileName}-{startMs}-{endMs}.mp4",
        };
        Response.ContentType = "video/mp4";
        Response.Headers.CacheControl = "no-store";
        Response.Headers.ContentDisposition = contentDisposition.ToString();

        try
        {
            // -ss/-t 输入 seek + -c copy + fMP4 直写响应体；起点吸附最近关键帧，endMs 超长自然输出到结尾
            await extractor.ExtractAsync(fullPath, startMs.Value, endMs.Value, Response.Body, ct);
        }
        catch (VideoClipFailedException)
        {
            // 零字节失败说明尚未写出任何响应体：清掉视频头信息后按契约返回 500
            Response.ContentType = null;
            Response.Headers.Remove("Content-Disposition");
            return StatusCode(StatusCodes.Status500InternalServerError, new { error = "video clip failed" });
        }

        return new EmptyResult();
    }

    [HttpPost("recordings/audio/merge")]
    public async Task<IActionResult> MergeRecordingAudioClips(
        [FromBody] RecordingAudioMergeRequest? request, CancellationToken ct)
    {
        var items = request?.Items;
        if (items is null || items.Count == 0)
        {
            return BadRequest(new { error = "items is required" });
        }

        if (items.Count > options.AudioMergeMaxItems)
        {
            return BadRequest(new { error = "too many items" });
        }

        var resolved = new List<AudioMergeClip>(items.Count);
        long totalMs = 0;

        // 先做全部参数校验（区间/单段上限/总时长），避免文件解析先于参数错误返回。
        foreach (var item in items)
        {
            if (item.StartMs < 0 || item.EndMs <= item.StartMs)
            {
                return BadRequest(new { error = "invalid time range" });
            }

            var duration = item.EndMs - item.StartMs;
            if (duration > options.AudioClipMaxDurationMs)
            {
                return BadRequest(new { error = "invalid time range" });
            }

            totalMs += duration;
            if (totalMs > options.AudioMergeMaxTotalDurationMs)
            {
                return BadRequest(new { error = "total duration too long" });
            }
        }

        // 再逐项解析文件（复用路径穿越防护与录制中判定）。
        foreach (var item in items)
        {
            var (error, fullPath) = await ResolveMergeFileAsync(item.RoomId, item.FileName, ct);
            if (error is not null)
            {
                return error;
            }

            resolved.Add(new AudioMergeClip(fullPath, item.StartMs, item.EndMs));
        }

        var merger = clipMerger
            ?? throw new InvalidOperationException("IAudioClipMerger is not configured");

        Response.ContentType = merger.ContentType;
        Response.Headers.CacheControl = "no-store";
        try
        {
            await merger.MergeAsync(resolved, Response.Body, ct);
        }
        catch (AudioMergeFailedException)
        {
            // 零字节失败说明尚未写出任何响应体：清掉音频头信息后按契约返回 500
            Response.ContentType = null;
            return StatusCode(StatusCodes.Status500InternalServerError, new { error = "audio merge failed" });
        }

        return new EmptyResult();
    }

    [HttpGet("{roomId:long}/stream/audio")]
    public async Task StreamAudio(long roomId, CancellationToken ct)
    {
        if (!audio.IsStreaming(roomId))
        {
            Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        Response.ContentType = "video/x-flv";
        Response.Headers.CacheControl = "no-store";

        using var subscription = audio.Subscribe(roomId);
        try
        {
            while (await subscription.WaitToReadAsync(ct))
            {
                var wrote = false;
                while (subscription.TryRead(out var chunk))
                {
                    await Response.Body.WriteAsync(chunk, ct);
                    wrote = true;
                }

                if (wrote)
                {
                    await Response.Body.FlushAsync(ct);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 客户端断开
        }
    }

    private string RoomDirectory(long roomId) => Path.Combine(options.StorageDir, roomId.ToString());

    /// <summary>
    /// 下载与音频片段共用的文件解析，顺序与原下载端点完全一致：
    /// IsSafeFileName 平文件名 400 → Path.GetFullPath 前缀校验 404 → 录制中 400 → File.Exists 404。
    /// 返回 (null, fullPath) 表示可访问。
    /// </summary>
    private async Task<(IActionResult? Error, string FullPath)> ResolveRecordingFileAsync(
        long roomId, string fileName, CancellationToken ct)
    {
        if (!IsSafeFileName(fileName))
        {
            return (BadRequest(new { error = "invalid fileName" }), string.Empty);
        }

        var directory = Path.GetFullPath(RoomDirectory(roomId));
        var fullPath = Path.GetFullPath(Path.Combine(directory, fileName));

        // 解析后必须落在房间存储目录内
        if (!fullPath.StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            return (NotFound(), string.Empty);
        }

        var recordingFiles = await GetRecordingFilesAsync(roomId, ct);
        if (recordingFiles.Contains(fileName))
        {
            return (BadRequest(new { error = "file is still recording and cannot be downloaded" }), string.Empty);
        }

        if (!System.IO.File.Exists(fullPath))
        {
            return (NotFound(), string.Empty);
        }

        return (null, fullPath);
    }

    /// <summary>
    /// 合并端点的文件解析（与下载/片段同防护，但错误体按合并契约）：非法文件名/越界/不存在 → 404
    /// <c>file not found</c>；文件正在录制中 → 400 <c>recording in progress</c>。
    /// </summary>
    private async Task<(IActionResult? Error, string FullPath)> ResolveMergeFileAsync(
        long roomId, string fileName, CancellationToken ct)
    {
        if (!IsSafeFileName(fileName))
        {
            return (NotFound(new { error = "file not found" }), string.Empty);
        }

        var directory = Path.GetFullPath(RoomDirectory(roomId));
        var fullPath = Path.GetFullPath(Path.Combine(directory, fileName));
        if (!fullPath.StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            return (NotFound(new { error = "file not found" }), string.Empty);
        }

        var recordingFiles = await GetRecordingFilesAsync(roomId, ct);
        if (recordingFiles.Contains(fileName))
        {
            return (BadRequest(new { error = "recording in progress" }), string.Empty);
        }

        if (!System.IO.File.Exists(fullPath))
        {
            return (NotFound(new { error = "file not found" }), string.Empty);
        }

        return (null, fullPath);
    }

    /// <summary>
    /// 当前场次「录制中」文件集合：state=recording 且心跳未超阈值时取当前场次产出文件名；否则为空集合。
    /// 与 recording-status 读侧陈旧退化口径一致。
    /// </summary>
    private async Task<HashSet<string>> GetRecordingFilesAsync(long roomId, CancellationToken ct)
    {
        var record = await statusRepository.GetByRoomIdAsync(roomId, ct);
        if (record is null || record.State != "recording")
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }

        if (DateTime.UtcNow - record.UpdatedAt > TimeSpan.FromSeconds(options.StaleStatusThresholdSeconds))
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }

        return new HashSet<string>(record.Files, StringComparer.Ordinal);
    }

    /// <summary>按磁盘权威判定单个文件存在性与大小；非法文件名/越界/IO 异常一律按不存在处理。</summary>
    private static object ToFileStat(string directory, string name)
    {
        if (!IsSafeFileName(name))
        {
            return new { name, exists = false, size = (long?)null };
        }

        var fullPath = Path.GetFullPath(Path.Combine(directory, name));
        if (!fullPath.StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            return new { name, exists = false, size = (long?)null };
        }

        try
        {
            var fileInfo = new FileInfo(fullPath);
            if (!fileInfo.Exists)
            {
                return new { name, exists = false, size = (long?)null };
            }

            return new { name, exists = true, size = (long?)fileInfo.Length };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new { name, exists = false, size = (long?)null };
        }
    }

    internal static bool IsSafeFileName(string fileName)
    {
        // 仅允许平文件名：拒绝空、路径分隔符与任何目录成分（".." 作为普通字符允许，由解析后前缀校验兜底）
        if (string.IsNullOrWhiteSpace(fileName)
            || fileName.Contains('/')
            || fileName.Contains('\\')
            || !string.Equals(Path.GetFileName(fileName), fileName, StringComparison.Ordinal))
        {
            return false;
        }

        return true;
    }

    private static object ToStatusDto(RecordingStatusRecord record) => new
    {
        roomId = record.RoomId,
        state = record.State,
        hostName = record.HostName,
        sessionTitle = record.SessionTitle,
        files = record.Files,
        error = record.Error,
        updatedAt = record.UpdatedAt,
    };
}
