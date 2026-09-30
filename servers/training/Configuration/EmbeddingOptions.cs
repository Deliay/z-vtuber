using System.ComponentModel.DataAnnotations;

namespace Z.Vtuber.Training.Configuration;

/// <summary>
/// 文本向量（embedding）服务配置（节 <c>Embedding</c>）——本地 Ollama
/// 「<c>https://ollama.internal.fffdan.com/</c>」，<c>/api/embeddings</c>（旧）或 <c>/api/embed</c>（新），
/// 亦支持 OpenAI 兼容 <c>/v1/embeddings</c>。不依赖三方 LLM API。
/// </summary>
public sealed class EmbeddingOptions
{
    public const string SectionName = "Embedding";

    public const string DefaultBaseUrl = "https://ollama.internal.fffdan.com/";

    /// <summary>本地 embedding 服务地址（默认本地 Ollama）。</summary>
    [Required]
    public string BaseUrl { get; set; } = DefaultBaseUrl;

    /// <summary>embedding 模型名（Ollama 已拉取，如 <c>bge-m3</c>）。</summary>
    [Required]
    public string Model { get; set; } = "bge-m3";

    /// <summary>文本向量维度；须与 Milvus 文本集合 dim 一致（<c>bge-m3</c>=1024）。</summary>
    [Range(1, 65536)]
    public int Dimensions { get; set; } = 1024;

    /// <summary>embedding 请求超时（秒）。</summary>
    [Range(1, 3600)]
    public int RequestTimeoutSeconds { get; set; } = 120;

    /// <summary>
    /// embedding 端点相对路径。默认 Ollama 旧端点 <c>/api/embeddings</c>（body 用 <c>prompt</c>，
    /// 响应 <c>{"embedding":[...]}</c>）；Ollama 新端点 <c>/api/embed</c> 与 OpenAI 兼容 <c>/v1/embeddings</c>
    /// 均用 <c>input</c>（<c>/api/embed</c> 传 <c>prompt</c> 会被忽略并返回空 <c>embeddings</c>）。
    /// </summary>
    [Required]
    public string ApiPath { get; set; } = "/api/embeddings";
}
