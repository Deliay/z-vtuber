using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using MongoDB.Driver;
using StackExchange.Redis;
using Z.Vtuber.Auth.Auth;
using Z.Vtuber.Auth.Configuration;
using Z.Vtuber.Auth.Controllers;
using Z.Vtuber.Auth.Danmaku;
using Z.Vtuber.Auth.Health;
using Z.Vtuber.Auth.Middleware;
using Z.Vtuber.Auth.OAuth;
using Z.Vtuber.Auth.Persistence;
using Z.Vtuber.Common.Persistence;

namespace Z.Vtuber.Auth;

/// <summary>
/// 可测试的宿主入口：<see cref="Build"/> 返回未启动的 <see cref="WebApplication"/>，
/// 测试可用临时端口启动真实 Kestrel。
/// </summary>
public static class AuthApp
{
    public static WebApplication Build(
        string[] args,
        IDictionary<string, string?>? overrides = null,
        Action<IServiceCollection>? configureServices = null)
    {
        var builder = WebApplication.CreateBuilder(args);
        if (overrides is not null)
        {
            builder.Configuration.AddInMemoryCollection(overrides);
        }

        ConfigureServices(builder);
        configureServices?.Invoke(builder.Services);

        var app = builder.Build();
        ConfigurePipeline(app);
        return app;
    }

    private static void ConfigureServices(WebApplicationBuilder builder)
    {
        var services = builder.Services;

        services.AddControllers();
        services.AddHttpContextAccessor();
        services.AddHealthChecks()
            .AddCheck<MongoDbHealthCheck>("mongo")
            .AddCheck<RedisHealthCheck>("redis")
            .AddCheck<DanmakuReadyHealthCheck>("danmaku");

        services.AddOptions<AuthOptions>()
            .BindConfiguration(AuthOptions.SectionName)
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<AuthOptions>, AuthOptionsValidator>();

        services.AddOptions<DanmakuOptions>()
            .BindConfiguration(DanmakuOptions.SectionName)
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<DanmakuOptions>, DanmakuOptionsValidator>();

        services.AddOptions<OAuthProviderOptions>()
            .BindConfiguration(OAuthProviderOptions.SectionName)
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<OAuthProviderOptions>, OAuthProviderOptionsValidator>();

        services.AddOptions<CasdoorOptions>()
            .BindConfiguration(CasdoorOptions.SectionName);

        services.AddSingleton(sp => sp.GetRequiredService<IOptions<AuthOptions>>().Value);
        services.AddSingleton(sp => sp.GetRequiredService<IOptions<DanmakuOptions>>().Value);
        services.AddSingleton(sp => sp.GetRequiredService<IOptions<OAuthProviderOptions>>().Value);
        services.AddSingleton(sp => sp.GetRequiredService<IOptions<CasdoorOptions>>().Value);
        services.AddSingleton(TimeProvider.System);

        services.AddSingleton<IMongoClient>(sp =>
        {
            var connectionString = sp.GetRequiredService<IConfiguration>().GetConnectionString("mongo")
                ?? throw new InvalidOperationException("Missing ConnectionStrings:mongo");
            return new MongoClient(connectionString);
        });
        services.AddSingleton<IMongoDatabase>(sp =>
            sp.GetRequiredService<IMongoClient>().GetDatabase(MongoDbInitializer.DatabaseName));

        services.AddSingleton<RedisConnection>();

        services.AddSingleton<AuthMongoSetup>();
        services.AddSingleton<LoginRequestRepository>();
        services.AddSingleton<UserRepository>();
        services.AddSingleton<ProviderSessionRepository>();
        services.AddSingleton<ProviderTicketRepository>();
        services.AddSingleton<OAuthCodeRepository>();
        services.AddSingleton<ProviderTokenRepository>();
        services.AddSingleton<StandaloneSessionRepository>();
        services.AddSingleton<AuditRepository>();

        services.AddSingleton<TokenHasher>();
        services.AddSingleton<UserService>();
        services.AddSingleton<AuditLogger>();
        services.AddSingleton<IAuthRateLimiter>(sp => new RedisRateLimiter(
            sp.GetRequiredService<RedisConnection>().Value,
            sp.GetRequiredService<IOptions<AuthOptions>>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<ILogger<RedisRateLimiter>>()));
        services.AddSingleton<ProviderSessionService>();
        services.AddSingleton<ProviderTicketService>();
        services.AddSingleton<AuthorizationCodeService>();
        services.AddSingleton<ProviderTokenService>();
        services.AddSingleton<StandaloneSessionService>();
        services.AddSingleton<LoginRequestService>();
        services.AddSingleton<LoginRequestScanner>();
        services.AddSingleton<IDanmakuEventHandler, DanmakuEventHandler>();
        services.AddSingleton<DanmakuHealthRegistry>();
        services.AddSingleton<DanmakuDeduplicator>();
        services.AddSingleton<IRoomLeaderLease>(sp => new RoomLeaderLease(
            sp.GetRequiredService<RedisConnection>().Value,
            sp.GetRequiredService<IOptions<AuthOptions>>(),
            sp.GetRequiredService<IOptions<DanmakuOptions>>(),
            sp.GetRequiredService<ILogger<RoomLeaderLease>>()));

        services.AddHttpClient<DanmakuBackfillClient>((sp, client) =>
        {
            var options = sp.GetRequiredService<IOptions<DanmakuOptions>>().Value;
            client.BaseAddress = new Uri(options.BaseUrl);
            client.Timeout = TimeSpan.FromSeconds(Math.Max(5, options.ReadTimeoutSeconds));
        });
        services.AddHttpClient("DanmakuSource", (sp, client) =>
        {
            var options = sp.GetRequiredService<IOptions<DanmakuOptions>>().Value;
            client.BaseAddress = new Uri(options.BaseUrl);
            client.Timeout = Timeout.InfiniteTimeSpan;
            client.DefaultRequestHeaders.Accept.ParseAdd("text/event-stream");
        }).ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(15),
            PooledConnectionLifetime = TimeSpan.Zero
        });

        services.AddHostedService<AuthIndexInitializer>();
        services.AddHostedService(sp => sp.GetRequiredService<LoginRequestScanner>());
        services.AddHostedService<RoomEventSourceHost>();

        var origins = builder.Configuration.GetSection($"{AuthOptions.SectionName}:AllowedOrigins").Get<string[]>() ?? [];
        services.AddCors(options =>
        {
            options.AddPolicy("AuthApi", policy =>
            {
                if (origins.Length > 0)
                {
                    policy.WithOrigins(origins).AllowAnyHeader().AllowAnyMethod();
                }
            });
        });
    }

    private static void ConfigurePipeline(WebApplication app)
    {
        var authOptions = app.Services.GetRequiredService<IOptions<AuthOptions>>().Value;
        if (authOptions.TrustForwardedHeaders)
        {
            app.UseForwardedHeaders(new ForwardedHeadersOptions
            {
                ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
            });
        }

        app.UseMiddleware<SecurityHeadersMiddleware>();
        app.UseMiddleware<AuthExceptionMiddleware>();
        app.UseStaticFiles();
        app.UseCors("AuthApi");

        app.MapHealthChecks("/health", new HealthCheckOptions { Predicate = _ => false });
        app.MapHealthChecks("/health/ready");
        app.MapControllers();
    }
}

/// <summary>启动时幂等建索引；失败则整个服务启动失败。</summary>
public sealed class AuthIndexInitializer(
    AuthMongoSetup setup,
    ILogger<AuthIndexInitializer> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("Initializing auth MongoDB indexes.");
        await setup.EnsureIndexesAsync(cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
