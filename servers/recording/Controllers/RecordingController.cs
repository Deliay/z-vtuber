using Microsoft.AspNetCore.Mvc;
using Z.Vtuber.Common.Persistence;
using Z.Vtuber.Common.Persistence.Entities;
using Z.Vtuber.Recording.Audio;
using Z.Vtuber.Recording.Configuration;

namespace Z.Vtuber.Recording.Controllers;

[ApiController]
[Route("api")]
public sealed class RecordingController(
    RecordingStatusRepository statusRepository,
    AudioStreamHub audio,
    RecordingOptions options) : ControllerBase
{
    [HttpGet("recording/status")]
    public async Task<IActionResult> GetStatus(CancellationToken ct)
    {
        var records = await statusRepository.GetAllAsync(ct);
        var items = records.Select(ToStatusDto).ToList();
        return Ok(new { items });
    }

    [HttpGet("{roomId:long}/recordings")]
    public async Task<IActionResult> ListRecordings(long roomId, CancellationToken ct)
    {
        var directory = RoomDirectory(roomId);
        if (!Directory.Exists(directory))
        {
            return Ok(new { files = Array.Empty<object>() });
        }

        var recordingFiles = await GetRecordingFilesAsync(roomId, ct);
        var entries = new List<(string Name, long Size, DateTime CreatedAt)>();

        foreach (var path in Directory.EnumerateFiles(directory, "*.flv"))
        {
            var name = Path.GetFileName(path);
            if (string.IsNullOrEmpty(name) || recordingFiles.Contains(name))
            {
                continue;
            }

            try
            {
                var info = new FileInfo(path);
                var size = info.Length;
                var createdAt = info.CreationTimeUtc;
                if (createdAt == default)
                {
                    createdAt = info.LastWriteTimeUtc;
                }

                entries.Add((name, size, createdAt));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 枚举与读取之间文件被清理：跳过该文件，不影响整体响应
            }
        }

        var files = entries
            .OrderBy(e => e.Name, StringComparer.Ordinal)
            .Select(e => new { name = e.Name, size = e.Size, createdAt = e.CreatedAt })
            .ToArray();

        return Ok(new { files });
    }

    [HttpGet("{roomId:long}/recordings/{fileName}")]
    public async Task<IActionResult> DownloadRecording(long roomId, string fileName, CancellationToken ct)
    {
        if (!IsSafeFileName(fileName))
        {
            return BadRequest(new { error = "invalid fileName" });
        }

        var directory = Path.GetFullPath(RoomDirectory(roomId));
        var fullPath = Path.GetFullPath(Path.Combine(directory, fileName));

        // 解析后必须落在房间存储目录内
        if (!fullPath.StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            return NotFound();
        }

        var recordingFiles = await GetRecordingFilesAsync(roomId, ct);
        if (recordingFiles.Contains(fileName))
        {
            return BadRequest(new { error = "file is still recording and cannot be downloaded" });
        }

        if (!System.IO.File.Exists(fullPath))
        {
            return NotFound();
        }

        return PhysicalFile(fullPath, "application/octet-stream", fileName, enableRangeProcessing: true);
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
