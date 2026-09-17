using Microsoft.AspNetCore.Mvc;
using Z.Vtuber.Common.Persistence;
using Z.Vtuber.RecordingTranscript.Api.Contracts;

namespace Z.Vtuber.RecordingTranscript.Api.Controllers;

[ApiController]
public class TranscriptSettingsController(
    TranscriptAutoEnqueueRepository autoEnqueue,
    TranscriptModelRepository models,
    AccountBasicInfoRepository accountInfo) : ControllerBase
{
    [HttpGet("api/transcripts/settings")]
    public async Task<IActionResult> ListSettings(CancellationToken ct = default)
    {
        var records = await autoEnqueue.GetAllAsync(ct);

        var mids = records.Where(r => r.Mid > 0).Select(r => r.Mid).Distinct().ToList();
        var infos = await accountInfo.GetLatestByMidsAsync(mids, ct);

        var modelById = (await models.ListAsync(ct)).ToDictionary(m => m.Id);

        var items = records
            .OrderByDescending(r => r.UpdatedAt)
            .Select(r =>
            {
                var anchorName = r.Mid > 0 && infos.TryGetValue(r.Mid, out var info)
                    ? TranscriptMapping.ExtractAnchorName(info.Data)
                    : null;
                var modelName = r.ModelId is { } modelId && modelById.TryGetValue(modelId, out var model)
                    ? model.Name
                    : null;
                return new TranscriptSettingsItemDto(
                    r.RoomId,
                    r.Mid > 0 ? r.Mid : null,
                    anchorName,
                    r.Enabled,
                    r.ModelId?.ToString(),
                    modelName);
            })
            .ToList();

        return Ok(new { items });
    }
}
