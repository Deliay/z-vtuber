using Microsoft.Extensions.Hosting;

namespace Z.Vtuber.Cookie.Browser;

public class LoginSessionWorker : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // TODO: implement pending login session monitoring
        return Task.CompletedTask;
    }
}