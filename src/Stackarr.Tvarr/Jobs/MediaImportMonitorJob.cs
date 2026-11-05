using Hangfire;
using Microsoft.Extensions.DependencyInjection;
using Stackarr.Tvarr.Services;

namespace Stackarr.Tvarr.Jobs;

/// <summary>
/// Polls the arrs' history for completed imports so a movie/episode landing in the library is
/// announced as a <c>downloads.imported</c> notification.
/// </summary>
public class MediaImportMonitorJob(IServiceScopeFactory scopeFactory)
{
    public const string DefaultCron = "* * * * *";

    [JobDisplayName("Media Import Monitor")]
    public async Task Execute(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        MediaImportMonitor monitor = scope.ServiceProvider.GetRequiredService<MediaImportMonitor>();
        await monitor.PollAsync(ct);
    }
}
