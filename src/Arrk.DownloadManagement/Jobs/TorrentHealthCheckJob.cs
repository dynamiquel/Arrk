using Hangfire;
using Microsoft.Extensions.DependencyInjection;

namespace Arrk.DownloadManagement.Jobs;

public class TorrentHealthCheckJob(IServiceScopeFactory scopeFactory)
{
    /// <summary>Every minute: the penalty clock is measured in minutes.</summary>
    public const string DefaultCron = "* * * * *";

    [JobDisplayName("Torrent Slow/Stalled Detection")]
    public async Task Execute(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        TorrentHealthDetector detector = scope.ServiceProvider.GetRequiredService<TorrentHealthDetector>();
        await detector.CheckTorrents(ct);
    }
}
