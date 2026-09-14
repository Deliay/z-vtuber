using Z.Vtuber.Recording.Audio;

namespace Z.Vtuber.Tests;

public class AudioStreamHubTests
{
    private static byte[] Chunk(byte value, int size)
    {
        var data = new byte[size];
        Array.Fill(data, value);
        return data;
    }

    [Fact]
    public void Publish_WithoutSubscribers_DoesNotBuffer()
    {
        var hub = new AudioStreamHub(1024);
        hub.MarkStreaming(1);

        hub.Publish(1, Chunk(1, 128));
        hub.Publish(1, Chunk(2, 128));

        var subscriber = hub.Subscribe(1);
        // 先前的广播不应被缓存
        Assert.False(subscriber.TryRead(out _));
    }

    [Fact]
    public void Publish_BeforeMarkStreaming_IsDropped()
    {
        var hub = new AudioStreamHub(1024);
        var subscriber = hub.Subscribe(1);

        hub.Publish(1, Chunk(1, 32));

        Assert.False(subscriber.TryRead(out _));
    }

    [Fact]
    public async Task Subscriber_ReceivesChunksInOrder()
    {
        var hub = new AudioStreamHub(1024);
        hub.MarkStreaming(1);
        using var subscriber = hub.Subscribe(1);

        hub.Publish(1, Chunk(1, 16));
        hub.Publish(1, Chunk(2, 16));

        Assert.True(await subscriber.WaitToReadAsync(CancellationToken.None));
        Assert.True(subscriber.TryRead(out var first));
        Assert.True(subscriber.TryRead(out var second));
        Assert.Equal(1, first[0]);
        Assert.Equal(2, second[0]);
    }

    [Fact]
    public void SlowConsumer_DropsOldest_AndRespectsBufferBound()
    {
        var hub = new AudioStreamHub(10);
        hub.MarkStreaming(1);
        using var subscriber = hub.Subscribe(1);

        for (var i = 0; i < 10; i++)
        {
            hub.Publish(1, Chunk((byte)i, 4));
        }

        Assert.True(subscriber.QueuedBytes <= 10);

        // 最旧的数据已被丢弃，保留的是最新的若干块
        Assert.True(subscriber.TryRead(out var first));
        Assert.True(subscriber.TryRead(out var second));
        Assert.True(second[0] > first[0]);
    }

    [Fact]
    public void SlowConsumer_DoesNotBlockOtherSubscribers()
    {
        var hub = new AudioStreamHub(8);
        hub.MarkStreaming(1);
        using var slow = hub.Subscribe(1);
        using var fast = hub.Subscribe(1);

        for (var i = 0; i < 5; i++)
        {
            hub.Publish(1, Chunk((byte)(i + 1), 4));
        }

        Assert.True(slow.QueuedBytes <= 8);
        Assert.True(fast.QueuedBytes <= 8);

        // 两者都能读到最新数据，广播未被慢消费者阻塞
        Assert.True(fast.TryRead(out _));
    }

    [Fact]
    public async Task MarkStopped_CompletesSubscribers()
    {
        var hub = new AudioStreamHub(1024);
        hub.MarkStreaming(1);
        using var subscriber = hub.Subscribe(1);

        hub.MarkStopped(1);

        Assert.False(await subscriber.WaitToReadAsync(CancellationToken.None));
        Assert.False(hub.IsStreaming(1));
    }

    [Fact]
    public void Unsubscribe_StopsDelivery()
    {
        var hub = new AudioStreamHub(1024);
        hub.MarkStreaming(1);
        var subscriber = hub.Subscribe(1);

        hub.Unsubscribe(1, subscriber);
        hub.Publish(1, Chunk(1, 16));

        Assert.False(subscriber.TryRead(out _));
    }

    [Fact]
    public void SingleChunkLargerThanBuffer_IsDropped()
    {
        var hub = new AudioStreamHub(8);
        hub.MarkStreaming(1);
        using var subscriber = hub.Subscribe(1);

        hub.Publish(1, Chunk(1, 64));

        Assert.Equal(0, subscriber.QueuedBytes);
    }
}
