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
    public IActionResult ListRecordings(long roomId)
    {
        var directory = RoomDirectory(roomId);
        if (!Directory.Exists(directory))
        {
            return Ok(new { files = Array.Empty<string>() });
        }

        var files = Directory.EnumerateFiles(directory, "*.flv")
            .Select(Path.GetFileName)
            .Where(name => !string.IsNullOrEmpty(name))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        return Ok(new { files });
    }

    [HttpGet("{roomId:long}/recordings/{fileName}")]
    public IActionResult DownloadRecording(long roomId, string fileName)
    {
        if (!IsSafeFileName(fileName))
        {
            return BadRequest(new { error = "invalid fileName" });
        }

        var directory = Path.GetFullPath(RoomDirectory(roomId));
        var fullPath = Path.GetFullPath(Path.Combine(directory, fileName));

        // 解析后必须落在房间存储目录内
        if (!fullPath.StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || !System.IO.File.Exists(fullPath))
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

    internal static bool IsSafeFileName(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)
            || fileName.Contains('/')
            || fileName.Contains('\\')
            || fileName.Contains("..", StringComparison.Ordinal)
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
