namespace Z.Vtuber.Training.ImageGeneration;

/// <summary>显式 <c>modelId</c> 非法/不存在（或 Provider 缺失）→ 映射 404 <c>model not found</c>。</summary>
public sealed class ImageModelNotFoundException() : Exception("model not found");

/// <summary>显式 <c>modelId</c> 对应模型停用 → 映射 400 <c>model disabled</c>。</summary>
public sealed class ImageModelDisabledException() : Exception("model disabled");

/// <summary>显式 <c>modelId</c> 输入能力不覆盖 <c>text,image</c> → 映射 400 <c>model does not support image input</c>。</summary>
public sealed class ImageModelInputNotSupportedException()
    : Exception("model does not support image input");

/// <summary>显式 <c>modelId</c> 输出能力不含 <c>image</c> → 映射 400 <c>model does not support image output</c>。</summary>
public sealed class ImageModelOutputNotSupportedException()
    : Exception("model does not support image output");
