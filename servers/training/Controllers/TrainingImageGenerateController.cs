using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Z.Vtuber.Training.Ai;
using Z.Vtuber.Training.Configuration;
using Z.Vtuber.Training.Contracts;
using Z.Vtuber.Training.ImageGeneration;

namespace Z.Vtuber.Training.Controllers;

/// <summary>
/// 绘图生成 API（契约 <c>contracts/training.openapi.yaml</c>）：
/// <c>POST /api/training/images/generate</c>，<c>multipart/form-data</c> 输入
/// <c>text</c>（必填）+ <c>image</c>（参考图片 1..N 张，同名字段重复提交，必填）+ <c>modelId</c>（可选）→ 200 图片二进制；
/// 错误体统一 <c>{"error": "..."}</c>。输入校验（text→张数→逐张类型→逐张大小）在调用上游前完成，失败不发起模型调用。
/// </summary>
[ApiController]
[Route("api/training/images")]
public sealed class TrainingImageGenerateController(
    ImageGenerateService service,
    ImageOptions options,
    ILogger<TrainingImageGenerateController>? logger = null) : ControllerBase
{
    /// <summary>上传图片类型白名单（按 multipart part 的 Content-Type 判定，不做魔数嗅探）。</summary>
    internal static readonly string[] SupportedImageTypes = ["image/png", "image/jpeg", "image/webp"];

    private readonly ILogger<TrainingImageGenerateController> _logger =
        logger ?? NullLogger<TrainingImageGenerateController>.Instance;

    /// <summary>
    /// 绘图生成：text（400 <c>text is required</c>）→ 过滤 0 字节 part 后张数 ≥1（400 <c>image is required</c>）→
    /// 张数 ≤ <see cref="ImageOptions.MaxImageCount"/>（400 <c>too many images</c>）→
    /// 逐张类型白名单（400 <c>unsupported image type</c>）→ 逐张大小上限（400 <c>image too large</c>，Length 前置 + 读后复核）→
    /// <see cref="ImageGenerateService"/>（模型解析 + 调上游）→ 200 图片二进制。
    /// </summary>
    [HttpPost("generate")]
    [Consumes("multipart/form-data")]
    public async Task<IActionResult> Generate(
        [FromForm] string? text,
        [FromForm] IFormFileCollection? image,
        [FromForm] string? modelId,
        CancellationToken ct = default)
    {
        var prompt = text?.Trim();
        if (string.IsNullOrEmpty(prompt))
        {
            return BadRequest(new ErrorResponse("text is required"));
        }

        // 0 字节 part 一律剔除，不参与计数与后续校验（与既有 image.Length == 0 → image is required 口径一致）。
        var files = (image ?? new FormFileCollection()).Where(f => f.Length > 0).ToList();
        if (files.Count == 0)
        {
            return BadRequest(new ErrorResponse("image is required"));
        }

        if (files.Count > options.MaxImageCount)
        {
            return BadRequest(new ErrorResponse("too many images"));
        }

        // 第一趟：全量类型白名单（任一非法整体拒绝）。
        if (files.Any(f => !IsSupportedImageType(f.ContentType)))
        {
            return BadRequest(new ErrorResponse("unsupported image type"));
        }

        // 第二趟：全量大小上限（IFormFile.Length 前置拦截），读取后按实际字节数复核（双保险）。
        if (files.Any(f => f.Length > options.MaxImageBytes))
        {
            return BadRequest(new ErrorResponse("image too large"));
        }

        var inputs = new List<GenerateImageInput>(files.Count);
        foreach (var file in files)
        {
            byte[] bytes;
            await using (var buffer = new MemoryStream())
            {
                await file.CopyToAsync(buffer, ct);
                bytes = buffer.ToArray();
            }

            if (bytes.LongLength > options.MaxImageBytes)
            {
                return BadRequest(new ErrorResponse("image too large"));
            }

            inputs.Add(new GenerateImageInput(bytes, NormalizeContentType(file.ContentType), file.FileName));
        }

        try
        {
            var result = await service.GenerateAsync(prompt, modelId, inputs, ct);
            return File(result.Bytes, result.MimeType);
        }
        catch (NoImageModelAvailableException)
        {
            return StatusCode(StatusCodes.Status409Conflict, new ErrorResponse("no image model available"));
        }
        catch (ImageModelNotFoundException)
        {
            return NotFound(new ErrorResponse("model not found"));
        }
        catch (ImageModelDisabledException)
        {
            return BadRequest(new ErrorResponse("model disabled"));
        }
        catch (ImageModelInputNotSupportedException)
        {
            return BadRequest(new ErrorResponse("model does not support image input"));
        }
        catch (ImageModelOutputNotSupportedException)
        {
            return BadRequest(new ErrorResponse("model does not support image output"));
        }
        catch (ModelImageNotSupportedException)
        {
            // 派发层兜底：Claude-like Provider 无图片输出能力。
            return BadRequest(new ErrorResponse("model does not support image output"));
        }
        catch (ModelOutputException ex)
        {
            _logger.LogWarning(ex, "training image generate failed: invalid model output");
            return StatusCode(StatusCodes.Status502BadGateway, new ErrorResponse("invalid model output"));
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "training image generate failed: model service unavailable");
            return StatusCode(StatusCodes.Status502BadGateway, new ErrorResponse("model service unavailable"));
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return StatusCode(StatusCodes.Status502BadGateway, new ErrorResponse("model service unavailable"));
        }
    }

    /// <summary>类型白名单判定（忽略 <c>;</c> 后的参数，大小写不敏感）。</summary>
    internal static bool IsSupportedImageType(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType))
        {
            return false;
        }

        var normalized = contentType.Split(';')[0].Trim().ToLowerInvariant();
        return SupportedImageTypes.Contains(normalized, StringComparer.Ordinal);
    }

    private static string NormalizeContentType(string? contentType) =>
        string.IsNullOrWhiteSpace(contentType)
            ? "image/png"
            : contentType.Split(';')[0].Trim().ToLowerInvariant();
}
