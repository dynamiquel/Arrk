using Hangfire;
using Microsoft.Extensions.DependencyInjection;

namespace Arrk.DownloadManagement.Jobs;

public class DownloadEventMonitorJob(IServiceScopeFactory scopeFactory)
{
    public const string DefaultCron = "* * * * *";

    [JobDisplayName("Download Event Monitor")]
    public async Task Execute(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var monitor = scope.ServiceProvider.GetRequiredService<DownloadEventMonitor>();
        await monitor.PollAsync(ct);
    }
}
