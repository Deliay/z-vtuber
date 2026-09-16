using System.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using MongoDB.Driver;
using Testcontainers.MongoDb;
using Z.Vtuber.Common.Persistence;

namespace Z.Vtuber.Tests;

public sealed class MongoDbFixture : IAsyncLifetime
{
    private static readonly MongoDbContainer SharedContainer = new MongoDbBuilder("mongo:7.0")
        .WithEntrypoint("sh", "-c", "ulimit -n 65535 && exec docker-entrypoint.sh mongod --replSet rs0 --keyFile /tmp/mongodb-keyfile --bind_ip_all")
        .WithReplicaSet("rs0")
        .Build();

    private static readonly SemaphoreSlim InitLock = new(1, 1);
    private static string? _sharedConnectionString;

    private static int _counter;
    private readonly string _databaseName = $"test_{Interlocked.Increment(ref _counter)}_{Guid.NewGuid():N}";

    /// <summary>replica set 单节点连接串（含 directConnection=true），供需要 raw client 的测试使用。</summary>
    public string ConnectionString { get; private set; } = null!;

    public IMongoDatabase Database { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await InitLock.WaitAsync();
        try
        {
            if (_sharedConnectionString is null)
            {
                await SharedContainer.StartAsync();
                _sharedConnectionString = BuildReplicaSetConnectionString(SharedContainer.GetConnectionString());
                await EnsureReplicaSetPrimaryAsync(_sharedConnectionString);
            }

            ConnectionString = _sharedConnectionString;
        }
        finally
        {
            InitLock.Release();
        }

        var client = new MongoClient(ConnectionString);
        Database = client.GetDatabase(_databaseName);

        await ResetAsync();
    }

    public async Task ResetAsync()
    {
        var names = new[]
        {
            MongoCollectionNames.Cookies,
            MongoCollectionNames.CookieSessions,
            MongoCollectionNames.Subscriptions,
            MongoCollectionNames.AccountBasicInfo,
            MongoCollectionNames.LiveStatus,
            MongoCollectionNames.LiveEvent,
            MongoCollectionNames.SpaceTimeline,
            MongoCollectionNames.LiveSession,
            MongoCollectionNames.Meta,
            MongoCollectionNames.LiveEventTextIndex,
            MongoCollectionNames.CookieAccountInfo,
            MongoCollectionNames.LiveGuards,
            MongoCollectionNames.WeiboSubscriptions,
            MongoCollectionNames.WeiboUserTimeline,
            MongoCollectionNames.WeiboCookies,
            MongoCollectionNames.WeiboCookieSessions,
            MongoCollectionNames.NotificationSubscriptions,
            MongoCollectionNames.RecordingStatus,
            MongoCollectionNames.Recordings,
            MongoCollectionNames.TranscriptTasks,
            MongoCollectionNames.TranscriptSegments,
            MongoCollectionNames.TranscriptModels,
            MongoCollectionNames.TranscriptAutoEnqueue,
        };

        foreach (var name in names)
        {
            await Database.DropCollectionAsync(name);
        }

        var initializer = new MongoDbInitializer(Database, NullLogger<MongoDbInitializer>.Instance);
        await initializer.EnsureIndexesAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static string BuildReplicaSetConnectionString(string connectionString)
    {
        if (connectionString.Contains("directConnection", StringComparison.OrdinalIgnoreCase))
        {
            return connectionString;
        }

        if (connectionString.Contains('?'))
        {
            return $"{connectionString}&directConnection=true";
        }

        return $"{connectionString.TrimEnd('/')}/?directConnection=true";
    }

    private static async Task EnsureReplicaSetPrimaryAsync(string connectionString)
    {
        var client = new MongoClient(connectionString);
        var admin = client.GetDatabase("admin");

        var deadline = DateTime.UtcNow.AddMinutes(2);
        var initiated = false;
        while (!initiated && DateTime.UtcNow < deadline)
        {
            try
            {
                await admin.RunCommandAsync(
                    new BsonDocumentCommand<BsonDocument>(new BsonDocument("replSetInitiate", new BsonDocument())));
                initiated = true;
            }
            catch (MongoCommandException ex) when (ex.Code == 23 || ex.CodeName == "AlreadyInitialized"
                || ex.Message.Contains("already initialized", StringComparison.OrdinalIgnoreCase))
            {
                initiated = true;
            }
            catch (Exception)
            {
                await Task.Delay(500);
            }
        }

        if (!initiated)
        {
            throw new InvalidOperationException("Failed to initiate MongoDB replica set within timeout.");
        }

        Exception? lastError = null;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                var hello = await admin.RunCommandAsync(
                    new BsonDocumentCommand<BsonDocument>(new BsonDocument("hello", 1)));
                if (hello.TryGetValue("isWritablePrimary", out var primary) && primary.ToBoolean())
                {
                    return;
                }
                if (hello.TryGetValue("ismaster", out var isMaster) && isMaster.ToBoolean())
                {
                    return;
                }
            }
            catch (Exception ex)
            {
                lastError = ex;
            }

            await Task.Delay(500);
        }

        throw new InvalidOperationException(
            $"MongoDB replica set did not become primary within timeout. Last error: {lastError?.Message}");
    }
}
