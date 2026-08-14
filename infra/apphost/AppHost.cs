var builder = DistributedApplication.CreateBuilder(args);

var mongo = builder.AddMongoDB("mongo")
    .WithMongoExpress()
    .WithDataVolume();

var api = builder.AddProject<Projects.Z_Vtuber>("api")
    .WithReference(mongo)
    .WithExternalHttpEndpoints();

var web = builder.AddViteApp("web", "../../apps/web")
    .WithReference(api)
    .WithEnvironment("VITE_API_URL", api.GetEndpoint("http"))
    .WithExternalHttpEndpoints();

builder.Build().Run();
