using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging.Abstractions;
using Mikibot.Crawler.WebsocketCrawler.Data;
using Z.Vtuber.Crawler.Crawlers;
using Z.Vtuber.Crawler.Live;
using Z.Vtuber.Crawler.Services;
using Z.Vtuber.Common.Live;
using Z.Vtuber.Common.Persistence;
using Z.Vtuber.Common.Persistence.Entities;
using Z.Vtuber.Common.Services;
using Z.Vtuber.Common.Locking;

namespace Z.Vtuber.Tests;

public class LiveEventSourceLockTimingTests
{
    private sealed class FakeDanmakuClient : IDanmakuClient
    {
        private readonly TaskCompletionSource _connectTcs = new();
        private readonly TaskCompletionSource _eventLoopReady = new();
        private readonly TaskCompletionSource _testDone = new();

        public bool ConnectShouldFail { get; set; }
        public Exception? ConnectException { get; set; }

        public bool Connected { get; private set; }
        public bool EventsEnumerated { get; private set; }

        public Task WaitForConnectAsync() => _connectTcs.Task;
        public Task WaitForEventLoopReady() => _eventLoopReady.Task;
        public void SignalTestDone() => _testDone.SetResult();

        public async ValueTask<bool> ConnectAsync(long roomId, CancellationToken ct = default)
        {
            if (ConnectShouldFail)
            {
                _connectTcs.TrySetException(ConnectException ?? new InvalidOperationException("connect failed"));
                throw ConnectException ?? new InvalidOperationException("connect failed");
            }

            Connected = true;
            _connectTcs.TrySetResult();
            return true;
        }

        public async IAsyncEnumerable<IData> Events([EnumeratorCancellation] CancellationToken ct = default)
        {
            EventsEnumerated = true;
            _eventLoopReady.TrySetResult();

            try
            {
                await _testDone.Task.WaitAsync(ct);
            }
            catch (OperationCanceledException)
            {
            }

            yield break;
        }

        public void Dispose() { }
    }

    private sealed class FakeDanmakuClientFactory : IDanmakuClientFactory
    {
        private readonly CookieCallGate _gate;
        private readonly ConcurrentDictionary<string, FakeDanmakuClient> _clients = new();
        private readonly TaskCompletionSource<FakeDanmakuClient> _clientCreated = new();

        public FakeDanmakuClientFactory(CookieCallGate gate) => _gate = gate;

        public Task<IDanmakuClient> CreateAsync(string cookie, CancellationToken ct = default)
        {
            var client = new FakeDanmakuClient();
            _clients[cookie] = client;
            return Task.FromResult<IDanmakuClient>(client);
        }

        public async Task<(IDanmakuClient Client, IDistributedLockHandle Release)> CreateWithLockAsync(string cookie, CancellationToken ct = default)
        {
            var releaser = await _gate.AcquireAsync(cookie, ct);
            var client = new FakeDanmakuClient();
            _clients[cookie] = client;
            _clientCreated.TrySetResult(client);
            return (client, releaser);
        }

        public Task<FakeDanmakuClient> WaitForClientAsync() => _clientCreated.Task;
    }

    private sealed class FakeSubscriptionService : ISubscriptionService
    {
        public event Action<long, SubscriptionType>? Subscribed;
        public Task<IReadOnlyList<long>> GetMidsAsync(SubscriptionType type, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<long>>([12345]);
        public Task<IReadOnlyList<SubscriptionRecord>> GetAllAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<SubscriptionRecord>>([]);
        public Task SubscribeAsync(long mid, SubscriptionType type, CancellationToken ct = default)
            => Task.CompletedTask;
        public Task<bool> UnsubscribeAsync(long mid, SubscriptionType type, CancellationToken ct = default)
            => Task.FromResult(true);
    }

    private sealed class FakeCookiePool : ICookiePoolService
    {
        public Task<CookieRecord> AddAsync(string cookie, string? accountId = null, CancellationToken ct = default)
            => Task.FromResult(new CookieRecord { Cookie = cookie, AccountId = accountId, Status = CookieStatus.Active });
        public Task<CookieRecord> NextAsync(CancellationToken ct = default)
            => Task.FromResult(new CookieRecord { Cookie = "test-cookie", Status = CookieStatus.Active });
        public Task<bool> MarkOfflineAsync(string cookie, CancellationToken ct = default) => Task.FromResult(true);
        public Task MarkCooldownAsync(string cookie, TimeSpan duration, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<CookieRecord>> GetActiveAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<CookieRecord>>([]);
        public Task<CookieRecord?> FindByCookieAsync(string cookie, CancellationToken ct = default)
            => Task.FromResult<CookieRecord?>(new CookieRecord { Cookie = cookie, Status = CookieStatus.Active });
        public Task<CookieRecord?> FindByCookieIdAsync(string cookieId, CancellationToken ct = default)
            => Task.FromResult<CookieRecord?>(new CookieRecord { Cookie = "test-cookie", Status = CookieStatus.Active });
        public Task UpdateAsync(string cookieId, MongoDB.Driver.UpdateDefinition<CookieRecord> update, CancellationToken ct = default)
            => Task.CompletedTask;
    }

    private sealed class FakeLiveEventPublisher : ILiveEventPublisher
    {
        public int PublishCount;
        public Task PublishAsync(LiveEvent liveEvent, CancellationToken ct = default)
        {
            Interlocked.Increment(ref PublishCount);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeLiveSessionTracker : ILiveSessionTracker
    {
        public string? GetCurrentSessionId(long roomId) => null;
        public void SetCurrentSessionId(long roomId, string? sessionId) { }
    }

    private LiveEventSource CreateSource(IDanmakuClientFactory factory)
    {
        return new LiveEventSource(
            new FakeSubscriptionService(),
            null!,
            new FakeCookieServiceClient(),
            factory,
            null!,
            null!,
            new FakeLiveEventPublisher(),
            new FakeLiveSessionTracker(),
            NullLogger<LiveEventSource>.Instance);
    }

    [Fact]
    public async Task RunRoomAsync_LockReleasedDuringEventLoop_OtherAcquireCompletesQuickly()
    {
        var gate = new CookieCallGate(new FakeDistributedLockProvider());
        var factory = new FakeDanmakuClientFactory(gate);
        var source = CreateSource(factory);

        // Pre-hold the lock so RunRoomAsync blocks on CreateWithLockAsync
        var lockHolder = await gate.AcquireAsync("test-cookie");

        var cts = new CancellationTokenSource();
        var runTask = Task.Run(() => source.StartRoomAsync(12345, cts.Token));

        // Give RunRoomAsync time to start and block on lock acquisition
        await Task.Delay(100);

        // Release the lock — RunRoomAsync proceeds through CreateWithLockAsync → ConnectAsync → lockRelease.Dispose()
        await lockHolder.DisposeAsync();

        // Wait for the factory to create the client (CreateWithLockAsync completed after lock acquired)
        var client = await factory.WaitForClientAsync();
        await client.WaitForConnectAsync();

        // Wait for event loop to enter Events()
        await client.WaitForEventLoopReady();

        // The lock MUST be released now — another AcquireAsync should complete quickly
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await using var secondLock = await gate.AcquireAsync("test-cookie");
        sw.Stop();

        Assert.True(sw.ElapsedMilliseconds < 500,
            $"Second AcquireAsync took {sw.ElapsedMilliseconds}ms — lock was NOT released after ConnectAsync");

        // Clean up
        client.SignalTestDone();
        cts.Cancel();
        await runTask;
    }

    [Fact]
    public async Task RunRoomAsync_ConnectAsyncFails_LockReleased()
    {
        var gate = new CookieCallGate(new FakeDistributedLockProvider());
        var factory = new FakeDanmakuClientFactory(gate);

        // Override factory to return a client that fails ConnectAsync
        var factoryWithFail = new FailConnectFactory(gate);
        var source = CreateSource(factoryWithFail);

        var lockHolder = await gate.AcquireAsync("test-cookie");

        var cts = new CancellationTokenSource();
        var runTask = Task.Run(() => source.StartRoomAsync(12345, cts.Token));

        await Task.Delay(100);

        // Release lock — RunRoomAsync proceeds, CreateWithLockAsync succeeds, ConnectAsync fails
        await lockHolder.DisposeAsync();

        // Wait briefly for the catch block to execute and dispose lockRelease
        await Task.Delay(200);

        // The lock MUST be available during the reconnect delay
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await using var secondLock = await gate.AcquireAsync("test-cookie");
        sw.Stop();

        Assert.True(sw.ElapsedMilliseconds < 500,
            $"Second AcquireAsync took {sw.ElapsedMilliseconds}ms — lock was NOT released after ConnectAsync failure");

        cts.Cancel();
        await runTask;
    }

    private sealed class FailConnectFactory : IDanmakuClientFactory
    {
        private readonly CookieCallGate _gate;
        private readonly TaskCompletionSource _connectFailed = new();

        public FailConnectFactory(CookieCallGate gate) => _gate = gate;

        public Task WaitForConnectFailed() => _connectFailed.Task;

        public Task<IDanmakuClient> CreateAsync(string cookie, CancellationToken ct = default)
            => Task.FromResult<IDanmakuClient>(new FakeDanmakuClient { ConnectShouldFail = true });

        public async Task<(IDanmakuClient Client, IDistributedLockHandle Release)> CreateWithLockAsync(string cookie, CancellationToken ct = default)
        {
            var releaser = await _gate.AcquireAsync(cookie, ct);
            var client = new FakeDanmakuClient { ConnectShouldFail = true };
            // Signal when ConnectAsync is called (it will throw)
            _ = Task.Run(async () =>
            {
                try { await client.WaitForConnectAsync(); }
                catch { _connectFailed.TrySetResult(); }
            });
            return (client, releaser);
        }
    }
}

public static class LiveEventSourceTestExtensions
{
    public static Task StartRoomAsync(this LiveEventSource source, long roomId, CancellationToken ct)
    {
        var method = typeof(LiveEventSource).GetMethod("RunRoomAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        if (method is null)
            throw new InvalidOperationException("RunRoomAsync method not found");

        return (Task)method.Invoke(source, [roomId, ct])!;
    }
}
