using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using StackExchange.Redis;
using Z.Vtuber.Common.Live;
using Z.Vtuber.Recording.Recording;

namespace Z.Vtuber.Tests;

[Collection("redis")]
public class RecordingEventPublisherTests(RedisFixture fixture)
{
    [Fact]
    public async Task PublishStarted_SerializesPayloadToStartedChannel()
    {
        var startedAt = new DateTime(2026, 9, 16, 12, 0, 0, DateTimeKind.Utc);
        var evt = new RecordingStartedEvent(
            "65f000000000000000000001", 123L, 456L, "晚间歌回", "http://recording/api/123/stream/audio", startedAt);

        var json = await PublishAndCaptureAsync(RecordingEventChannels.Started, publisher => publisher.PublishStartedAsync(evt));

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.Equal(evt.RecordingId, root.GetProperty("recordingId").GetString());
        Assert.Equal(123L, root.GetProperty("roomId").GetInt64());
        Assert.Equal(456L, root.GetProperty("mid").GetInt64());
        Assert.Equal("晚间歌回", root.GetProperty("title").GetString());
        Assert.Equal("http://recording/api/123/stream/audio", root.GetProperty("liveAudioUrl").GetString());
    }

    [Fact]
    public async Task PublishFinished_SerializesPayloadToFinishedChannel()
    {
        var endedAt = new DateTime(2026, 9, 16, 14, 0, 0, DateTimeKind.Utc);
        var evt = new RecordingFinishedEvent(
            "65f000000000000000000002",
            123L,
            456L,
            "晚间歌回",
            ["[RAW]a.flv", "[AV1]a.flv"],
            ["http://recording/api/123/recordings/a.flv"],
            endedAt);

        var json = await PublishAndCaptureAsync(RecordingEventChannels.Finished, publisher => publisher.PublishFinishedAsync(evt));

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.Equal(evt.RecordingId, root.GetProperty("recordingId").GetString());
        Assert.Equal(123L, root.GetProperty("roomId").GetInt64());
        Assert.Equal(2, root.GetProperty("files").GetArrayLength());
        Assert.Equal(1, root.GetProperty("fileUrls").GetArrayLength());
    }

    [Fact]
    public async Task PublishFailure_IsLoggedNotThrown()
    {
        var logger = new ListLogger<RedisRecordingEventPublisher>();
        // redis 为 null 触发内部异常，验证被捕获并记 warning
        var publisher = new RedisRecordingEventPublisher(null!, logger);

        await publisher.PublishStartedAsync(new RecordingStartedEvent(
            "id", 1, 2, "t", "http://x/api/1/stream/audio", DateTime.UtcNow));
        await publisher.PublishFinishedAsync(new RecordingFinishedEvent(
            "id", 1, 2, "t", ["a.flv"], ["http://x/a.flv"], DateTime.UtcNow));

        Assert.Equal(2, logger.Entries.Count);
        Assert.True(logger.HasWarning);
    }

    private async Task<string> PublishAndCaptureAsync(
        string channel,
        Func<RedisRecordingEventPublisher, Task> publish)
    {
        var subscriber = fixture.Connection.GetSubscriber();
        var redisChannel = new RedisChannel(channel, RedisChannel.PatternMode.Literal);
        var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        await subscriber.SubscribeAsync(redisChannel, (_, value) => received.TrySetResult(value!));
        try
        {
            var publisher = new RedisRecordingEventPublisher(
                fixture.Connection, NullLogger<RedisRecordingEventPublisher>.Instance);
            await publish(publisher);

            return await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            await subscriber.UnsubscribeAsync(redisChannel);
        }
    }
}
