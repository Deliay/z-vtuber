using System.ComponentModel.DataAnnotations;

namespace Z.Vtuber.Training.Configuration;

/// <summary>
/// Milvus 向量库配置（节 <c>Milvus</c>）——向量索引承载（Milvus RESTful API v2，HttpClient 手写）。
/// 连接信息由 Aspire（<c>Aspire.Hosting.Milvus</c>）注入 <c>Milvus__Endpoint</c>/<c>Milvus__Token</c>。
/// </summary>
public sealed class MilvusOptions
{
    public const string SectionName = "Milvus";

    public const string DefaultEndpoint = "http://localhost:19530";

    /// <summary>Milvus HTTP(S) 端点（REST API v2，默认 19530）。</summary>
    [Required]
    public string Endpoint { get; set; } = DefaultEndpoint;

    /// <summary>数据库名（默认 <c>default</c>）。</summary>
    public string Database { get; set; } = "default";

    /// <summary>鉴权 token（<c>username:password</c>；空则不发送 Authorization）。</summary>
    public string? Token { get; set; }

    /// <summary>文本向量集合名。</summary>
    [Required]
    public string TextCollection { get; set; } = "item_text_embeddings";

    /// <summary>情感向量集合名。</summary>
    [Required]
    public string EmotionCollection { get; set; } = "item_emotion_vectors";

    /// <summary>文本向量检索的候选数（top-k，用于逐候选兜底；实际返回按相似度排序）。</summary>
    [Range(1, 1000)]
    public int SearchLimit { get; set; } = 16;

    /// <summary>Milvus 请求超时（秒）。</summary>
    [Range(1, 3600)]
    public int RequestTimeoutSeconds { get; set; } = 30;
}
