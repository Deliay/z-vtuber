using System.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using MongoDB.Driver;
using Testcontainers.MongoDb;
using Z.Vtuber.Common.Persistence;

namespace Z.Vtuber.Tests;

/// <summary>
/// Mongo 集成测试夹具：静态共享容器 + 每类独立数据库。
/// <para>
/// 性能契约（2026-09-30 测试性能优化）：
/// 1. <see cref="InitializeAsync"/> 建库并**一次性**初始化 7 域索引；
/// 2. <see cref="ResetAsync()"/> 只清数据（<c>DeleteMany({})</c>），不再 drop 集合 / 重建索引；
/// 3. <see cref="ResetAsync(string[])"/> 支持按集合裁剪清理面；
/// 4. 确需重建索引的用例显式调用 <see cref="EnsureIndexesAsync"/> 或自行 drop。
/// </para>
/// 既有 68 处 <c>InitializeAsync() =&gt; fixture.ResetAsync()</c> 调用点语义兼容（签名不变，隔离不弱化）。
/// </summary>
public sealed class MongoDbFixture : IAsyncLifetime
{
    private static readonly MongoDbContainer SharedContainer = new MongoDbBuilder("mongo:7.0")
        .WithEntrypoint("sh", "-c", "ulimit -n 65535 && exec docker-entrypoint.sh mongod")
        .Build();

    private static readonly SemaphoreSlim StartGate = new(1, 1);
    private static bool _containerStarted;

    private static int _counter;
    private readonly string _databaseName = $"test_{Interlocked.Increment(ref _counter)}_{Guid.NewGuid():N}";

    public IMongoDatabase Database { get; private set; } = null!;

    /// <summary>fixture 清理覆盖的全部集合（7 域并集），只读视图。</summary>
    public static IReadOnlyList<string> AllCollections => AllCollectionsCore;

    private static readonly string[] AllCollectionsCore =
    [
        CookieCollectionNames.Cookies,
        CookieCollectionNames.CookieSessions,
        LiveCollectionNames.Subscriptions,
        LiveCollectionNames.AccountBasicInfo,
        LiveCollectionNames.LiveStatus,
        LiveCollectionNames.LiveEvent,
        LiveCollectionNames.SpaceTimeline,
        LiveCollectionNames.LiveSession,
        CoreCollectionNames.Meta,
        LiveCollectionNames.LiveEventTextIndex,
        CookieCollectionNames.CookieAccountInfo,
        LiveCollectionNames.LiveGuards,
        WeiboCollectionNames.WeiboSubscriptions,
        WeiboCollectionNames.WeiboUserTimeline,
        CookieCollectionNames.WeiboCookies,
        CookieCollectionNames.WeiboCookieSessions,
        NotificationCollectionNames.NotificationSubscriptions,
        RecordingCollectionNames.RecordingStatus,
        RecordingCollectionNames.Recordings,
        TranscriptCollectionNames.TranscriptTasks,
        TranscriptCollectionNames.TranscriptSegments,
        TranscriptCollectionNames.TranscriptModels,
        TranscriptCollectionNames.TranscriptAutoEnqueue,
        TrainingCollectionNames.TrainingDatasets,
        TrainingCollectionNames.TrainingDatasetItems,
        TrainingCollectionNames.TrainingProviders,
        TrainingCollectionNames.TrainingModels,
        TrainingCollectionNames.TrainingEmotionTasks,
        TrainingCollectionNames.TrainingDatasetItemSegments,
    ];

    public async Task InitializeAsync()
    {
        await EnsureContainerStartedAsync();
        var client = new MongoClient(SharedContainer.GetConnectionString());
        Database = client.GetDatabase(_databaseName);

        await EnsureIndexesAsync();
    }

    /// <summary>一次性初始化 7 域索引（幂等）。<see cref="ResetAsync()"/> 不再调用它。</summary>
    public async Task EnsureIndexesAsync()
    {
        var live = new LiveMongoSetup(Database, NullLogger<LiveMongoSetup>.Instance);
        var cookie = new CookieMongoSetup(Database, NullLogger<CookieMongoSetup>.Instance);
        var weibo = new WeiboMongoSetup(Database, NullLogger<WeiboMongoSetup>.Instance);
        var recording = new RecordingMongoSetup(Database, NullLogger<RecordingMongoSetup>.Instance);
        var transcript = new TranscriptMongoSetup(Database, NullLogger<TranscriptMongoSetup>.Instance);
        var training = new TrainingMongoSetup(Database, NullLogger<TrainingMongoSetup>.Instance);
        var notification = new NotificationMongoSetup(Database, NullLogger<NotificationMongoSetup>.Instance);

        await live.EnsureIndexesAsync();
        await cookie.EnsureIndexesAsync();
        await weibo.EnsureIndexesAsync();
        await recording.EnsureIndexesAsync();
        await transcript.EnsureIndexesAsync();
        await training.EnsureIndexesAsync();
        await notification.EnsureIndexesAsync();
    }

    /// <summary>清空全部集合的数据（保留集合与索引）。</summary>
    public Task ResetAsync() => ResetAsync(AllCollectionsCore);

    /// <summary>按集合裁剪清理面：仅清空指定集合的数据。</summary>
    public async Task ResetAsync(params string[] collections)
    {
        var targets = collections.Length == 0 ? AllCollectionsCore : collections;

        // 并发下发删除，避免 28 次串行往返的固定开销。
        await Task.WhenAll(targets.Select(ClearAsync));
    }

    private Task ClearAsync(string name)
    {
        var collection = Database.GetCollection<BsonDocument>(name);
        return collection.DeleteManyAsync(FilterDefinition<BsonDocument>.Empty);
    }

    private static async Task EnsureContainerStartedAsync()
    {
        if (_containerStarted)
        {
            return;
        }

        await StartGate.WaitAsync();
        try
        {
            if (_containerStarted)
            {
                return;
            }

            await SharedContainer.StartAsync();
            _containerStarted = true;
        }
        finally
        {
            StartGate.Release();
        }
    }

    public Task DisposeAsync() => Task.CompletedTask;
}
