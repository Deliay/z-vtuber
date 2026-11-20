using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Z.Vtuber.Tests.AuthFakes;

/// <summary>
/// 测试用假弹幕事件源：真实 Kestrel，按 roomId 提供 SSE 订阅与历史 range。
/// 支持脚本化 heartbeat/danmaku、主动断开、只写历史（模拟断线期间补投）。
/// </summary>
public sealed class FakeDanmakuSource : IAsyncDisposable
{
    private sealed record HistoryItem(string RoomId, string Type, DateTime OccurredAt, string PayloadJson);

    private readonly WebApplication _app;
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<Guid, Channel<string>>> _subscribers =
        new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, List<HistoryItem>> _history = new(StringComparer.Ordinal);

    public FakeDanmakuSource()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ApplicationName = "FakeDanmakuSource",
            EnvironmentName = "Development"
        });
        builder.Logging.ClearProviders();
        _app = builder.Build();
        _app.MapGet("/api/b/live/{roomId}/event/subscribe", HandleSubscribeAsync);
        _app.MapGet("/api/b/live/{roomId}/event/range", HandleRangeAsync);
    }

    public string BaseUrl { get; private set; } = string.Empty;

    public async Task StartAsync()
    {
        _app.Urls.Add("http://127.0.0.1:0");
        await _app.StartAsync();
        var addresses = _app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()?.Addresses;
        BaseUrl = addresses?.FirstOrDefault()
            ?? throw new InvalidOperationException("Fake danmaku source did not bind an address.");
    }

    public int SubscriberCount(string roomId) =>
        _subscribers.TryGetValue(roomId, out var channels) ? channels.Count : 0;

    /// <summary>向指定房间所有在线订阅写一条 danmaku 帧。</summary>
    public Task PublishDanmakuAsync(
        string roomId,
        string token,
        long uid = 403496,
        string name = "Zero_Ash",
        string? face = "https://i2.hdslb.com/bfs/face/test.jpg",
        DateTime? occurredAt = null)
    {
        var payload = AuthTestData.DanmakuPayload(token, uid, name, face);
        var item = new HistoryItem(
            roomId, "danmaku", occurredAt ?? DateTime.UtcNow, payload);
        AddHistory(item);
        var frame =
            $"event: danmaku\ndata: {ToEventJson(item)}\n\n";
        return WriteToSubscribersAsync(roomId, frame);
    }

    /// <summary>仅写入历史（模拟断线期间用户发送了 token），重连后由 range 补投。</summary>
    public Task PublishHistoryOnlyAsync(
        string roomId,
        string token,
        long uid = 403496,
        string name = "Zero_Ash",
        DateTime? occurredAt = null)
    {
        var payload = AuthTestData.DanmakuPayload(token, uid, name);
        AddHistory(new HistoryItem(roomId, "danmaku", occurredAt ?? DateTime.UtcNow, payload));
        return Task.CompletedTask;
    }

    public async Task DisconnectAsync(string roomId)
    {
        if (!_subscribers.TryGetValue(roomId, out var channels))
        {
            return;
        }

        foreach (var pair in channels)
        {
            pair.Value.Writer.TryComplete();
        }

        await WaitUntilAsync(() => SubscriberCount(roomId) == 0, TimeSpan.FromSeconds(2));
    }

    private void AddHistory(HistoryItem item)
    {
        var list = _history.GetOrAdd(item.RoomId, _ => []);
        lock (list)
        {
            list.Add(item);
        }
    }

    private Task WriteToSubscribersAsync(string roomId, string frame)
    {
        if (!_subscribers.TryGetValue(roomId, out var channels))
        {
            return Task.CompletedTask;
        }

        foreach (var channel in channels.Values)
        {
            channel.Writer.TryWrite(frame);
        }

        return Task.CompletedTask;
    }

    private async Task HandleSubscribeAsync(HttpContext context, string roomId)
    {
        context.Response.StatusCode = (int)HttpStatusCode.OK;
        context.Response.ContentType = "text/event-stream";
        context.Response.Headers.CacheControl = "no-cache";

        var id = Guid.NewGuid();
        var channel = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });
        var channels = _subscribers.GetOrAdd(roomId, _ => new ConcurrentDictionary<Guid, Channel<string>>());
        channels[id] = channel;

        try
        {
            await context.Response.WriteAsync("event: heartbeat\ndata: {}\n\n", context.RequestAborted);
            await context.Response.Body.FlushAsync(context.RequestAborted);

            await foreach (var frame in channel.Reader.ReadAllAsync(context.RequestAborted))
            {
                await context.Response.WriteAsync(frame, context.RequestAborted);
                await context.Response.Body.FlushAsync(context.RequestAborted);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            channels.TryRemove(id, out _);
        }
    }

    private IResult HandleRangeAsync(HttpContext context, string roomId)
    {
        var startedAt = ParseDate(context.Request.Query["startedAt"].ToString());
        var endedAt = ParseDate(context.Request.Query["endedAt"].ToString());

        List<HistoryItem> snapshot;
        if (_history.TryGetValue(roomId, out var list))
        {
            lock (list)
            {
                snapshot = list.ToList();
            }
        }
        else
        {
            snapshot = [];
        }

        var items = snapshot
            .Where(x => (startedAt is null || x.OccurredAt >= startedAt) &&
                        (endedAt is null || x.OccurredAt <= endedAt))
            .OrderBy(x => x.OccurredAt)
            .Select(ToEventJson)
            .ToList();

        return Results.Json(new
        {
            total = items.Count,
            page = 1,
            pageSize = 100,
            items = items.Select(x => JsonSerializer.Deserialize<JsonElement>(x)).ToList()
        });
    }

    private static DateTime? ParseDate(string? value) =>
        DateTime.TryParse(value, null, System.Globalization.DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed.ToUniversalTime()
            : null;

    private static string ToEventJson(HistoryItem item) =>
        JsonSerializer.Serialize(new
        {
            roomId = item.RoomId,
            type = item.Type,
            occurredAt = item.OccurredAt.ToString("O"),
            payload = JsonDocument.Parse(item.PayloadJson).RootElement.Clone()
        });

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var channels in _subscribers.Values)
        {
            foreach (var channel in channels.Values)
            {
                channel.Writer.TryComplete();
            }
        }

        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
