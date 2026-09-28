using MongoDB.Driver;

namespace Z.Vtuber.Tests;

internal static class TestIndexHelpers
{
    public static async Task<List<string>> ListIndexNamesAsync<T>(IMongoCollection<T> collection)
    {
        using var cursor = await collection.Indexes.ListAsync();
        var indexes = await cursor.ToListAsync();
        return indexes.Select(i => i["name"].AsString).ToList();
    }
}
