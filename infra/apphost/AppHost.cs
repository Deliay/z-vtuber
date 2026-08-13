var builder = DistributedApplication.CreateBuilder(args);

var api = builder.AddProject<Projects.Z_Vtuber>("api")
    .WithExternalHttpEndpoints();

var web = builder.AddViteApp("web", "../../apps/web")
    .WithReference(api)
    .WithEnvironment("VITE_API_URL", api.GetEndpoint("http"))
    .WithExternalHttpEndpoints();

builder.Build().Run();
