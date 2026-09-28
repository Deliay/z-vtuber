using MongoDB.Bson;
using MongoDB.Driver;
using Z.Vtuber.Common.Persistence.Entities;

namespace Z.Vtuber.Common.Persistence;

public sealed class TranscriptSegmentRepository(IMongoDatabase database)
{
    private readonly IMongoCollection<TranscriptSegmentRecord> _collection =
        database.GetCollection<TranscriptSegmentRecord>(TranscriptCollectionNames.TranscriptSegments);

    public Task InsertAsync(
        TranscriptSegmentRecord record, IClientSessionHandle session, CancellationToken ct = default) =>
        _collection.InsertOneAsync(session, record, cancellationToken: ct);

    public Task InsertAsync(TranscriptSegmentRecord record, CancellationToken ct = default) =>
        _collection.InsertOneAsync(record, cancellationToken: ct);

    public async Task<IReadOnlyList<TranscriptSegmentRecord>> ListByTaskAsync(
        ObjectId taskId, CancellationToken ct = default) =>
        await _collection.Find(s => s.TaskId == taskId)
            .SortBy(s => s.StartTime)
            .ToListAsync(ct);
}
