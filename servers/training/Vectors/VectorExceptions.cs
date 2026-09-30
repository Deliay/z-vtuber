namespace Z.Vtuber.Training.Vectors;

/// <summary>本地 embedding 服务不可用（不可达/超时/非 2xx/响应非法）→ 映射 502 <c>embedding service unavailable</c>。</summary>
public sealed class EmbeddingUnavailableException(string message, Exception? inner = null)
    : Exception(message, inner);

/// <summary>Milvus 向量库不可用（连接失败/请求异常/非预期响应）→ 映射 502 <c>vector store unavailable</c>。</summary>
public sealed class VectorStoreUnavailableException(string message, Exception? inner = null)
    : Exception(message, inner);
