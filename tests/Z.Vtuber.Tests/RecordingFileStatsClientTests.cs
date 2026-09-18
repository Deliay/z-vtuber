using Microsoft.Extensions.Logging.Abstractions;
using Z.Vtuber.RecordingTranscript.Api.Clients;

namespace Z.Vtuber.Tests;

public class RecordingFileStatsClientTests
{
    private static RecordingFileStatsClient CreateClient(EchoRecordingFileStatsHandler handler) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("http://recording/") },
            NullLogger<RecordingFileStatsClient>.Instance);

    [Fact]
    public async Task MoreThanLimitQueries_AreChunkedAndMerged()
    {
        var handler = new EchoRecordingFileStatsHandler();
        var client = CreateClient(handler);
        var queries = Enumerable.Range(0, 250)
            .Select(i => new RecordingFileStatsQuery(i, [$"f{i}.flv"]))
            .ToList();

        var stats = await client.GetFileStatsAsync(queries);

        // 250 个房间按每批 <=100 拆成 3 批
        Assert.Equal([100, 100, 50], handler.BatchItemCounts);
        Assert.Equal(250, stats.Count);
        Assert.All(stats.Values, stat => Assert.True(stat.Exists));
    }

    [Fact]
    public async Task EmptyQueries_DoNotCallUpstream()
    {
        var handler = new EchoRecordingFileStatsHandler();

        var stats = await CreateClient(handler).GetFileStatsAsync([]);

        Assert.Empty(stats);
        Assert.Empty(handler.BatchItemCounts);
    }

    [Fact]
    public async Task MissingFile_ReturnsNotExists()
    {
        var handler = new EchoRecordingFileStatsHandler();
        handler.Missing.Add((7, "[RAW] gone.flv"));

        var stats = await CreateClient(handler)
            .GetFileStatsAsync([new RecordingFileStatsQuery(7, ["[RAW] gone.flv", "[RAW] ok.flv"])]);

        Assert.False(stats[(7, "[RAW] gone.flv")].Exists);
        Assert.Null(stats[(7, "[RAW] gone.flv")].Size);
        Assert.True(stats[(7, "[RAW] ok.flv")].Exists);
    }
}
