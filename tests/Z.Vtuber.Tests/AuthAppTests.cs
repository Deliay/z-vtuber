using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Z.Vtuber.Auth;

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
        Assert.NotNull(ActivatorUtilities.CreateInstance<Z.Vtuber.Auth.Controllers.PagesController>(services));
        Assert.NotNull(ActivatorUtilities.CreateInstance<Z.Vtuber.Auth.Controllers.DanmakuRoomsController>(services));
        Assert.NotNull(ActivatorUtilities.CreateInstance<Z.Vtuber.Auth.Controllers.MeController>(services));
        Assert.NotNull(ActivatorUtilities.CreateInstance<Z.Vtuber.Auth.OAuth.OAuthAuthorizeController>(services));
        Assert.NotNull(ActivatorUtilities.CreateInstance<Z.Vtuber.Auth.OAuth.OAuthUserInfoController>(services));
    }

    [Fact]
    public void Build_WithShortPepper_FailsFast()
    {
        var overrides = Overrides();
        overrides["Auth:TokenPepper"] = "short";
        Assert.Throws<OptionsValidationException>(() => AuthApp.Build([], overrides));
    }
}
