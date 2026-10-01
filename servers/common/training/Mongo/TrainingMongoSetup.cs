using MongoDB.Bson;
using MongoDB.Driver;
using Z.Vtuber.Common.Contracts;
using Z.Vtuber.Common.Persistence.Entities;

namespace Z.Vtuber.Common.Persistence;

public sealed class TrainingMongoSetup(IMongoDatabase database, ILogger<TrainingMongoSetup> logger)
    : MongoDbInitializer(database, logger)
{
    public async Task EnsureIndexesAsync(CancellationToken ct = default)
    {
        // training_datasets：(Type, Name) 唯一（重名天然 409）
        var datasets = Database.GetCollection<TrainingDatasetRecord>(TrainingCollectionNames.TrainingDatasets);
        var datasetNameKeys = Builders<TrainingDatasetRecord>.IndexKeys
            .Ascending(d => d.Type)
            .Ascending(d => d.Name);
        await datasets.Indexes.CreateOneAsync(
            new CreateIndexModel<TrainingDatasetRecord>(
                datasetNameKeys, new CreateIndexOptions { Unique = true, Name = "Type_1_Name_1" }),
            cancellationToken: ct);

        // training_dataset_items：(DatasetId, AudioKey) 唯一（导入去重与标识唯一性基础）
        var items = Database.GetCollection<TrainingDatasetItemRecord>(TrainingCollectionNames.TrainingDatasetItems);
        var audioKeyKeys = Builders<TrainingDatasetItemRecord>.IndexKeys
            .Ascending(i => i.DatasetId)
            .Ascending(i => i.AudioKey);
        await items.Indexes.CreateOneAsync(
            new CreateIndexModel<TrainingDatasetItemRecord>(
                audioKeyKeys, new CreateIndexOptions { Unique = true, Name = "DatasetId_1_AudioKey_1" }),
            cancellationToken: ct);

        // 分组/过滤查询
        var audioTypeKeys = Builders<TrainingDatasetItemRecord>.IndexKeys
            .Ascending(i => i.DatasetId)
            .Ascending(i => i.AudioType);
        await items.Indexes.CreateOneAsync(
            new CreateIndexModel<TrainingDatasetItemRecord>(
                audioTypeKeys, new CreateIndexOptions { Name = "DatasetId_1_AudioType_1" }),
            cancellationToken: ct);

        var sourceKeys = Builders<TrainingDatasetItemRecord>.IndexKeys
            .Ascending(i => i.DatasetId)
            .Ascending(i => i.Source);
        await items.Indexes.CreateOneAsync(
            new CreateIndexModel<TrainingDatasetItemRecord>(
                sourceKeys, new CreateIndexOptions { Name = "DatasetId_1_Source_1" }),
            cancellationToken: ct);

        // training_providers：Name 唯一（重名天然 409）
        var providers = Database.GetCollection<TrainingProviderRecord>(TrainingCollectionNames.TrainingProviders);
        var providerNameKeys = Builders<TrainingProviderRecord>.IndexKeys.Ascending(p => p.Name);
        await providers.Indexes.CreateOneAsync(
            new CreateIndexModel<TrainingProviderRecord>(
                providerNameKeys, new CreateIndexOptions { Unique = true, Name = "Name_1" }),
            cancellationToken: ct);

        // training_models：(ProviderId, Model) 唯一；ProviderId（级联删除）
        var models = Database.GetCollection<TrainingModelRecord>(TrainingCollectionNames.TrainingModels);
        var modelUniqueKeys = Builders<TrainingModelRecord>.IndexKeys
            .Ascending(m => m.ProviderId)
            .Ascending(m => m.Model);
        await models.Indexes.CreateOneAsync(
            new CreateIndexModel<TrainingModelRecord>(
                modelUniqueKeys, new CreateIndexOptions { Unique = true, Name = "ProviderId_1_Model_1" }),
            cancellationToken: ct);

        var modelProviderKeys = Builders<TrainingModelRecord>.IndexKeys.Ascending(m => m.ProviderId);
        await models.Indexes.CreateOneAsync(
            new CreateIndexModel<TrainingModelRecord>(
                modelProviderKeys, new CreateIndexOptions { Name = "ProviderId_1" }),
            cancellationToken: ct);

        // training_emotion_tasks：(Status, CreatedAt) 扫描领取；DatasetId 队列查询；ItemId 重复提交判定
        var emotionTasks = Database.GetCollection<TrainingEmotionTaskRecord>(
            TrainingCollectionNames.TrainingEmotionTasks);
        var statusCreatedKeys = Builders<TrainingEmotionTaskRecord>.IndexKeys
            .Ascending(t => t.Status)
            .Ascending(t => t.CreatedAt);
        await emotionTasks.Indexes.CreateOneAsync(
            new CreateIndexModel<TrainingEmotionTaskRecord>(
                statusCreatedKeys, new CreateIndexOptions { Name = "Status_1_CreatedAt_1" }),
            cancellationToken: ct);

        var datasetCreatedKeys = Builders<TrainingEmotionTaskRecord>.IndexKeys
            .Ascending(t => t.DatasetId)
            .Ascending(t => t.CreatedAt);
        await emotionTasks.Indexes.CreateOneAsync(
            new CreateIndexModel<TrainingEmotionTaskRecord>(
                datasetCreatedKeys, new CreateIndexOptions { Name = "DatasetId_1_CreatedAt_1" }),
            cancellationToken: ct);

        var itemKeys = Builders<TrainingEmotionTaskRecord>.IndexKeys.Ascending(t => t.ItemId);
        await emotionTasks.Indexes.CreateOneAsync(
            new CreateIndexModel<TrainingEmotionTaskRecord>(
                itemKeys, new CreateIndexOptions { Name = "ItemId_1" }),
            cancellationToken: ct);

        // 活跃任务幂等：同一条目最多一个 PENDING/RUNNING 任务（部分唯一索引）——
        // 并发重复提交由唯一键冲突兜底计 skipped，取代纯 check-then-act。
        var activeItemKeys = Builders<TrainingEmotionTaskRecord>.IndexKeys.Ascending(t => t.ItemId);
        await emotionTasks.Indexes.CreateOneAsync(
            new CreateIndexModel<TrainingEmotionTaskRecord>(
                activeItemKeys,
                new CreateIndexOptions<TrainingEmotionTaskRecord>
                {
                    Unique = true,
                    Name = "ItemId_1_Active",
                    PartialFilterExpression = Builders<TrainingEmotionTaskRecord>.Filter.In(
                        t => t.Status,
                        new[] { TrainingEmotionTaskStatus.Pending, TrainingEmotionTaskStatus.Running }),
                }),
            cancellationToken: ct);

        // training_feature_points：Slug 唯一（登记 upsert 的身份键）
        var featurePoints = Database.GetCollection<TrainingFeaturePointRecord>(
            TrainingCollectionNames.TrainingFeaturePoints);
        var featurePointSlugKeys = Builders<TrainingFeaturePointRecord>.IndexKeys.Ascending(f => f.Slug);
        await featurePoints.Indexes.CreateOneAsync(
            new CreateIndexModel<TrainingFeaturePointRecord>(
                featurePointSlugKeys, new CreateIndexOptions { Unique = true, Name = "Slug_1" }),
            cancellationToken: ct);

        // training_dataset_item_segments：(DatasetId) 按数据集加载/清空/级联；(DatasetId, ItemId) 条目级替换/删除。
        // 匹配为内存相似度计算（非等值检索），故不建 Pinyin 等值索引。
        var segments = Database.GetCollection<TrainingDatasetItemSegmentRecord>(
            TrainingCollectionNames.TrainingDatasetItemSegments);
        var datasetSegmentKeys = Builders<TrainingDatasetItemSegmentRecord>.IndexKeys
            .Ascending(s => s.DatasetId);
        await segments.Indexes.CreateOneAsync(
            new CreateIndexModel<TrainingDatasetItemSegmentRecord>(
                datasetSegmentKeys, new CreateIndexOptions { Name = "DatasetId_1" }),
            cancellationToken: ct);

        var datasetItemSegmentKeys = Builders<TrainingDatasetItemSegmentRecord>.IndexKeys
            .Ascending(s => s.DatasetId)
            .Ascending(s => s.ItemId);
        await segments.Indexes.CreateOneAsync(
            new CreateIndexModel<TrainingDatasetItemSegmentRecord>(
                datasetItemSegmentKeys, new CreateIndexOptions { Name = "DatasetId_1_ItemId_1" }),
            cancellationToken: ct);

        Logger.LogInformation("Ensured training collection indexes.");
    }

    /// <summary>存量模型能力清洗的迁移 marker（<c>zvtuber_meta</c>）。</summary>
    public const string ModelCapabilitiesBackfillMigrationKey = "training_model_capabilities_backfill_v1";

    /// <summary>存量模型能力清洗补齐的输入能力（PRD：输入「语音、文本」）。</summary>
    private static readonly string[] BackfillInputCapabilities =
        [TrainingModelCapabilities.Audio, TrainingModelCapabilities.Text];

    /// <summary>存量模型能力清洗补齐的输出能力（PRD：输出「文本」）。</summary>
    private static readonly string[] BackfillOutputCapabilities = [TrainingModelCapabilities.Text];

    /// <summary>
    /// 存量模型能力一次性清洗（幂等）：<c>training_models</c> 中 <c>InputCapabilities</c>/<c>OutputCapabilities</c>
    /// **字段缺失**的文档逐侧 <c>$set</c> 为输入 <c>["audio","text"]</c>/输出 <c>["text"]</c>，
    /// 只补缺失字段、**不覆盖**已设置的能力（PRD：不覆盖用户已手动调整的能力）。
    /// 用 BsonDocument 级 <c>Exists</c> 判定（强类型无法表达字段缺失），marker 落 <c>zvtuber_meta</c>，
    /// 已存在即跳过；marker 在更新成功后写入，重复执行零副作用。
    /// </summary>
    public async Task BackfillModelCapabilitiesAsync(CancellationToken ct = default)
    {
        if (await IsMigrationAppliedAsync(ModelCapabilitiesBackfillMigrationKey, ct))
        {
            Logger.LogInformation("Training model capabilities backfill already applied; skipping.");
            return;
        }

        var collection = Database.GetCollection<BsonDocument>(TrainingCollectionNames.TrainingModels);
        var inputFilter = Builders<BsonDocument>.Filter.Exists("InputCapabilities", false);
        var outputFilter = Builders<BsonDocument>.Filter.Exists("OutputCapabilities", false);
        var inputUpdate = Builders<BsonDocument>.Update.Set(
            "InputCapabilities", new BsonArray(BackfillInputCapabilities));
        var outputUpdate = Builders<BsonDocument>.Update.Set(
            "OutputCapabilities", new BsonArray(BackfillOutputCapabilities));

        var inputResult = await collection.UpdateManyAsync(inputFilter, inputUpdate, cancellationToken: ct);
        var outputResult = await collection.UpdateManyAsync(outputFilter, outputUpdate, cancellationToken: ct);

        Logger.LogInformation(
            "Training model capabilities backfill filled input {InputModified} and output {OutputModified} documents.",
            inputResult.ModifiedCount, outputResult.ModifiedCount);

        await MarkMigrationAppliedAsync(ModelCapabilitiesBackfillMigrationKey, ct);
        Logger.LogInformation("Training model capabilities backfill completed and marked as applied.");
    }
}
