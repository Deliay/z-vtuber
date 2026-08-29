using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using MongoDB.Driver;
using Z.Vtuber.Controllers;
using Z.Vtuber.Persistence;
using Z.Vtuber.Persistence.Entities;
using Z.Vtuber.Services;

namespace Z.Vtuber.Tests;

[Collection("mongo")]
public class BilibiliBackstageControllerTests(MongoDbFixture fixture) : IAsyncLifetime
{
    public Task InitializeAsync() => fixture.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private static BilibiliBackstageController CreateController(IMongoDatabase database) => new(
        new SubscriptionService(database),
        new CookiePoolService(database, new ProbeServiceProvider()),
        new AccountBasicInfoRepository(database),
        new CookieAccountInfoRepository(database));

    private static readonly JsonSerializerOptions CamelCase = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private static JsonElement Ok(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        return JsonSerializer.SerializeToElement(ok.Value, CamelCase);
    }

    private static SubscriptionRecord Subscription(long mid, SubscriptionType type) => new()
    {
        Mid = mid,
        Type = type,
        SubscribedAt = DateTime.UtcNow,
    };

    [Fact]
    public async Task Subscription_WithProfile_ReturnsStreamerName()
    {
        const long mid = 1150976664;
        var subscriptions = fixture.Database
            .GetCollection<SubscriptionRecord>(MongoCollectionNames.Subscriptions);
        await subscriptions.InsertOneAsync(Subscription(mid, SubscriptionType.Danmaku));

        var accountInfo = fixture.Database
            .GetCollection<AccountBasicInfoRecord>(MongoCollectionNames.AccountBasicInfo);
        await accountInfo.InsertOneAsync(new AccountBasicInfoRecord
        {
            Mid = mid,
            RoomId = 1,
            Data = """{"uName":"某主播"}""",
            FetchedAt = DateTime.UtcNow,
        });

        var json = Ok(await CreateController(fixture.Database).ListSubscriptions(CancellationToken.None));

        var first = json.GetProperty("items")[0];
        Assert.Equal(mid, first.GetProperty("mid").GetInt64());
        Assert.Equal("某主播", first.GetProperty("streamer").GetProperty("name").GetString());
    }

    [Fact]
    public async Task Subscription_SameMidBothTypes_ShareStreamerName()
    {
        const long mid = 1150976664;
        var subscriptions = fixture.Database
            .GetCollection<SubscriptionRecord>(MongoCollectionNames.Subscriptions);
        await subscriptions.InsertOneAsync(Subscription(mid, SubscriptionType.Danmaku));
        await subscriptions.InsertOneAsync(Subscription(mid, SubscriptionType.Dynamic));

        var accountInfo = fixture.Database
            .GetCollection<AccountBasicInfoRecord>(MongoCollectionNames.AccountBasicInfo);
        await accountInfo.InsertOneAsync(new AccountBasicInfoRecord
        {
            Mid = mid,
            RoomId = 1,
            Data = """{"uName":"同昵称"}""",
            FetchedAt = DateTime.UtcNow,
        });

        var json = Ok(await CreateController(fixture.Database).ListSubscriptions(CancellationToken.None));

        Assert.Equal(2, json.GetProperty("items").GetArrayLength());
        Assert.Equal("同昵称", json.GetProperty("items")[0].GetProperty("streamer").GetProperty("name").GetString());
        Assert.Equal("同昵称", json.GetProperty("items")[1].GetProperty("streamer").GetProperty("name").GetString());
    }

    [Fact]
    public async Task Subscription_NoProfile_StreamerIsNull()
    {
        const long mid = 1150976664;
        var subscriptions = fixture.Database
            .GetCollection<SubscriptionRecord>(MongoCollectionNames.Subscriptions);
        await subscriptions.InsertOneAsync(Subscription(mid, SubscriptionType.Danmaku));

        var json = Ok(await CreateController(fixture.Database).ListSubscriptions(CancellationToken.None));

        var streamer = json.GetProperty("items")[0].GetProperty("streamer");
        Assert.Equal(JsonValueKind.Null, streamer.ValueKind);
    }

    [Fact]
    public async Task Subscription_DataMissingUName_NameIsNull()
    {
        const long mid = 1150976664;
        var subscriptions = fixture.Database
            .GetCollection<SubscriptionRecord>(MongoCollectionNames.Subscriptions);
        await subscriptions.InsertOneAsync(Subscription(mid, SubscriptionType.Danmaku));

        var accountInfo = fixture.Database
            .GetCollection<AccountBasicInfoRecord>(MongoCollectionNames.AccountBasicInfo);
        await accountInfo.InsertOneAsync(new AccountBasicInfoRecord
        {
            Mid = mid,
            RoomId = 1,
            Data = """{"title":"无昵称"}""",
            FetchedAt = DateTime.UtcNow,
        });

        var json = Ok(await CreateController(fixture.Database).ListSubscriptions(CancellationToken.None));

        var streamer = json.GetProperty("items")[0].GetProperty("streamer");
        Assert.Equal(JsonValueKind.Object, streamer.ValueKind);
        Assert.Equal(JsonValueKind.Null, streamer.GetProperty("name").ValueKind);
    }

    [Fact]
    public async Task Subscription_InvalidJsonData_DoesNotBreakOtherRows()
    {
        var subscriptions = fixture.Database
            .GetCollection<SubscriptionRecord>(MongoCollectionNames.Subscriptions);
        await subscriptions.InsertOneAsync(Subscription(111, SubscriptionType.Danmaku));
        await subscriptions.InsertOneAsync(Subscription(222, SubscriptionType.Danmaku));

        var accountInfo = fixture.Database
            .GetCollection<AccountBasicInfoRecord>(MongoCollectionNames.AccountBasicInfo);
        await accountInfo.InsertOneAsync(new AccountBasicInfoRecord
        {
            Mid = 111,
            RoomId = 1,
            Data = "not-json",
            FetchedAt = DateTime.UtcNow,
        });
        await accountInfo.InsertOneAsync(new AccountBasicInfoRecord
        {
            Mid = 222,
            RoomId = 2,
            Data = """{"uName":"正常"}""",
            FetchedAt = DateTime.UtcNow,
        });

        var json = Ok(await CreateController(fixture.Database).ListSubscriptions(CancellationToken.None));

        var items = json.GetProperty("items");
        Assert.Equal(2, items.GetArrayLength());
        Assert.Equal(JsonValueKind.Null, items[0].GetProperty("streamer").GetProperty("name").ValueKind);
        Assert.Equal("正常", items[1].GetProperty("streamer").GetProperty("name").GetString());
    }

    [Fact]
    public async Task Cookie_WithAccountInfo_ReturnsAccountNameWithoutCookiePlaintext()
    {
        var cookies = fixture.Database
            .GetCollection<CookieRecord>(MongoCollectionNames.Cookies);
        var record = new CookieRecord
        {
            Cookie = "SECRET-COOKIE",
            Status = CookieStatus.Active,
        };
        await cookies.InsertOneAsync(record);

        var accountInfo = fixture.Database
            .GetCollection<CookieAccountInfoRecord>(MongoCollectionNames.CookieAccountInfo);
        await accountInfo.InsertOneAsync(new CookieAccountInfoRecord
        {
            CookieId = record.Id,
            Mid = 88,
            Uname = "某帐号",
            FetchedAt = DateTime.UtcNow,
        });

        var json = Ok(await CreateController(fixture.Database).ListCookies(CancellationToken.None));

        var first = json.GetProperty("items")[0];
        Assert.Equal(record.Id.ToString(), first.GetProperty("id").GetString());
        Assert.Equal("某帐号", first.GetProperty("account").GetProperty("name").GetString());
        var raw = json.GetRawText();
        Assert.DoesNotContain("SECRET-COOKIE", raw);
    }

    [Fact]
    public async Task Cookie_WithoutAccountInfo_AccountIsNull()
    {
        var cookies = fixture.Database
            .GetCollection<CookieRecord>(MongoCollectionNames.Cookies);
        await cookies.InsertOneAsync(new CookieRecord
        {
            Cookie = "some-cookie",
            Status = CookieStatus.Active,
        });

        var json = Ok(await CreateController(fixture.Database).ListCookies(CancellationToken.None));

        var account = json.GetProperty("items")[0].GetProperty("account");
        Assert.Equal(JsonValueKind.Null, account.ValueKind);
    }

    [Fact]
    public async Task Cookie_MultipleCookies_EachAccountJoined()
    {
        var cookies = fixture.Database
            .GetCollection<CookieRecord>(MongoCollectionNames.Cookies);
        var cookieA = new CookieRecord { Cookie = "cookie-a", Status = CookieStatus.Active };
        var cookieB = new CookieRecord { Cookie = "cookie-b", Status = CookieStatus.Active };
        await cookies.InsertOneAsync(cookieA);
        await cookies.InsertOneAsync(cookieB);

        var accountInfo = fixture.Database
            .GetCollection<CookieAccountInfoRecord>(MongoCollectionNames.CookieAccountInfo);
        await accountInfo.InsertOneAsync(new CookieAccountInfoRecord
        {
            CookieId = cookieA.Id,
            Mid = 1,
            Uname = "帐号A",
            FetchedAt = DateTime.UtcNow,
        });

        var json = Ok(await CreateController(fixture.Database).ListCookies(CancellationToken.None));

        var items = json.GetProperty("items");
        Assert.Equal(2, items.GetArrayLength());
        var dict = items.EnumerateArray().ToDictionary(
            i => i.GetProperty("id").GetString()!, i => i);

        var accountA = dict[cookieA.Id.ToString()].GetProperty("account");
        Assert.Equal("帐号A", accountA.GetProperty("name").GetString());

        var accountB = dict[cookieB.Id.ToString()].GetProperty("account");
        Assert.Equal(JsonValueKind.Null, accountB.ValueKind);
    }
}