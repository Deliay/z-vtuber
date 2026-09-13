var builder = DistributedApplication.CreateBuilder(args);

var mongo = builder.AddMongoDB("mongo")
    .WithImage("mongo", "7")
    .WithMongoExpress()
    .WithDataVolume();

var messaging = builder.AddRedis("messaging")
    .WithDataVolume();

var api = builder.AddProject<Projects.Z_Vtuber>("api")
    .WithReference(mongo)
    .WaitFor(mongo)
    .WithReference(messaging)
    .WaitFor(messaging)
    .WithExternalHttpEndpoints();

var cookie = builder.AddProject<Projects.Z_Vtuber_Cookie>("cookie")
    .WithReference(mongo)
    .WaitFor(mongo)
    .WithReference(messaging)
    .WaitFor(messaging)
    .WithEnvironment("CookieService:BrowserSessionDataDir",
        Path.Combine(Path.GetTempPath(), "z-vtuber-browser-sessions"))
    .WithHttpHealthCheck("/health");

var crawler = builder.AddProject<Projects.Z_Vtuber_Crawler>("crawler")
    .WithReference(mongo)
    .WaitFor(mongo)
    .WithReference(messaging)
    .WaitFor(messaging)
    .WithReference(cookie)
    .WaitFor(cookie)
    .WithEnvironment("CookieService:BaseUrl", cookie.GetEndpoint("http"))
    .WithHttpHealthCheck("/health");

var notifier = builder.AddProject<Projects.Z_Vtuber_Notifier>("notifier")
    .WithReference(mongo)
    .WaitFor(mongo)
    .WithReference(messaging)
    .WaitFor(messaging)
    .WithEnvironment("Z_VTUBER_MILKY_ENDPOINT", builder.AddParameter("milky-endpoint", secret: true))
    .WithEnvironment("Z_VTUBER_MILKY_ACCESS_TOKEN", builder.AddParameter("milky-access-token", secret: true))
    .WithHttpHealthCheck("/health");

var web = builder.AddViteApp("web", "../../apps/web")
    .WithReference(api)
    .WithReference(notifier)
    .WithReference(cookie)
    .WithEnvironment("VITE_API_URL", api.GetEndpoint("http"))
    .WithEnvironment("VITE_NOTIFY_URL", notifier.GetEndpoint("http"))
    .WithEnvironment("VITE_COOKIE_URL", cookie.GetEndpoint("http"))
    .WithExternalHttpEndpoints();

builder.Build().Run();
