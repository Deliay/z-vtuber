using System.IO.Compression;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using Z.Vtuber.Common.Persistence;
using Z.Vtuber.Common.Persistence.Entities;
using Z.Vtuber.Training.Ai;
using Z.Vtuber.Training.Clients;
using Z.Vtuber.Training.Contracts;
using Z.Vtuber.Training.Controllers;

namespace Z.Vtuber.Tests;

/// <summary>
/// 数据集导出 zip 端点（<c>TrainingDatasetsController.Export</c>）控制器直测（Testcontainers Mongo）——
/// 连续编号成对文件 / mp3 与 txt 内容 / 顺序与列表一致 / 失败与降级 wav 跳过不占编号 /
/// 全失败空包仍 200 / 数据集 404 / 中文名 Content-Disposition 编码 / endMs=null 透传。
/// </summary>
public class TrainingDatasetExportApiTests(MongoDbFixture fixture)
    : IClassFixture<MongoDbFixture>, IAsyncLifetime
{
    private const string AudioType = "audio";

    public Task InitializeAsync() => fixture.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private TrainingDatasetRepository Datasets => new(fixture.Database);

    private TrainingDatasetItemRepository Items => new(fixture.Database);

    private TrainingDatasetsController CreateController(ExportStubAudioClient audio) =>
        WithContext(new TrainingDatasetsController(
            Datasets, Items, audio, NullLogger<TrainingDatasetsController>.Instance));

    private static T WithContext<T>(T controller) where T : ControllerBase
    {
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { Response = { Body = new MemoryStream() } },
        };
        return controller;
    }

    private async Task<TrainingDatasetRecord> SeedDatasetAsync(string name)
    {
        var now = DateTime.UtcNow;
        return await Datasets.InsertAsync(new TrainingDatasetRecord
        {
            Name = name,
            Type = AudioType,
            CreatedAt = now,
            UpdatedAt = now,
        });
    }

    private async Task SeedItemAsync(
        ObjectId datasetId,
        string audioUri,
        string text,
        string audioKey,
        long startMs = 0,
        long? endMs = 30000,
        DateTime? createdAt = null)
    {
        var now = createdAt ?? DateTime.UtcNow;
        await Items.InsertManyIgnoreDuplicatesAsync(
        [
            new TrainingDatasetItemRecord
            {
                DatasetId = datasetId,
                AudioType = "transcript",
                Source = "task-1",
                AudioUri = audioUri,
                AudioKey = audioKey,
                StartMs = startMs,
                EndMs = endMs,
                Text = text,
                CreatedAt = now,
                UpdatedAt = now,
            },
        ]);
    }

    /// <summary>把响应体当 zip 读回：条目名 → 原始字节（保持条目顺序）。</summary>
    private static List<(string Name, byte[] Bytes)> ReadZip(Stream body)
    {
        body.Position = 0;
        using var zip = new ZipArchive(body, ZipArchiveMode.Read, leaveOpen: true);
        var result = new List<(string, byte[])>();
        foreach (var entry in zip.Entries)
        {
            using var stream = entry.Open();
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            result.Add((entry.FullName, buffer.ToArray()));
        }

        return result;
    }

    // ---- 成功导出：连续编号 / 成对 / 内容 / 顺序 ----

    [Fact]
    public async Task Export_MultipleItems_WritesPairedContinuousNumberedEntries()
    {
        var dataset = await SeedDatasetAsync("歌回语料");
        var baseTime = new DateTime(2026, 10, 7, 0, 0, 0, DateTimeKind.Utc);
        // 故意乱序落库，验证导出按 CreatedAt 升序。
        await SeedItemAsync(dataset.Id, "b/live/700001/recordings/c.flv", "第三句", "k3",
            createdAt: baseTime.AddMinutes(2));
        await SeedItemAsync(dataset.Id, "b/live/700001/recordings/a.flv", "第一句", "k1",
            createdAt: baseTime);
        await SeedItemAsync(dataset.Id, "b/live/700001/recordings/b.flv", "第二句", "k2",
            createdAt: baseTime.AddMinutes(1));

        var audio = new ExportStubAudioClient();
        audio.Clips["b/live/700001/recordings/a.flv"] = new AudioClip([1, 1, 1], "audio/mpeg");
        audio.Clips["b/live/700001/recordings/b.flv"] = new AudioClip([2, 2], "audio/mpeg");
        audio.Clips["b/live/700001/recordings/c.flv"] = new AudioClip([3], "audio/mpeg");

        var controller = CreateController(audio);
        var result = await controller.Export(dataset.Id.ToString());

        Assert.IsType<EmptyResult>(result);
        Assert.Equal(StatusCodes.Status200OK, controller.Response.StatusCode);
        Assert.Equal("application/zip", controller.Response.ContentType);

        var entries = ReadZip(controller.Response.Body);

        Assert.Equal(
            new[] { "000001.mp3", "000001.txt", "000002.mp3", "000002.txt", "000003.mp3", "000003.txt" },
            entries.Select(e => e.Name).ToArray());

        Assert.Equal(new byte[] { 1, 1, 1 }, entries[0].Bytes);
        Assert.Equal(new byte[] { 2, 2 }, entries[2].Bytes);
        Assert.Equal(new byte[] { 3 }, entries[4].Bytes);

        Assert.Equal("第一句", Encoding.UTF8.GetString(entries[1].Bytes));
        Assert.Equal("第二句", Encoding.UTF8.GetString(entries[3].Bytes));
        Assert.Equal("第三句", Encoding.UTF8.GetString(entries[5].Bytes));

        // txt UTF-8 无 BOM
        foreach (var txtIndex in new[] { 1, 3, 5 })
        {
            var bytes = entries[txtIndex].Bytes;
            Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF);
        }

        // 顺序与条目列表（CreatedAt 升序）一致
        Assert.Equal(
            new[] { "b/live/700001/recordings/a.flv", "b/live/700001/recordings/b.flv", "b/live/700001/recordings/c.flv" },
            audio.Requests.Select(r => r.Uri).ToArray());
    }

    // ---- 失败跳过：编号连续、不占号 ----

    [Fact]
    public async Task Export_SkipsFailedAndWavItems_KeepingNumberingContinuous()
    {
        var dataset = await SeedDatasetAsync("含失败条目");
        var baseTime = new DateTime(2026, 10, 7, 0, 0, 0, DateTimeKind.Utc);
        await SeedItemAsync(dataset.Id, "b/live/700001/recordings/ok1.flv", "成功一", "k1", createdAt: baseTime);
        await SeedItemAsync(dataset.Id, "b/live/700001/recordings/throw.flv", "抛错", "k2", createdAt: baseTime.AddMinutes(1));
        await SeedItemAsync(dataset.Id, "b/live/700001/recordings/wav.flv", "降级wav", "k3", createdAt: baseTime.AddMinutes(2));
        await SeedItemAsync(dataset.Id, "b/live/700001/recordings/ok2.flv", "成功二", "k4", createdAt: baseTime.AddMinutes(3));

        var audio = new ExportStubAudioClient();
        audio.Clips["b/live/700001/recordings/ok1.flv"] = new AudioClip([11], "audio/mpeg");
        audio.Clips["b/live/700001/recordings/ok2.flv"] = new AudioClip([22], "audio/mpeg");
        audio.FailingUris.Add("b/live/700001/recordings/throw.flv");
        audio.Clips["b/live/700001/recordings/wav.flv"] = new AudioClip([33], "audio/wav");

        var entries = ReadZip(await ExportAsync(dataset.Id, audio));

        Assert.Equal(
            new[] { "000001.mp3", "000001.txt", "000002.mp3", "000002.txt" },
            entries.Select(e => e.Name).ToArray());
        Assert.Equal(new byte[] { 11 }, entries[0].Bytes);
        Assert.Equal("成功一", Encoding.UTF8.GetString(entries[1].Bytes));
        Assert.Equal(new byte[] { 22 }, entries[2].Bytes);
        Assert.Equal("成功二", Encoding.UTF8.GetString(entries[3].Bytes));
    }

    [Fact]
    public async Task Export_AllItemsFail_Returns200WithEmptyZip()
    {
        var dataset = await SeedDatasetAsync("全失败");
        await SeedItemAsync(dataset.Id, "b/live/700001/recordings/a.flv", "一", "k1");
        await SeedItemAsync(dataset.Id, "b/live/700001/recordings/b.flv", "二", "k2");

        var audio = new ExportStubAudioClient();
        audio.FailingUris.Add("b/live/700001/recordings/a.flv");
        audio.FailingUris.Add("b/live/700001/recordings/b.flv");

        var controller = CreateController(audio);
        var result = await controller.Export(dataset.Id.ToString());

        Assert.IsType<EmptyResult>(result);
        Assert.Equal(StatusCodes.Status200OK, controller.Response.StatusCode);
        Assert.Empty(ReadZip(controller.Response.Body));
    }

    // ---- 边界 ----

    [Fact]
    public async Task Export_UnknownOrInvalidDataset_Returns404WithoutBody()
    {
        var audio = new ExportStubAudioClient();
        var controller = CreateController(audio);

        var unknown = await controller.Export(ObjectId.GenerateNewId().ToString());
        var unknownError = Assert.IsType<NotFoundObjectResult>(unknown);
        Assert.Equal("dataset not found", Assert.IsType<ErrorResponse>(unknownError.Value).Error);

        var invalid = await controller.Export("not-an-objectid");
        Assert.IsType<NotFoundObjectResult>(invalid);

        // 404 分支不写 zip：条目不会被读取
        Assert.Empty(audio.Requests);
        Assert.Equal(0, controller.Response.Body.Length);
    }

    [Fact]
    public async Task Export_ChineseAndSpecialDatasetName_EncodesContentDisposition()
    {
        var dataset = await SeedDatasetAsync("歌回语料 2026/秋");
        await SeedItemAsync(dataset.Id, "b/live/700001/recordings/a.flv", "句子", "k1");

        var audio = new ExportStubAudioClient();
        audio.Clips["b/live/700001/recordings/a.flv"] = new AudioClip([1], "audio/mpeg");

        var controller = CreateController(audio);
        await controller.Export(dataset.Id.ToString());

        var expected = Uri.EscapeDataString("歌回语料 2026/秋");
        var header = controller.Response.Headers.ContentDisposition.ToString();
        Assert.Equal($"attachment; filename*=UTF-8''{expected}.zip", header);
        Assert.Equal("no-store", controller.Response.Headers.CacheControl.ToString());
    }

    [Fact]
    public async Task Export_ItemWithNullEndMs_PassesNullToAudioClient()
    {
        var dataset = await SeedDatasetAsync("空结束时间");
        await SeedItemAsync(dataset.Id, "b/live/700001/recordings/a.flv", "无结束", "k1",
            startMs: 1000, endMs: null);

        var audio = new ExportStubAudioClient();
        audio.Clips["b/live/700001/recordings/a.flv"] = new AudioClip([7], "audio/mpeg");

        await ExportAsync(dataset.Id, audio);

        var request = Assert.Single(audio.Requests);
        Assert.Equal(("b/live/700001/recordings/a.flv", 1000L, (long?)null), request);
    }

    [Fact]
    public async Task Export_ItemWithNullText_DoesNotBreakExportAndKeepsPairing()
    {
        var dataset = await SeedDatasetAsync("空文本脏数据");
        var baseTime = new DateTime(2026, 10, 7, 0, 0, 0, DateTimeKind.Utc);
        // 历史/上游脏数据可能落库 text=null：应写空 txt 并继续导出后续条目，不中断、不跳号。
        await SeedItemAsync(dataset.Id, "b/live/700001/recordings/a.flv", null!, "k1", createdAt: baseTime);
        await SeedItemAsync(dataset.Id, "b/live/700001/recordings/b.flv", "第二句", "k2",
            createdAt: baseTime.AddMinutes(1));

        var audio = new ExportStubAudioClient();
        audio.Clips["b/live/700001/recordings/a.flv"] = new AudioClip([1], "audio/mpeg");
        audio.Clips["b/live/700001/recordings/b.flv"] = new AudioClip([2], "audio/mpeg");

        var entries = ReadZip(await ExportAsync(dataset.Id, audio));

        Assert.Equal(
            new[] { "000001.mp3", "000001.txt", "000002.mp3", "000002.txt" },
            entries.Select(e => e.Name).ToArray());
        Assert.Empty(entries[1].Bytes);
        Assert.Equal("第二句", Encoding.UTF8.GetString(entries[3].Bytes));
    }

    private async Task<Stream> ExportAsync(ObjectId datasetId, ExportStubAudioClient audio)
    {
        var controller = CreateController(audio);
        await controller.Export(datasetId.ToString());
        return controller.Response.Body;
    }

    /// <summary>
    /// 导出测试用音频取数替身：按 URI 映射返回片段，<see cref="FailingUris"/> 抛不可用；
    /// 记录每次调用的入参（uri/startMs/endMs）以便断言顺序与空 endMs 透传。
    /// </summary>
    private sealed class ExportStubAudioClient : IRecordingAudioClient
    {
        public Dictionary<string, AudioClip> Clips { get; } = [];

        public HashSet<string> FailingUris { get; } = [];

        public List<(string Uri, long StartMs, long? EndMs)> Requests { get; } = [];

        public Task<AudioClip> GetClipAsync(
            string audioUri, long startMs, long? endMs, CancellationToken ct = default)
        {
            Requests.Add((audioUri, startMs, endMs));
            if (FailingUris.Contains(audioUri) || !Clips.TryGetValue(audioUri, out var clip))
            {
                return Task.FromException<AudioClip>(new AudioUnavailableException("audio unavailable"));
            }

            return Task.FromResult(clip);
        }

        public Task<AudioClip> MergeClipsAsync(
            IReadOnlyList<AudioClipRequest> items, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }
}
