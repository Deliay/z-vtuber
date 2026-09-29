using MongoDB.Driver;
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

        Logger.LogInformation("Ensured training collection indexes.");
    }
}
