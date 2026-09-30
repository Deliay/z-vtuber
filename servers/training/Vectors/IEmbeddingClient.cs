namespace Z.Vtuber.Training.Vectors;

/// <summary>
/// 文本向量（embedding）生成抽象：由本地 Ollama（<c>{Embedding:BaseUrl}/api/embeddings</c> 或 <c>/api/embed</c>）
/// 或 OpenAI 兼容端点产出；失败抛 <see cref="EmbeddingUnavailableException"/>（映射 502）。
/// </summary>
public interface IEmbeddingClient
{
    /// <summary>把文本转为向量；不可达/超时/非 2xx/响应非法 → <see cref="EmbeddingUnavailableException"/>。</summary>
    Task<float[]> GetEmbeddingAsync(string text, CancellationToken ct = default);
}
