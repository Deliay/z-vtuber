using System.ComponentModel.DataAnnotations;

namespace Z.Vtuber.Training.Configuration;

/// <summary>绘图生成配置（节 <c>Image</c>）。</summary>
public sealed class ImageOptions
{
    public const string SectionName = "Image";

    /// <summary>上传参考图片字节上限（超过 → 400 <c>image too large</c>）。默认 10 MiB。</summary>
    [Range(1, 1_073_741_824)]
    public long MaxImageBytes { get; set; } = 10 * 1024 * 1024;

    /// <summary>图片生成请求超时（秒）；图像推理慢，默认 120s。代理 180s 预算更高。</summary>
    [Range(1, 3600)]
    public int RequestTimeoutSeconds { get; set; } = 120;
}
