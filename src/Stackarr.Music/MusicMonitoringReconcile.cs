using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Stackarr.Music.Services;

namespace Stackarr.Music;

/// <summary>
/// Reconciles albums that entered the library because one of their songs was grabbed, either by a
/// playlist auto-download or a single-song grab. Such albums are unmonitored with only the grabbed
/// song wanted, so the remaining tracks read as Unmonitored rather than Missing. Runs once shortly
/// after startup, is idempotent, and never undoes an explicit Monitor (which records a
/// LibraryIntent).
/// </summary>
public class MusicMonitoringReconcile(
    MusicLibrary library,
    ILogger<MusicMonitoringReconcile> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(25), CancellationToken.None);
                int reconciled = await library.ReconcileImplicitAlbumMonitoringAsync();
                if (reconciled > 0)
                    logger.LogInformation("Reconciled {Count} implicit music album(s) to unmonitored", reconciled);
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Startup music monitoring reconcile failed");
            }
        }, CancellationToken.None);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
