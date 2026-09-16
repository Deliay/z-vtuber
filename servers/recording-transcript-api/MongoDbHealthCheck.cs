using MongoDB.Bson;
using MongoDB.Driver;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Z.Vtuber.RecordingTranscript.Api;

public sealed class MongoDbHealthCheck(
    IMongoDatabase database,
    ILogger<MongoDbHealthCheck> logger) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await database.RunCommandAsync<BsonDocument>(
                new BsonDocument("ping", 1),
                cancellationToken: cancellationToken);
            return HealthCheckResult.Healthy("MongoDB is reachable.");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "MongoDB connectivity check failed.");
            return HealthCheckResult.Unhealthy("MongoDB is unreachable.", ex);
        }
    }
}
