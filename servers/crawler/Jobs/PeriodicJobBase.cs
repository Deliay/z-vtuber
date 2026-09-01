using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Z.Vtuber.Tests")]

namespace Z.Vtuber.Crawler.Jobs;

public abstract class PeriodicJobBase(ILogger logger) : BackgroundService
{
    protected abstract TimeSpan Interval { get; }

    internal abstract Task RunOnceAsync(CancellationToken ct);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Unhandled error in {Job}", GetType().Name);
            }
        }
    }
}
