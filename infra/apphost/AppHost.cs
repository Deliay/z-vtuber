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

var crawler = builder.AddProject<Projects.Z_Vtuber_Crawler>("crawler")
    .WithReference(mongo)
    .WaitFor(mongo)
    .WithReference(messaging)
    .WaitFor(messaging)
    .WithHttpHealthCheck("/health");

var notifier = builder.AddProject<Projects.Z_Vtuber_Notifier>("notifier")
    .WithReference(mongo)
    .WaitFor(mongo)
    .WithReference(messaging)
    .WaitFor(messaging)
    .WithEnvironment("Z_VTUBER_SATORI_ENDPOINT", builder.AddParameter("satori-endpoint", secret: true))
    .WithEnvironment("Z_VTUBER_SATORI_TOKEN", builder.AddParameter("satori-token", secret: true))
    .WithHttpHealthCheck("/health");

var web = builder.AddViteApp("web", "../../apps/web")
    .WithReference(api)
    .WithReference(notifier)
    .WithEnvironment("VITE_API_URL", api.GetEndpoint("http"))
    .WithEnvironment("VITE_NOTIFY_URL", notifier.GetEndpoint("http"))
    .WithExternalHttpEndpoints();

builder.Build().Run();
