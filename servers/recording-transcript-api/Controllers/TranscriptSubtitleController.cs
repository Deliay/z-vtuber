using System.Text;
using Microsoft.AspNetCore.Mvc;
using MongoDB.Bson;
using Z.Vtuber.Common.Persistence;
using Z.Vtuber.RecordingTranscript.Api.Contracts;
using Z.Vtuber.RecordingTranscript.Api.Subtitles;

namespace Z.Vtuber.RecordingTranscript.Api.Controllers;

/// <summary>
/// 字幕下载端点（契约：<c>contracts/recording-transcript.openapi.yaml /api/transcripts/task/{taskId}/subtitle</c>）。
/// 任意状态任务均可下载（按已入库片段渲染），无片段返回合法空字幕；
/// 输出 UTF-8 with BOM，<see cref="ControllerBase.File(byte[], string, string)"/> 由框架生成
/// Content-Disposition（含 <c>filename*</c>）。
/// </summary>
[ApiController]
public class TranscriptSubtitleController(
    RecordingRepository recordings,
    TranscriptTaskRepository tasks,
    TranscriptSegmentRepository segments,
    TranscriptModelRepository models) : ControllerBase
{
    internal const string SrtContentType = "application/x-subrip; charset=utf-8";
    internal const string AssContentType = "text/x-ass; charset=utf-8";

    [HttpGet("api/transcripts/task/{taskId}/subtitle")]
    public async Task<IActionResult> DownloadSubtitle(
        string taskId,
        [FromQuery(Name = "format")] string? format = null,
        CancellationToken ct = default)
    {
        string contentType;
        string extension;
        bool renderAss;
        switch (format)
        {
            case null or "" or "srt":
                contentType = SrtContentType;
                extension = "srt";
                renderAss = false;
                break;
            case "ass":
                contentType = AssContentType;
                extension = "ass";
                renderAss = true;
                break;
            default:
                return BadRequest(new ErrorResponse("unsupported format"));
        }

        if (!ObjectId.TryParse(taskId, out var id))
        {
            return NotFound(new ErrorResponse("task not found"));
        }

        var task = await tasks.GetByIdAsync(id, ct);
        if (task is null)
        {
            return NotFound(new ErrorResponse("task not found"));
        }

        var segmentList = await segments.ListByTaskAsync(id, ct);
        var dtos = segmentList.Select(TranscriptMapping.ToSegmentDto).ToList();
        var text = renderAss
            ? SubtitleRenderer.RenderAss(dtos)
            : SubtitleRenderer.RenderSrt(dtos);

        var recording = await recordings.GetByIdAsync(task.RecordingId, ct);
        var model = await models.GetByIdAsync(task.ModelId, ct);
        var fileDownloadName = SubtitleFileName.Build(recording?.Title, model?.Name, taskId, extension);

        var body = Encoding.UTF8.GetPreamble()
            .Concat(Encoding.UTF8.GetBytes(text))
            .ToArray();
        return File(body, contentType, fileDownloadName);
    }
}
