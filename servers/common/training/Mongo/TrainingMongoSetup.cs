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

        Logger.LogInformation("Ensured training collection indexes.");
    }
}
