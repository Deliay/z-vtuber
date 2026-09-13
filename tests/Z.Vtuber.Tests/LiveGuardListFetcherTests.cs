using Microsoft.Extensions.Logging.Abstractions;
using Mikibot.Crawler.Http.Bilibili.Model;
using Z.Vtuber.Common.Persistence.Entities;
using Z.Vtuber.Crawler.Configuration;
using Z.Vtuber.Crawler.Crawlers;
using Z.Vtuber.Crawler.Services;

namespace Z.Vtuber.Tests;

public class LiveGuardListFetcherTests
{
    private const long Mid = 1001;
    private const long RoomId = 123456;
    private const string Cookie = "test-cookie";

    private BilibiliCrawlerOptions GuardOptions() => new()
    {
        GuardMaxPages = 50,
        GuardPageDelayMinSeconds = 1,
        GuardPageDelayMaxSeconds = 1,
    };

    private sealed class CountingFetcher : LiveGuardListFetcher
    {
        public int DelayCount { get; private set; }

        public CountingFetcher(IBiliApiClient bili, BilibiliCrawlerOptions options)
            : base(bili, options, NullLogger<LiveGuardListFetcher>.Instance)
        {
        }

        internal override Task DelayBetweenPagesAsync(CancellationToken ct)
        {
            DelayCount++;
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task Fetch_MultiPage_MergesAllPages()
    {
        var pageData = new Dictionary<int, GuardInfo>
        {
            [1] = new() { Info = new GuardInfo.PageInfo { Count = 2, Current = 1, PageCount = 2 }, List = [new GuardUserInfo { Uid = 1 }, new GuardUserInfo { Uid = 2 }] },
            [2] = new() { Info = new GuardInfo.PageInfo { Count = 2, Current = 2, PageCount = 2 }, List = [new GuardUserInfo { Uid = 3 }] },
        };
        var bili = new FakeBiliApiClient { RoomGuardListProvider = (_, page) => pageData[page] };
        var fetcher = new CountingFetcher(bili, GuardOptions());

        var result = await fetcher.FetchAllAsync(Cookie, Cookie, Mid, RoomId, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(3, result.Count);
        Assert.Equal([1L, 2L, 3L], result.Select(g => g.Uid).OrderBy(u => u).ToList());
        Assert.Equal(1, fetcher.DelayCount);
    }

    [Fact]
    public async Task Fetch_FirstPageTop3_MergesIntoGuardList()
    {
        var bili = new FakeBiliApiClient
        {
            RoomGuardListProvider = (_, page) => page switch
            {
                1 => new GuardInfo
                {
                    Info = new GuardInfo.PageInfo { Count = 2, Current = 1, PageCount = 1 },
                    List = [new GuardUserInfo { Uid = 1, UserName = "user1" }, new GuardUserInfo { Uid = 2, UserName = "user2" }],
                    Top3 = [new GuardUserInfo { Uid = 3, UserName = "top3_user", GuardType = 5, MedalInfo = new GuardUserInfo.Medal { Name = "medal", Level = 10 } }],
                },
                _ => new GuardInfo { Info = new GuardInfo.PageInfo { PageCount = 1 }, List = [] },
            }
        };
        var fetcher = new CountingFetcher(bili, GuardOptions());

        var result = await fetcher.FetchAllAsync(Cookie, Cookie, Mid, RoomId, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(3, result.Count);
        var uids = result.Select(g => g.Uid).OrderBy(u => u).ToList();
        Assert.Equal([1L, 2L, 3L], uids);
        var top3Entry = result.First(g => g.Uid == 3);
        Assert.Equal("top3_user", top3Entry.UserName);
        Assert.Equal(5, top3Entry.GuardLevel);
        Assert.Equal("medal", top3Entry.MedalName);
        Assert.Equal(10, top3Entry.MedalLevel);
    }

    [Fact]
    public async Task Fetch_Top3OverlapWithList_DeduplicatesByUid()
    {
        var bili = new FakeBiliApiClient
        {
            RoomGuardListProvider = (_, page) => page switch
            {
                1 => new GuardInfo
                {
                    Info = new GuardInfo.PageInfo { Count = 2, Current = 1, PageCount = 1 },
                    List = [new GuardUserInfo { Uid = 1, UserName = "user1" }, new GuardUserInfo { Uid = 2, UserName = "user2" }],
                    Top3 = [new GuardUserInfo { Uid = 1, UserName = "top3_user1_dup" }],
                },
                _ => new GuardInfo { Info = new GuardInfo.PageInfo { PageCount = 1 }, List = [] },
            }
        };
        var fetcher = new CountingFetcher(bili, GuardOptions());

        var result = await fetcher.FetchAllAsync(Cookie, Cookie, Mid, RoomId, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(2, result.Count);
        var uids = result.Select(g => g.Uid).OrderBy(u => u).ToList();
        Assert.Equal([1L, 2L], uids);
    }

    [Fact]
    public async Task Fetch_Top3Null_FallsBackToGuardListOnly()
    {
        var bili = new FakeBiliApiClient
        {
            RoomGuardListProvider = (_, page) => page switch
            {
                1 => new GuardInfo
                {
                    Info = new GuardInfo.PageInfo { Count = 2, Current = 1, PageCount = 1 },
                    List = [new GuardUserInfo { Uid = 1, UserName = "user1" }, new GuardUserInfo { Uid = 2, UserName = "user2" }],
                    Top3 = null!,
                },
                _ => new GuardInfo { Info = new GuardInfo.PageInfo { PageCount = 1 }, List = [] },
            }
        };
        var fetcher = new CountingFetcher(bili, GuardOptions());

        var result = await fetcher.FetchAllAsync(Cookie, Cookie, Mid, RoomId, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(2, result.Count);
    }

    [Fact]
    public async Task Fetch_Top3EmptyList_FallsBackToGuardListOnly()
    {
        var bili = new FakeBiliApiClient
        {
            RoomGuardListProvider = (_, page) => page switch
            {
                1 => new GuardInfo
                {
                    Info = new GuardInfo.PageInfo { Count = 2, Current = 1, PageCount = 1 },
                    List = [new GuardUserInfo { Uid = 1, UserName = "user1" }, new GuardUserInfo { Uid = 2, UserName = "user2" }],
                    Top3 = [],
                },
                _ => new GuardInfo { Info = new GuardInfo.PageInfo { PageCount = 1 }, List = [] },
            }
        };
        var fetcher = new CountingFetcher(bili, GuardOptions());

        var result = await fetcher.FetchAllAsync(Cookie, Cookie, Mid, RoomId, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(2, result.Count);
    }

    [Fact]
    public async Task Fetch_MultiPageTop3_NotMergedBeyondFirstPage()
    {
        var bili = new FakeBiliApiClient
        {
            RoomGuardListProvider = (_, page) => page switch
            {
                1 => new GuardInfo
                {
                    Info = new GuardInfo.PageInfo { Count = 1, Current = 1, PageCount = 2 },
                    List = [new GuardUserInfo { Uid = 1, UserName = "user1" }],
                    Top3 = [new GuardUserInfo { Uid = 10, UserName = "top3_page1" }],
                },
                2 => new GuardInfo
                {
                    Info = new GuardInfo.PageInfo { Count = 1, Current = 2, PageCount = 2 },
                    List = [new GuardUserInfo { Uid = 2, UserName = "user2" }],
                    Top3 = [new GuardUserInfo { Uid = 20, UserName = "top3_page2_should_not_merge" }],
                },
                _ => new GuardInfo { Info = new GuardInfo.PageInfo { PageCount = 1 }, List = [] },
            }
        };
        var fetcher = new CountingFetcher(bili, GuardOptions());

        var result = await fetcher.FetchAllAsync(Cookie, Cookie, Mid, RoomId, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(3, result.Count);
        var uids = result.Select(g => g.Uid).OrderBy(u => u).ToList();
        Assert.Equal([1L, 2L, 10L], uids);
    }

    [Fact]
    public async Task Fetch_EmptyFirstPage_ReturnsEmptyList()
    {
        var bili = new FakeBiliApiClient { RoomGuardListProvider = (_, _) => new GuardInfo { Info = new GuardInfo.PageInfo { Count = 0, Current = 1, PageCount = 1 }, List = [] } };
        var fetcher = new CountingFetcher(bili, GuardOptions());

        var result = await fetcher.FetchAllAsync(Cookie, Cookie, Mid, RoomId, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Empty(result);
    }

    [Fact]
    public async Task Fetch_BackoffActive_ReturnsNull()
    {
        var bili = new FakeBiliApiClient { RoomGuardListProvider = (_, _) => throw new CrawlBackoffActiveException("backoff", TimeSpan.FromMinutes(1)) };
        var fetcher = new CountingFetcher(bili, GuardOptions());

        var result = await fetcher.FetchAllAsync(Cookie, Cookie, Mid, RoomId, CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task Fetch_SinglePageFailure_ReturnsNull()
    {
        var bili = new FakeBiliApiClient { RoomGuardListProvider = (_, _) => throw new Exception("boom") };
        var fetcher = new CountingFetcher(bili, GuardOptions());

        var result = await fetcher.FetchAllAsync(Cookie, Cookie, Mid, RoomId, CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task Fetch_PageCountCap_LimitsRequests()
    {
        var options = GuardOptions();
        options.GuardMaxPages = 2;
        var pagesRequested = 0;
        var bili = new FakeBiliApiClient
        {
            RoomGuardListProvider = (_, _) =>
            {
                pagesRequested++;
                return new GuardInfo { Info = new GuardInfo.PageInfo { Count = 100, Current = pagesRequested, PageCount = 100 }, List = [new GuardUserInfo { Uid = pagesRequested }] };
            }
        };
        var fetcher = new CountingFetcher(bili, options);

        var result = await fetcher.FetchAllAsync(Cookie, Cookie, Mid, RoomId, CancellationToken.None);

        Assert.Equal(2, pagesRequested);
        Assert.NotNull(result);
        Assert.Equal(2, result.Count);
        Assert.Equal(1, fetcher.DelayCount);
    }
}