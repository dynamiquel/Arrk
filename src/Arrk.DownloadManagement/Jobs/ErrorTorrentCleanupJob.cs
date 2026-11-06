using Hangfire;
using Microsoft.Extensions.DependencyInjection;

namespace Arrk.DownloadManagement.Jobs;

public class ErrorTorrentCleanupJob(IServiceScopeFactory scopeFactory)
{
    public const string DefaultCron = "* * * * *";

    [JobDisplayName("Error Torrent Cleanup")]
    public async Task Execute(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var cleanupService = scope.ServiceProvider.GetRequiredService<ErrorTorrentCleanup>();
        await cleanupService.CleanupErroredTorrents(ct);
    }
}
