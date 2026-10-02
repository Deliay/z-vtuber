namespace Z.Vtuber.Training.ImageGeneration;

/// <summary>缺省解析无任何可用图片模型 → 映射 409 <c>no image model available</c>。</summary>
public sealed class NoImageModelAvailableException() : Exception("no image model available");
