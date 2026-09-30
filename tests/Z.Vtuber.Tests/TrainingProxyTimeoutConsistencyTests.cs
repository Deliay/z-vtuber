using Z.Vtuber.Controllers;
using Z.Vtuber.Training.Configuration;

namespace Z.Vtuber.Tests;

/// <summary>
/// P2-2 回归：<c>servers/api</c> 代理 training 的请求超时必须**高于** training 各阶段上游预算，
/// 否则长耗时端点（synthesize 慢 TTS / vectors/rebuild 逐条 embedding）在 training 正常完成时被代理先判超时，
/// 成功路径被误报为 502 <c>training service unavailable</c>。
/// </summary>
public class TrainingProxyTimeoutConsistencyTests
{
    [Fact]
    public void ProxyDefaultTimeout_ExceedsEachTrainingStageBudget()
    {
        var stageBudgets = new (string Stage, int Seconds)[]
        {
            ("Tts", new TtsOptions().RequestTimeoutSeconds),
            ("Ai", new AiOptions().RequestTimeoutSeconds),
            ("Embedding", new EmbeddingOptions().RequestTimeoutSeconds),
            ("Milvus", new MilvusOptions().RequestTimeoutSeconds),
        };

        foreach (var (stage, seconds) in stageBudgets)
        {
            Assert.True(
                TrainingProxyController.DefaultTimeoutSeconds > seconds,
                $"TrainingService 代理超时 {TrainingProxyController.DefaultTimeoutSeconds}s 必须高于 {stage} 阶段预算 {seconds}s");
        }
    }
}
