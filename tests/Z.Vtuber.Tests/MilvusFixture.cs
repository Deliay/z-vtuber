using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Testcontainers.Milvus;
using Z.Vtuber.Training.Configuration;
using Z.Vtuber.Training.Vectors;
using Xunit;

namespace Z.Vtuber.Tests;

/// <summary>
/// 真 Milvus（Testcontainers <c>milvusdb/milvus</c>）集成 fixture：开启鉴权
/// （<c>COMMON_SECURITY_AUTHORIZATIONENABLED=true</c>，root 密码=<see cref="TestPassword"/>），
/// 复刻 Aspire.Hosting.Milvus 托管口径，用于实证手写 REST/FLAT/tie-break 链路与鉴权 token <c>root:{password}</c>。
/// 容器按需惰性启动：仅当测试真正打真 Milvus（<see cref="CreateStore"/>/<see cref="CreateRestClient"/>）时才拉起，
/// 不影响同夹具下的纯 stub/错误矩阵用例。
/// </summary>
public sealed class MilvusFixture : IAsyncLifetime
{
    public const string TestPassword = "MilvusTestPw1";

    private static readonly MilvusContainer SharedContainer = new MilvusBuilder("milvusdb/milvus:v2.5.27")
        .WithEnvironment("COMMON_SECURITY_AUTHORIZATIONENABLED", "true")
        .WithEnvironment("COMMON_SECURITY_DEFAULTROOTPASSWORD", TestPassword)
        .Build();

    private static readonly SemaphoreSlim StartLock = new(1, 1);
    private static bool _started;

    /// <summary>REST API v2 base（shared-port 模式 = gRPC 19530）。</summary>
    public string Endpoint => SharedContainer.GetEndpoint().ToString().TrimEnd('/');

    /// <summary>正确 token = <c>root:{password}</c>（对应 Aspire <c>MilvusServerResource.Token</c>）。</summary>
    public string Token => $"root:{TestPassword}";

    /// <summary>错误 token（裸密码）——复刻问题 1 的配置，应得 401。</summary>
    public string BarePasswordToken => TestPassword;

    // 夹具本身不拉起容器，避免纯 stub 用例耦合 Docker；由真实调用惰性启动。
    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>建一个打真 Milvus 的 <see cref="MilvusVectorStore"/>（token 默认取 <see cref="Token"/>）。</summary>
    public MilvusVectorStore CreateStore(int dimensions = 4, string? token = null, string? textCollection = null)
    {
        EnsureStarted();
        var restClient = CreateRestClient(token ?? Token);
        return new MilvusVectorStore(
            restClient,
            Options.Create(new MilvusOptions
            {
                Endpoint = Endpoint,
                Token = token ?? Token,
                TextCollection = textCollection ?? $"txt_{Guid.NewGuid():N}",
                EmotionCollection = $"emo_{Guid.NewGuid():N}",
            }),
            Options.Create(new EmbeddingOptions { Dimensions = dimensions }),
            NullLogger<MilvusVectorStore>.Instance);
    }

    /// <summary>建一个打真 Milvus 的 <see cref="MilvusRestClient"/>（真实 HTTP）。</summary>
    public MilvusRestClient CreateRestClient(string? token = null, int timeoutSeconds = 30)
    {
        EnsureStarted();
        return new MilvusRestClient(
            new StubHttpClientFactory(new HttpClient(new SocketsHttpHandler())),
            Options.Create(new MilvusOptions
            {
                Endpoint = Endpoint,
                Token = token ?? Token,
                RequestTimeoutSeconds = timeoutSeconds,
            }));
    }

    private static void EnsureStarted()
    {
        if (_started)
        {
            return;
        }

        StartLock.Wait();
        try
        {
            if (_started)
            {
                return;
            }

            SharedContainer.StartAsync().GetAwaiter().GetResult();
            _started = true;
        }
        finally
        {
            StartLock.Release();
        }
    }
}
