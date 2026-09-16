using Aspire.Hosting.ApplicationModel;

var builder = DistributedApplication.CreateBuilder(args);

// 单节点 replica set（事务前置）：容器以 --replSet rs0 启动，init 脚本负责 rs.initiate；
// 连接串追加 directConnection=true，客户端直连种子节点、跳过拓扑发现（replica set 通告的容器地址对宿主机不可达）。
var mongo = builder.AddMongoDB("mongo")
    .WithImage("mongo", "7")
    .WithMongoExpress()
    .WithDataVolume()
    .WithArgs("--replSet", "rs0")
    .WithInitFiles(Path.Combine(builder.AppHostDirectory, "mongo"));

// Aspire 生成的连接串已带查询串（如 ?authSource=admin&authMechanism=SCRAM-SHA-256），据此选择分隔符。
var mongoConnectionString = mongo.Resource.ConnectionStringExpression;
var directConnectionSeparator = mongoConnectionString.Format.Contains('?') ? "&" : "?";
var mongoConnectionWithDirectMode = ReferenceExpression.Create(
    $"{mongoConnectionString}{directConnectionSeparator}directConnection=true");

var messaging = builder.AddRedis("messaging")
    .WithDataVolume();

IResourceBuilder<ProjectResource> WithMongo(IResourceBuilder<ProjectResource> project) =>
    project.WithReference(mongo)
        .WaitFor(mongo)
        .WithEnvironment("ConnectionStrings__mongo", mongoConnectionWithDirectMode);

var api = WithMongo(builder.AddProject<Projects.Z_Vtuber>("api"))
    .WithReference(messaging)
    .WaitFor(messaging)
    .WithExternalHttpEndpoints();

var cookie = WithMongo(builder.AddProject<Projects.Z_Vtuber_Cookie>("cookie"))
    .WithReference(messaging)
    .WaitFor(messaging)
    .WithEnvironment("CookieService:BrowserSessionDataDir",
        Path.Combine(Path.GetTempPath(), "z-vtuber-browser-sessions"))
    .WithHttpHealthCheck("/health");

var crawler = WithMongo(builder.AddProject<Projects.Z_Vtuber_Crawler>("crawler"))
    .WithReference(messaging)
    .WaitFor(messaging)
    .WithReference(cookie)
    .WaitFor(cookie)
    .WithEnvironment("CookieService:BaseUrl", cookie.GetEndpoint("http"))
    .WithHttpHealthCheck("/health");

var notifier = WithMongo(builder.AddProject<Projects.Z_Vtuber_Notifier>("notifier"))
    .WithReference(messaging)
    .WaitFor(messaging)
    .WithEnvironment("Z_VTUBER_MILKY_ENDPOINT", builder.AddParameter("milky-endpoint", secret: true))
    .WithEnvironment("Z_VTUBER_MILKY_ACCESS_TOKEN", builder.AddParameter("milky-access-token", secret: true))
    .WithHttpHealthCheck("/health");

var recording = WithMongo(builder.AddProject<Projects.Z_Vtuber_Recording>("recording"))
    .WithReference(messaging)
    .WaitFor(messaging)
    .WithReference(cookie)
    .WaitFor(cookie)
    .WithEnvironment("CookieService:BaseUrl", cookie.GetEndpoint("http"))
    .WithEnvironment("Recording:StorageDir",
        Path.Combine(Path.GetTempPath(), "z-vtuber-recordings"))
    .WithHttpHealthCheck("/health");

var transcriptApi = WithMongo(
        builder.AddProject<Projects.Z_Vtuber_RecordingTranscript_Api>("recording-transcript-api"))
    .WithReference(messaging)
    .WaitFor(messaging)
    .WithHttpHealthCheck("/health");

var transcriptConsumer = WithMongo(
        builder.AddProject<Projects.Z_Vtuber_RecordingTranscript_Consumer>("recording-transcript-consumer"))
    .WithReference(messaging)
    .WaitFor(messaging)
    .WithReference(recording)
    .WaitFor(recording)
    .WithEnvironment("RecordingService:BaseUrl", recording.GetEndpoint("http"))
    .WithHttpHealthCheck("/health");

api.WithReference(recording)
    .WaitFor(recording)
    .WithEnvironment("RecordingService:BaseUrl", recording.GetEndpoint("http"));

api.WithReference(transcriptApi)
    .WaitFor(transcriptApi)
    .WithEnvironment("TranscriptService:BaseUrl", transcriptApi.GetEndpoint("http"));

var web = builder.AddViteApp("web", "../../apps/web")
    .WithReference(api)
    .WithReference(notifier)
    .WithReference(cookie)
    .WithEnvironment("VITE_API_URL", api.GetEndpoint("http"))
    .WithEnvironment("VITE_NOTIFY_URL", notifier.GetEndpoint("http"))
    .WithEnvironment("VITE_COOKIE_URL", cookie.GetEndpoint("http"))
    .WithExternalHttpEndpoints();

builder.Build().Run();
