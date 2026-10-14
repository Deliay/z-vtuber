using MongoDB.Driver;
using Z.Vtuber.Common.Persistence;
using Z.Vtuber.Common.Persistence.Entities;
using Z.Vtuber.Training.Ai;
using Z.Vtuber.Training.Ai.Prompts;
using Z.Vtuber.Training.Configuration;
using Z.Vtuber.Training.Contracts;

namespace Z.Vtuber.Training.Emotion;

/// <summary>
/// 单任务编排：读模型/Provider/条目 → 空文本 FAILED → 拼情绪 prompt → 调模型（JSON）→
/// 解析校验 → <c>SetEmotionAsync</c>（条目 <c>Extensions.emotion</c>）→ 任务 DONE + Emotion 快照 + FinishedAt；
/// 逐阶段 fencing（失锁放弃写入，保留 RUNNING 交他节点接管）；异常映射可读 FAILED；finally 释放并销毁锁。
/// 不在本类累加 <c>Attempts</c>（由扫描器领取时累加）。
/// </summary>
/// <remarks>非 sealed 且 <see cref="RunAsync"/> 为 virtual，便于扫描器测试注入记录型子类（生产按具体类型注册）。</remarks>
public class EmotionTaskRunner(
    TrainingEmotionTaskRepository tasks,
    TrainingDatasetItemRepository items,
    TrainingModelRepository models,
    TrainingProviderRepository providers,
    IChatModelClientFactory aiFactory,
    EmotionTaskOptions options,
    ILogger<EmotionTaskRunner> logger)
{
    public virtual async Task RunAsync(
        TrainingEmotionTaskRecord task, IEmotionTaskLock handle, CancellationToken ct = default)
    {
        if (handle.Lost)
        {
            logger.LogWarning("Emotion task {TaskId} lock already lost; abandoning run", task.Id);
            return;
        }

        handle.StartRenewal(ct);
        logger.LogDebug(
            "Running emotion task {TaskId} (attempt {Attempt}/{MaxAttempts})",
            task.Id, task.Attempts, options.MaxAttempts);
        try
        {
            var model = await models.GetByIdAsync(task.ModelId, ct);
            if (model is null)
            {
                await FailAsync(handle, task, "model not found", ct);
                return;
            }

            if (!model.Enabled)
            {
                // 与单条重新识别同口径：停用模型不得执行（入队校验被绕过时仍在此兜底）。
                await FailAsync(handle, task, "model disabled", ct);
                return;
            }

            var provider = await providers.GetByIdAsync(model.ProviderId, ct);
            if (provider is null)
            {
                await FailAsync(handle, task, "provider not found", ct);
                return;
            }

            var item = await items.GetByIdAsync(task.DatasetId, task.ItemId, ct);
            if (item is null)
            {
                await FailAsync(handle, task, "item not found", ct);
                return;
            }

            if (string.IsNullOrWhiteSpace(item.Text))
            {
                await FailAsync(handle, task, "item text is empty", ct);
                return;
            }

            var prompt = EmotionPromptBuilder.Build(item.Text);
            var raw = await aiFactory.Create(provider.Type).CompleteJsonAsync(provider, model, prompt, ct);

            // 写库前 fencing：失锁则放弃写入（保留 RUNNING 交新持有者接管）。
            if (!await OwnsLockAsync(handle, task, ct))
            {
                return;
            }

            EmotionResult emotion;
            try
            {
                emotion = ModelJsonParser.ParseEmotion(raw, logger);
            }
            catch (ModelOutputException ex)
            {
                logger.LogWarning(ex, "Emotion task {TaskId} produced invalid model output", task.Id);
                await FailAsync(handle, task, "invalid model output", ct);
                return;
            }

            var document = TrainingMapping.ToEmotionDocument(emotion, logger);

            // fencing 落到写操作：仅当任务仍是「本节点领取的 RUNNING（同 Attempts）」时，CAS 到 DONE
            // 才能成功；命中失败说明锁已易主/任务被接管，放弃写入（保留 RUNNING 交新持有者），杜绝双写 emotion。
            var doneUpdate = Builders<TrainingEmotionTaskRecord>.Update
                .Set(t => t.Status, TrainingEmotionTaskStatus.Done)
                .Set(t => t.Emotion, document)
                .Set(t => t.FinishedAt, DateTime.UtcNow)
                .Set(t => t.Error, null);
            var claimed = await tasks.UpdateRunningAsync(task.Id, task.Attempts, doneUpdate, ct);
            if (claimed is null)
            {
                logger.LogWarning(
                    "Emotion task {TaskId} no longer owned at write time; skipping emotion write", task.Id);
                return;
            }

            var updatedItem = await items.SetEmotionAsync(task.DatasetId, task.ItemId, document, ct);
            if (updatedItem is null)
            {
                // 条目执行期间被删除：不得留下「DONE 但无对应条目的情绪快照」，改标 FAILED。
                logger.LogWarning(
                    "Emotion task {TaskId} item {ItemId} missing at write time; marking failed",
                    task.Id, task.ItemId);
                var missingUpdate = Builders<TrainingEmotionTaskRecord>.Update
                    .Set(t => t.Status, TrainingEmotionTaskStatus.Failed)
                    .Set(t => t.Error, "item not found")
                    .Set(t => t.FinishedAt, DateTime.UtcNow);
                await tasks.UpdateAsync(task.Id, missingUpdate, ct);
                return;
            }

            logger.LogInformation("Emotion task {TaskId} completed", task.Id);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // 停机取消：不写终态，保留 RUNNING 交他节点接管
            logger.LogInformation("Emotion task {TaskId} cancelled", task.Id);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Emotion task {TaskId} failed", task.Id);
            await FailAsync(handle, task, MapError(ex), CancellationToken.None);
        }
        finally
        {
            // 释放与销毁各自独立 try，避免释放异常跳过 Dispose 形成僵尸锁
            try
            {
                await handle.ReleaseAsync();
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to release emotion task lock for {TaskId}", task.Id);
            }

            try
            {
                await handle.DisposeAsync();
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to dispose emotion task lock for {TaskId}", task.Id);
            }
        }
    }

    /// <summary>fencing 校验：锁已易主/Redis 抖动 → false，调用方不得写库。</summary>
    private async Task<bool> OwnsLockAsync(
        IEmotionTaskLock handle, TrainingEmotionTaskRecord task, CancellationToken ct)
    {
        if (await handle.CheckOwnershipAsync(ct))
        {
            return true;
        }

        logger.LogWarning(
            "Emotion task {TaskId} lost lock; leaving status RUNNING for another node to take over", task.Id);
        return false;
    }

    private async Task FailAsync(
        IEmotionTaskLock handle, TrainingEmotionTaskRecord task, string error, CancellationToken ct)
    {
        if (handle.Lost)
        {
            return;
        }

        if (!await OwnsLockAsync(handle, task, ct))
        {
            return;
        }

        var update = Builders<TrainingEmotionTaskRecord>.Update
            .Set(t => t.Status, TrainingEmotionTaskStatus.Failed)
            .Set(t => t.Error, error)
            .Set(t => t.FinishedAt, DateTime.UtcNow);
        // 同样带 Id+Status(RUNNING)+Attempts 条件：被接管后不得覆盖新持有者的状态。
        var updated = await tasks.UpdateRunningAsync(task.Id, task.Attempts, update, ct);
        if (updated is null)
        {
            logger.LogWarning("Emotion task {TaskId} no longer owned at failure write time", task.Id);
        }
    }

    private static string MapError(Exception ex) => ex switch
    {
        ModelAudioNotSupportedException => "model does not support audio input",
        ProviderTypeException => "unsupported provider type",
        HttpRequestException => "model service unavailable",
        TaskCanceledException => "model service unavailable",
        TimeoutException => "model service unavailable",
        // 不落库透传 ex.Message，避免泄露内部细节（Mongo/网络原始报文）。
        _ => "emotion task failed",
    };
}
