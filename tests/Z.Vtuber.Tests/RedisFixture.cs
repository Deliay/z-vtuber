using StackExchange.Redis;
using Testcontainers.Redis;

namespace Z.Vtuber.Tests;

public sealed class RedisFixture : IAsyncLifetime
{
    private readonly RedisContainer _container = new RedisBuilder().Build();

    public IConnectionMultiplexer Connection { get; private set; } = null!;

    public string ConnectionString { get; private set; } = string.Empty;

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        ConnectionString = _container.GetConnectionString();
        Connection = await ConnectionMultiplexer.ConnectAsync(ConnectionString);
    }

    public Task DisposeAsync()
    {
        Connection?.Dispose();
        return _container.DisposeAsync().AsTask();
    }
}

[CollectionDefinition("redis")]
public sealed class RedisCollection : ICollectionFixture<RedisFixture>;
