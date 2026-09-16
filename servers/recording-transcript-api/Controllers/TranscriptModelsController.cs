using Microsoft.AspNetCore.Mvc;
using MongoDB.Bson;
using Z.Vtuber.Common.Persistence;
using Z.Vtuber.Common.Persistence.Entities;
using Z.Vtuber.RecordingTranscript.Api.Contracts;

namespace Z.Vtuber.RecordingTranscript.Api.Controllers;

[ApiController]
public class TranscriptModelsController(TranscriptModelRepository models) : ControllerBase
{
    [HttpGet("api/transcripts/models")]
    public async Task<IActionResult> List(CancellationToken ct = default)
    {
        var list = await models.ListAsync(ct);
        return Ok(new { items = list.Select(TranscriptMapping.ToModelDto).ToList() });
    }

    [HttpPost("api/transcripts/models")]
    public async Task<IActionResult> Create([FromBody] TranscriptModelCreateRequest? request, CancellationToken ct = default)
    {
        if (request is null
            || string.IsNullOrWhiteSpace(request.Name)
            || string.IsNullOrWhiteSpace(request.ApiKey))
        {
            return BadRequest(new ErrorResponse("name and apiKey are required"));
        }

        if (string.IsNullOrWhiteSpace(request.Provider) || string.IsNullOrWhiteSpace(request.Type))
        {
            return BadRequest(new ErrorResponse("provider and type are required"));
        }

        var model = new TranscriptModelRecord
        {
            Name = request.Name.Trim(),
            Provider = request.Provider.Trim(),
            Type = request.Type.Trim(),
            ApiKey = request.ApiKey,
            Enabled = request.Enabled ?? true,
            IsDefault = false,
            MaxConcurrency = Math.Max(1, request.MaxConcurrency ?? 1),
            CreatedAt = DateTime.UtcNow,
        };

        await models.InsertAsync(model, ct);

        if (request.IsDefault == true)
        {
            await models.SetDefaultAsync(model.Id, ct);
            model.IsDefault = true;
        }

        return Ok(TranscriptMapping.ToModelDto(model));
    }

    [HttpPut("api/transcripts/models/{modelId}")]
    public async Task<IActionResult> Update(
        string modelId,
        [FromBody] TranscriptModelUpdateRequest? request,
        CancellationToken ct = default)
    {
        if (!ObjectId.TryParse(modelId, out var id))
        {
            return NotFound(new ErrorResponse("model not found"));
        }

        var existing = await models.GetByIdAsync(id, ct);
        if (existing is null)
        {
            return NotFound(new ErrorResponse("model not found"));
        }

        if (request is null)
        {
            return BadRequest(new ErrorResponse("request body is required"));
        }

        await models.UpdateAsync(
            id,
            apiKey: request.ApiKey,
            enabled: request.Enabled,
            maxConcurrency: request.MaxConcurrency,
            ct: ct);

        if (request.IsDefault == true)
        {
            await models.SetDefaultAsync(id, ct);
        }
        else if (request.IsDefault == false)
        {
            await models.UpdateAsync(id, isDefault: false, ct: ct);
        }

        var updated = await models.GetByIdAsync(id, ct) ?? existing;
        return Ok(TranscriptMapping.ToModelDto(updated));
    }
}
