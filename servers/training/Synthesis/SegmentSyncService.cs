using MongoDB.Bson;
using Z.Vtuber.Common.Persistence;
using Z.Vtuber.Common.Persistence.Entities;

namespace Z.Vtuber.Training.Synthesis;

/// <summary>
/// 候选短句反向索引同步：<see cref="RebuildAsync"/> 清空数据集全部候选短句后按条目文本断句 + 拼音全量重建
/// （由 <c>segments/rebuild</c> 触发，失败原样上抛）；增量 <see cref="SyncItemAsync"/>（条目导入/更新）、
/// <see cref="DeleteItemsAsync"/>（批量删除）、<see cref="DeleteDatasetAsync"/>（数据集删除）为 best-effort
/// （失败仅 Warn，不阻断主流程，最终以 rebuild 对齐）。
/// </summary>
public sealed class SegmentSyncService(
    TrainingDatasetItemRepository items,
    TrainingDatasetItemSegmentRepository segments,
    ILogger<SegmentSyncService> logger)
{
    /// <summary>清空该数据集反向索引并全量重建，返回重建的候选短句总数。</summary>
    public async Task<long> RebuildAsync(ObjectId datasetId, CancellationToken ct = default)
    {
        await segments.DeleteByDatasetAsync(datasetId, ct);
        var records = await items.ListAsync(datasetId, ct: ct);

        var rebuilt = new List<TrainingDatasetItemSegmentRecord>();
        foreach (var record in records)
        {
            rebuilt.AddRange(BuildSegments(record));
        }

        await segments.InsertManyAsync(rebuilt, ct);
        logger.LogInformation(
            "Segment rebuild: datasetId={DatasetId} items={Items} rebuilt={Rebuilt}",
            datasetId, records.Count, rebuilt.Count);
        return rebuilt.Count;
    }

    /// <summary>单条目增量同步（删旧插新，best-effort，失败仅 Warn）。文本被清空时仅删除派生短句。</summary>
    public async Task SyncItemAsync(TrainingDatasetItemRecord record, CancellationToken ct = default)
    {
        try
        {
            await segments.ReplaceForItemAsync(
                record.DatasetId, record.Id, BuildSegments(record), ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex, "Segment sync failed for datasetId={DatasetId} itemId={ItemId}",
                record.DatasetId, record.Id);
        }
    }

    /// <summary>删除若干条目的候选短句（batch-delete 级联，best-effort）。</summary>
    public async Task DeleteItemsAsync(
        ObjectId datasetId, IReadOnlyCollection<ObjectId> itemIds, CancellationToken ct = default)
    {
        try
        {
            await segments.DeleteItemsAsync(datasetId, itemIds, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex, "Segment delete failed for datasetId={DatasetId} items={Items}",
                datasetId, itemIds.Count);
        }
    }

    /// <summary>删除数据集全部候选短句（数据集删除级联，best-effort）。</summary>
    public async Task DeleteDatasetAsync(ObjectId datasetId, CancellationToken ct = default)
    {
        try
        {
            await segments.DeleteByDatasetAsync(datasetId, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Segment delete failed for datasetId={DatasetId}", datasetId);
        }
    }

    /// <summary>
    /// 单条目文本 → 候选短句记录：文本空白 → 空；否则断句后逐条生成（保留 SegmentIndex 顺序，
    /// 拼音为无声调音节序列单空格 join）。
    /// </summary>
    internal static List<TrainingDatasetItemSegmentRecord> BuildSegments(TrainingDatasetItemRecord record)
    {
        var result = new List<TrainingDatasetItemSegmentRecord>();
        if (string.IsNullOrWhiteSpace(record.Text))
        {
            return result;
        }

        var sentences = TextSplitter.Split(record.Text);
        var now = DateTime.UtcNow;
        for (var index = 0; index < sentences.Count; index++)
        {
            var sentence = sentences[index];
            result.Add(new TrainingDatasetItemSegmentRecord
            {
                DatasetId = record.DatasetId,
                ItemId = record.Id,
                AudioKey = record.AudioKey,
                SegmentIndex = index,
                Text = sentence,
                Pinyin = PinyinConverter.ToPinyin(sentence),
                CreatedAt = now,
            });
        }

        return result;
    }
}
