using MongoDB.Driver;
using Z.Vtuber.Common.Persistence;
using Z.Vtuber.Common.Persistence.Entities;

namespace Z.Vtuber.RecordingTranscript.Consumer.Services;

/// <summary>
/// 存量 <c>transcript_models</c> 回填 <c>Model</c> 字段（升级迁移）：
/// 目标值为升级时全局配置 <c>TranscriptConsumer:AsrModel</c>（运行期实际生效值），缺省回退 <c>fun-asr-realtime</c>。
/// 沿用 <c>zvtuber_meta</c> marker + 幂等 + 回滚容错模式；已填记录不覆盖。
/// </summary>
public sealed class TranscriptModelMigration(
    IMongoDatabase database,
    IConfiguration configuration,
    ILogger<TranscriptModelMigration> logger)
{
    public const string MigrationKey = "transcript_model_field_backfill_v1";
    public const string DefaultModel = "fun-asr-realtime";

    public async Task MigrateAsync(CancellationToken ct = default)
    {
        var meta = database.GetCollection<MetaRecord>(MongoCollectionNames.Meta);
        var markerExists = await meta.Find(r => r.Key == MigrationKey).AnyAsync(ct);

        var models = database.GetCollection<TranscriptModelRecord>(MongoCollectionNames.TranscriptModels);
        var missingModelFilter = Builders<TranscriptModelRecord>.Filter.Or(
            Builders<TranscriptModelRecord>.Filter.Exists(m => m.Model, false),
            Builders<TranscriptModelRecord>.Filter.Eq(m => m.Model, string.Empty));
        var hasUnmigrated = await models.Find(missingModelFilter).AnyAsync(ct);

        if (markerExists && !hasUnmigrated)
        {
            logger.LogInformation("Transcript model field backfill already applied; skipping.");
            return;
        }

        var configured = configuration["TranscriptConsumer:AsrModel"];
        var modelName = string.IsNullOrWhiteSpace(configured) ? DefaultModel : configured.Trim();

        var result = await models.UpdateManyAsync(
            missingModelFilter,
            Builders<TranscriptModelRecord>.Update.Set(m => m.Model, modelName),
            cancellationToken: ct);

        logger.LogInformation(
            "Transcript model field backfill: matched {Matched}, modified {Modified}, model={Model}",
            result.MatchedCount, result.ModifiedCount, modelName);

        var markerFilter = Builders<MetaRecord>.Filter.Eq(r => r.Key, MigrationKey);
        await meta.ReplaceOneAsync(
            markerFilter, new MetaRecord { Key = MigrationKey }, new ReplaceOptions { IsUpsert = true }, ct);
    }
}
