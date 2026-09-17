using System.Reflection;
using Microsoft.Playwright;

namespace Z.Vtuber.Tests;

/// <summary>
/// 基于 <see cref="DispatchProxy"/> 的 Playwright 接口测试替身工厂，避免手写庞大的浏览器接口。
/// 未显式处理的成员将抛 <see cref="NotImplementedException"/>。
/// </summary>
public class FakeProxy<T> : DispatchProxy where T : class
{
    public Func<MethodInfo, object?[]?, object?>? Handler { get; set; }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        => Handler is null
            ? throw new NotImplementedException($"{targetMethod?.DeclaringType?.Name}.{targetMethod?.Name}")
            : Handler(targetMethod!, args);

    public static T Create(Func<MethodInfo, object?[]?, object?> handler)
    {
        var proxy = DispatchProxy.Create<T, FakeProxy<T>>();
        ((FakeProxy<T>)(object)proxy).Handler = handler;
        return proxy;
    }
}

internal sealed class FakeApiRequest
{
    public Func<string, string> ResponseBodyProvider { get; set; } = _ => string.Empty;
    public int GetCount { get; private set; }
    public IAPIRequestContext Proxy { get; }

    public FakeApiRequest()
    {
        Proxy = FakeProxy<IAPIRequestContext>.Create((method, args) =>
        {
            if (method.Name == "GetAsync")
            {
                GetCount++;
                var url = (string)args![0]!;
                var body = ResponseBodyProvider(url);
                var response = FakeProxy<IAPIResponse>.Create((m, _) =>
                    m.Name == "TextAsync" ? Task.FromResult(body) : throw new NotImplementedException(m.Name));
                return Task.FromResult(response);
            }

            throw new NotImplementedException($"{method.DeclaringType?.Name}.{method.Name}");
        });
    }
}

internal sealed class FakePage
{
    private EventHandler<IResponse>? _responseHandler;

    public string ResponseUrl { get; set; } = "https://www.bilibili.com/x/web-interface/nav";
    public string ResponseBody { get; set; } = """{"code":0,"data":{"isLogin":true,"mid":1,"uname":"u"}}""";
    public IPage Proxy { get; }

    public FakePage()
    {
        Proxy = FakeProxy<IPage>.Create((method, args) =>
        {
            switch (method.Name)
            {
                case "add_Response":
                    _responseHandler += (EventHandler<IResponse>)args![0]!;
                    return null;
                case "remove_Response":
                    _responseHandler -= (EventHandler<IResponse>)args![0]!;
                    return null;
                case "GotoAsync":
                    RaiseResponse();
                    return Task.FromResult<IResponse?>(null);
                case "CloseAsync":
                    return Task.CompletedTask;
                case "DisposeAsync":
                    return ValueTask.CompletedTask;
                default:
                    throw new NotImplementedException($"{method.DeclaringType?.Name}.{method.Name}");
            }
        });
    }

    private void RaiseResponse()
    {
        if (_responseHandler is null)
        {
            return;
        }

        var response = FakeProxy<IResponse>.Create((method, _) => method.Name switch
        {
            "get_Url" => (object?)ResponseUrl,
            "TextAsync" => Task.FromResult(ResponseBody),
            _ => throw new NotImplementedException(method.Name),
        });

        _responseHandler.Invoke(this, response);
    }
}

internal sealed class FakeBrowserContext
{
    public FakeBrowserContext(string accountId)
    {
        AccountId = accountId;
        Proxy = FakeProxy<IBrowserContext>.Create(Handle);
    }

    public string AccountId { get; }
    public List<IPage> Pages { get; } = [];
    public List<FakePage> CreatedPages { get; } = [];
    public List<BrowserContextCookiesResult> CookieResults { get; } = [];
    public FakeApiRequest ApiRequest { get; } = new();
    public string PageResponseBody { get; set; } = """{"code":0,"data":{"isLogin":true,"mid":1,"uname":"u"}}""";
    public string PageResponseUrl { get; set; } = "https://www.bilibili.com/x/web-interface/nav";
    public int CloseCount { get; private set; }
    public int NewPageCount { get; private set; }
    public bool IsClosed { get; private set; }
    public IBrowserContext Proxy { get; }

    private object? Handle(MethodInfo method, object?[]? args)
    {
        switch (method.Name)
        {
            case "get_Pages":
                return (IReadOnlyList<IPage>)Pages;
            case "get_APIRequest":
                return ApiRequest.Proxy;
            case "CookiesAsync":
                return Task.FromResult<IReadOnlyList<BrowserContextCookiesResult>>(CookieResults);
            case "NewPageAsync":
            {
                NewPageCount++;
                var page = new FakePage { ResponseBody = PageResponseBody, ResponseUrl = PageResponseUrl };
                CreatedPages.Add(page);
                Pages.Add(page.Proxy);
                return Task.FromResult(page.Proxy);
            }
            case "CloseAsync":
                CloseCount++;
                IsClosed = true;
                return Task.CompletedTask;
            case "DisposeAsync":
                return ValueTask.CompletedTask;
            default:
                throw new NotImplementedException($"{method.DeclaringType?.Name}.{method.Name}");
        }
    }
}

internal sealed class FakeBrowserHarness
{
    public FakeBrowserHarness()
    {
        BrowserType = FakeProxy<IBrowserType>.Create((method, args) =>
        {
            if (method.Name == "LaunchPersistentContextAsync")
            {
                var userDataDir = (string)args![0]!;
                LaunchedUserDataDirs.Add(userDataDir);
                LaunchCount++;
                var accountId = Path.GetFileName(userDataDir);
                var context = ContextFactory(accountId);
                Contexts[accountId] = context;
                return Task.FromResult(context.Proxy);
            }

            throw new NotImplementedException($"{method.DeclaringType?.Name}.{method.Name}");
        });
    }

    public Func<string, FakeBrowserContext> ContextFactory { get; set; } = accountId => new FakeBrowserContext(accountId);
    public List<string> LaunchedUserDataDirs { get; } = [];
    public Dictionary<string, FakeBrowserContext> Contexts { get; } = [];
    public int LaunchCount { get; private set; }
    public IBrowserType BrowserType { get; }
}

internal static class TestWait
{
    public static async Task UntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(10);
        }

        Assert.True(condition(), "Condition was not met within the timeout.");
    }
}
