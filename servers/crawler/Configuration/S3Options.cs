using System.ComponentModel.DataAnnotations;

namespace Z.Vtuber.Crawler.Configuration;

/// <summary>
/// 外部 S3 兼容对象存储配置（节 <c>S3</c>）。仅 <see cref="Enabled"/> 为 true 时校验必填项；
/// 关闭时 crawler 视频下载不启用缓存，保持原有回源透传行为。
/// </summary>
public sealed class S3Options : IValidatableObject
{
    public const string SectionName = "S3";

    /// <summary>是否启用 S3 视频缓存。</summary>
    public bool Enabled { get; set; }

    /// <summary>S3 兼容端点，如 <c>http://minio:9000</c> 或 <c>https://s3.amazonaws.com</c>。</summary>
    public string ServiceUrl { get; set; } = string.Empty;

    /// <summary>缓存对象所在 Bucket。</summary>
    public string Bucket { get; set; } = string.Empty;

    /// <summary>访问密钥。</summary>
    public string AccessKey { get; set; } = string.Empty;

    /// <summary>访问密钥 Secret。</summary>
    public string SecretKey { get; set; } = string.Empty;

    /// <summary>签名 Region，默认 <c>us-east-1</c>。</summary>
    public string Region { get; set; } = "us-east-1";

    /// <summary>是否使用 path-style 寻址（MinIO 等兼容存储需要 true）。</summary>
    public bool ForcePathStyle { get; set; } = true;

    /// <summary>缓存对象键前缀，默认 <c>video-cache</c>。</summary>
    public string Prefix { get; set; } = "video-cache";

    /// <summary>仅启用时要求端点/桶/凭据非空。</summary>
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (!Enabled)
        {
            yield break;
        }

        if (string.IsNullOrWhiteSpace(ServiceUrl))
        {
            yield return new ValidationResult("S3:ServiceUrl 在启用缓存时必填", [nameof(ServiceUrl)]);
        }

        if (string.IsNullOrWhiteSpace(Bucket))
        {
            yield return new ValidationResult("S3:Bucket 在启用缓存时必填", [nameof(Bucket)]);
        }

        if (string.IsNullOrWhiteSpace(AccessKey))
        {
            yield return new ValidationResult("S3:AccessKey 在启用缓存时必填", [nameof(AccessKey)]);
        }

        if (string.IsNullOrWhiteSpace(SecretKey))
        {
            yield return new ValidationResult("S3:SecretKey 在启用缓存时必填", [nameof(SecretKey)]);
        }
    }
}
