namespace Z.Vtuber.RecordingTranscript.Consumer;

public sealed class MongoDbHealthCheck(
    MongoDB.Driver.IMongoDatabase database,
    ILogger<MongoDbHealthCheck> logger) : Microsoft.Extensions.Diagnostics.HealthChecks.IHealthCheck
{
    public async Task<Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult> CheckHealthAsync(
        Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await database.RunCommandAsync<MongoDB.Bson.BsonDocument>(
                new MongoDB.Bson.BsonDocument("ping", 1),
                cancellationToken: cancellationToken);
            return Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult.Healthy("MongoDB is reachable.");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "MongoDB connectivity check failed.");
            return Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult.Unhealthy("MongoDB is unreachable.", ex);
        }
    }
}
