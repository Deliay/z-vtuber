using Aspire.Hosting.ApplicationModel;

var builder = DistributedApplication.CreateBuilder(args);

// 单节点 replica set（事务前置）：容器以 --replSet rs0 启动，init 脚本负责 rs.initiate；
// 连接串追加 directConnection=true，客户端直连种子节点、跳过拓扑发现（replica set 通告的容器地址对宿主机不可达）。
//
// !!! 注意（Mongo 数据卷迁移）!!!
// mongo/rs-init.js 由 WithInitFiles 挂载，docker-entrypoint 仅在数据目录【为空】的首次启动时执行它。
// 若复用已有的 standalone Mongo 数据卷（旧版本 AppHost 留下的卷），则：
//   - mongod 仍以 --replSet rs0 启动，但 rs.initiate 不会执行，副本集永远没有 primary；
//   - MongoDB 事务（如转写 segment+position 落库）会永久失败，且没有明显的启动报错，极难排查。
// 处理办法（首次以 replica set 运行前二选一）：
//   1) 删除/重建该 Mongo 数据卷：docker volume rm <apphost>-mongo-data（或 podman volume rm），再启动 AppHost；或
//   2) 手动进入 mongo 容器执行 rs.initiate({ _id: "rs0", members: [{ _id: 0, host: "mongo:27017" }] })。
// 切勿在未清理旧卷时直接复用，否则所有依赖事务的转写写入都会失败。
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
