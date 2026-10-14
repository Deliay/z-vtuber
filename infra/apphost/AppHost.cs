using Aspire.Hosting.ApplicationModel;

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

var recording = builder.AddProject<Projects.Z_Vtuber_Recording>("recording")
    .WithReference(mongo)
    .WaitFor(mongo)
    .WithReference(messaging)
    .WaitFor(messaging)
    .WithReference(cookie)
    .WaitFor(cookie)
    .WithEnvironment("CookieService:BaseUrl", cookie.GetEndpoint("http"))
    .WithEnvironment("Recording:StorageDir",
        Path.Combine(Path.GetTempPath(), "z-vtuber-recordings"))
    .WithHttpHealthCheck("/health");

var transcriptApi = builder.AddProject<Projects.Z_Vtuber_RecordingTranscript_Api>("recording-transcript-api")
    .WithReference(mongo)
    .WaitFor(mongo)
    .WithReference(messaging)
    .WaitFor(messaging)
    .WithReference(recording)
    .WaitFor(recording)
    .WithEnvironment("RecordingService:BaseUrl", recording.GetEndpoint("http"))
    .WithHttpHealthCheck("/health");

var transcriptConsumer = builder.AddProject<Projects.Z_Vtuber_RecordingTranscript_Consumer>("recording-transcript-consumer")
    .WithReference(mongo)
    .WaitFor(mongo)
    .WithReference(messaging)
    .WaitFor(messaging)
    .WithReference(recording)
    .WaitFor(recording)
    .WithEnvironment("RecordingService:BaseUrl", recording.GetEndpoint("http"))
    .WithHttpHealthCheck("/health");

var training = builder.AddProject<Projects.Z_Vtuber_Training>("training")
    .WithReference(mongo)
    .WaitFor(mongo)
    .WithReference(messaging)
    .WaitFor(messaging)
    .WithReference(recording)
    .WaitFor(recording)
    .WithEnvironment("TranscriptService__BaseUrl", transcriptApi.GetEndpoint("http"))
    .WithEnvironment("RecordingService__BaseUrl", recording.GetEndpoint("http"))
    .WithHttpHealthCheck("/health");

api.WithReference(recording)
    .WaitFor(recording)
    .WithEnvironment("RecordingService:BaseUrl", recording.GetEndpoint("http"));

api.WithReference(transcriptApi)
    .WaitFor(transcriptApi)
    .WithEnvironment("TranscriptService:BaseUrl", transcriptApi.GetEndpoint("http"));

api.WithReference(training)
    .WaitFor(training)
    .WithEnvironment("TrainingService:BaseUrl", training.GetEndpoint("http"));

api.WithReference(crawler)
    .WaitFor(crawler)
    .WithEnvironment("CrawlerService:BaseUrl", crawler.GetEndpoint("http"));

var web = builder.AddViteApp("web", "../../apps/web")
    .WithReference(api)
    .WithReference(notifier)
    .WithReference(cookie)
    .WithEnvironment("VITE_API_URL", api.GetEndpoint("http"))
    .WithEnvironment("VITE_NOTIFY_URL", notifier.GetEndpoint("http"))
    .WithEnvironment("VITE_COOKIE_URL", cookie.GetEndpoint("http"))
    .WithExternalHttpEndpoints();

builder.Build().Run();
