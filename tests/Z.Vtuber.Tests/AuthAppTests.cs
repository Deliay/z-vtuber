using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using System.Diagnostics.Metrics;
using Z.Vtuber.Auth;
using Z.Vtuber.Auth.Observability;

namespace Z.Vtuber.Tests;

public sealed class AuthAppTests
{
    private static Dictionary<string, string?> Overrides() => new()
    {
        ["Auth:Mode"] = "standalone",
        ["Auth:TokenPepper"] = AuthTestOptions.Pepper,
        ["Auth:PublicBaseUrl"] = "http://localhost:8087",
        ["Auth:Danmaku:RoomIds:0"] = "30066",
        ["ConnectionStrings:mongo"] = "mongodb://localhost:27017",
        ["ConnectionStrings:messaging"] = "localhost:6379"
    };

    [Fact]
    public void Build_WithValidConfiguration_Succeeds()
    {
        using var app = AuthApp.Build([], Overrides());
        Assert.NotNull(app.Services);
    }

    [Fact]
    public void Build_ResolvesCoreServicesAndControllers()
    {
        using var app = AuthApp.Build([], Overrides());
        using var scope = app.Services.CreateScope();
        var services = scope.ServiceProvider;
        Assert.NotNull(services.GetRequiredService<Z.Vtuber.Auth.Auth.LoginRequestService>());
        Assert.NotNull(services.GetRequiredService<Z.Vtuber.Auth.Observability.AuthMetrics>());
        Assert.NotNull(ActivatorUtilities.CreateInstance<Z.Vtuber.Auth.Controllers.PagesController>(services));
        Assert.NotNull(ActivatorUtilities.CreateInstance<Z.Vtuber.Auth.Controllers.DanmakuRoomsController>(services));
        Assert.NotNull(ActivatorUtilities.CreateInstance<Z.Vtuber.Auth.Controllers.MeController>(services));
        Assert.NotNull(ActivatorUtilities.CreateInstance<Z.Vtuber.Auth.OAuth.OAuthAuthorizeController>(services));
        Assert.NotNull(ActivatorUtilities.CreateInstance<Z.Vtuber.Auth.OAuth.OAuthUserInfoController>(services));
    }

    [Fact]
    public void AuthMetrics_EmitsStructuredCounters()
    {
        var measurements = new List<(string Name, long Value)>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, current) =>
        {
            if (instrument.Meter.Name == AuthMetrics.MeterName)
            {
                current.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, _, _) =>
        {
            lock (measurements)
            {
                measurements.Add((instrument.Name, value));
            }
        });
        listener.Start();

        var metrics = new AuthMetrics(new TestMeterFactory());
        metrics.LoginCreated();
        metrics.LoginVerified();
        metrics.LoginConfirmed();
        metrics.LoginFailed();
        metrics.RoomConnected();
        metrics.RoomReconnect();
        metrics.RoomBackfillProcessed(3);
        metrics.RoomBackfillTruncated();
        metrics.OAuthAuthorize();
        metrics.OAuthToken();
        metrics.OAuthFailure();

        lock (measurements)
        {
            Assert.Contains(measurements, x => x == ("auth.login.created", 1));
            Assert.Contains(measurements, x => x == ("auth.login.verified", 1));
            Assert.Contains(measurements, x => x == ("auth.login.confirmed", 1));
            Assert.Contains(measurements, x => x == ("auth.login.failed", 1));
            Assert.Contains(measurements, x => x == ("auth.danmaku.room.connects", 1));
            Assert.Contains(measurements, x => x == ("auth.danmaku.room.reconnects", 1));
            Assert.Contains(measurements, x => x == ("auth.danmaku.room.backfill.events", 3));
            Assert.Contains(measurements, x => x == ("auth.danmaku.room.backfill.truncated", 1));
            Assert.Contains(measurements, x => x == ("auth.oauth.authorize", 1));
            Assert.Contains(measurements, x => x == ("auth.oauth.token", 1));
            Assert.Contains(measurements, x => x == ("auth.oauth.failures", 1));
        }
    }

    [Fact]
    public void Build_WithShortPepper_FailsFast()
    {
        var overrides = Overrides();
        overrides["Auth:TokenPepper"] = "short";
        Assert.Throws<OptionsValidationException>(() => AuthApp.Build([], overrides));
    }
}
