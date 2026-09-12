using Microsoft.Extensions.Hosting;

namespace Z.Vtuber.Cookie.Refresh;

public class SessionRefreshJob : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // TODO: implement 10-minute refresh job
        return Task.CompletedTask;
    }
}