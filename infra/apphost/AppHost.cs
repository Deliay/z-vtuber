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

var web = builder.AddViteApp("web", "../../apps/web")
    .WithReference(api)
    .WithEnvironment("VITE_API_URL", api.GetEndpoint("http"))
    .WithExternalHttpEndpoints();

builder.Build().Run();
