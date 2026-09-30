using Microsoft.Extensions.Logging.Abstractions;
using Z.Vtuber.Common.Persistence;
using Z.Vtuber.Common.Persistence.Entities;
using Z.Vtuber.Common.Services;
using Z.Vtuber.Recording.Configuration;
using Z.Vtuber.Recording.Live;
using Z.Vtuber.Recording.Recording;
using Z.Vtuber.Recording.Services;

namespace Z.Vtuber.Tests;

public class LiveStatusPollerTests(MongoDbFixture fixture) : IClassFixture<MongoDbFixture>, IAsyncLifetime
{
    public Task InitializeAsync() => fixture.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private LiveStatusPoller CreatePoller(
        ISubscriptionService subscription,
        FakeBiliLiveApi bili,
        FakeRecordingSessionRunner runner,
        IRecordingCookieClient? cookieClient = null,
        RecordingOptions? options = null) =>
        new(
            subscription,
            new AccountBasicInfoRepository(fixture.Database),
            bili,
            cookieClient ?? new FakeRecordingCookieClient(),
            runner,
            new RecordingStatusReporter(new RecordingStatusRepository(fixture.Database)),
            new RecordingStatusRepository(fixture.Database),
            options ?? new RecordingOptions { PollIntervalSeconds = 1 },
            NullLogger<LiveStatusPoller>.Instance);

    private async Task SeedAccountAsync(long mid, long roomId)
    {
        await new AccountBasicInfoRepository(fixture.Database).UpsertAsync(new AccountBasicInfoRecord
        {
            Mid = mid,
            RoomId = roomId,
            Data = $$"""{"uName":"主播{{mid}}"}""",
            FetchedAt = DateTime.UtcNow,
        });
    }

    [Fact]
    public async Task NotLive_ReportsNotLive_WithoutStartingSession()
    {
        var subscription = new SubscriptionService(fixture.Database);
        await subscription.SubscribeAsync(1, SubscriptionType.Recording);
        await SeedAccountAsync(1, 800001);

        var bili = new FakeBiliLiveApi { RoomInfoProvider = roomId => new LiveRoomInfo(roomId, "title", false) };
        var runner = new FakeRecordingSessionRunner();
        var poller = CreatePoller(subscription, bili, runner);

        await poller.RunOnceAsync(CancellationToken.None);

        var record = await new RecordingStatusRepository(fixture.Database).GetByRoomIdAsync(800001);
        Assert.Equal("not_live", record!.State);
        Assert.Empty(runner.StartedRooms);
    }

    [Fact]
    public async Task Live_StartsRecordingSession()
    {
        var subscription = new SubscriptionService(fixture.Database);
        await subscription.SubscribeAsync(2, SubscriptionType.Recording);
        await SeedAccountAsync(2, 800002);

        var bili = new FakeBiliLiveApi { RoomInfoProvider = roomId => new LiveRoomInfo(roomId, "晚间歌回", true) };
        var runner = new FakeRecordingSessionRunner();
        var poller = CreatePoller(subscription, bili, runner);

        await poller.RunOnceAsync(CancellationToken.None);

        Assert.Contains(800002L, runner.StartedRooms);
        Assert.Contains(2L, runner.StartedMids);
    }

    [Fact]
    public async Task UnresolvableRoomId_IsSkipped()
    {
        var subscription = new SubscriptionService(fixture.Database);
        await subscription.SubscribeAsync(3, SubscriptionType.Recording);

        var runner = new FakeRecordingSessionRunner();
        var poller = CreatePoller(subscription, new FakeBiliLiveApi(), runner);

        await poller.RunOnceAsync(CancellationToken.None);

        Assert.Empty(runner.StartedRooms);
    }

    [Fact]
    public async Task Unsubscribed_ActiveSession_IsCancelled()
    {
        var subscription = new SubscriptionService(fixture.Database);
        await subscription.SubscribeAsync(4, SubscriptionType.Recording);
        await SeedAccountAsync(4, 800004);

        var cancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = new FakeRecordingSessionRunner
        {
            RunFunc = (_, _, _, _, _, _, ct) =>
            {
                ct.Register(() => cancelled.TrySetResult(true));
                return Task.Delay(Timeout.Infinite, ct);
            },
        };
        var poller = CreatePoller(subscription, new FakeBiliLiveApi(), runner);

        await poller.RunOnceAsync(CancellationToken.None);
        Assert.Contains(800004L, runner.StartedRooms);

        await subscription.UnsubscribeAsync(4, SubscriptionType.Recording);
        await poller.RunOnceAsync(CancellationToken.None);

        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(cancelled.Task.IsCompleted, "取消订阅后录制运行应被取消");
    }

    [Fact]
    public async Task StaleRecording_WithNoAvailableCookie_IsCorrectedToError()
    {
        var subscription = new SubscriptionService(fixture.Database);
        await subscription.SubscribeAsync(5, SubscriptionType.Recording);
        await SeedAccountAsync(5, 800005);

        var repository = new RecordingStatusRepository(fixture.Database);
        await repository.UpsertAsync(new RecordingStatusRecord
        {
            RoomId = 800005,
            State = "recording",
            HostName = "old-host",
            SessionTitle = "旧场次",
            Files = ["[RAW][800005][主播5] 2026-09-13-旧场次.flv"],
            UpdatedAt = DateTime.UtcNow - TimeSpan.FromMinutes(10),
        });

        var cookieClient = new FakeRecordingCookieClient
        {
            CheckoutFunc = _ => throw new NoAvailableCookieException(),
        };
        var poller = CreatePoller(subscription, new FakeBiliLiveApi(), new FakeRecordingSessionRunner(), cookieClient);

        await poller.RunOnceAsync(CancellationToken.None);

        var status = await repository.GetByRoomIdAsync(800005);
        Assert.Equal("error", status!.State);
        Assert.Contains("cookie", status.Error);
    }

    [Fact]
    public async Task FreshRecording_WithNoAvailableCookie_IsNotCorrected()
    {
        var subscription = new SubscriptionService(fixture.Database);
        await subscription.SubscribeAsync(6, SubscriptionType.Recording);
        await SeedAccountAsync(6, 800006);

        var repository = new RecordingStatusRepository(fixture.Database);
        await repository.UpsertAsync(new RecordingStatusRecord
        {
            RoomId = 800006,
            State = "recording",
            HostName = "active-host",
            UpdatedAt = DateTime.UtcNow,
        });

        var cookieClient = new FakeRecordingCookieClient
        {
            CheckoutFunc = _ => throw new NoAvailableCookieException(),
        };
        var poller = CreatePoller(subscription, new FakeBiliLiveApi(), new FakeRecordingSessionRunner(), cookieClient);

        await poller.RunOnceAsync(CancellationToken.None);

        // 心跳仍在阈值内：可能他实例正在录制，不得覆盖
        Assert.Equal("recording", (await repository.GetByRoomIdAsync(800006))!.State);
    }
}
