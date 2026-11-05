using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Stackarr.Core;
using Stackarr.Tvarr.Models;
using Stackarr.Tvarr.Services;

namespace Stackarr.Tvarr.Services;

public class TorrentCleanupJob(
    IServiceScopeFactory scopeFactory,
    ILogger<TorrentCleanupJob> logger)
{
    public const int MaxAttempts = 5;

    [JobDisplayName("Failed Torrent Re-search")]
    public async Task Execute(CancellationToken ct = default)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StackarrDbContext>();
        var sonarr = scope.ServiceProvider.GetRequiredService<SonarrService>();
        var radarr = scope.ServiceProvider.GetRequiredService<RadarrService>();

        List<PendingTorrentCleanup> pending = await db.Set<PendingTorrentCleanup>()
            .Where(p => p.CompletedAt == null)
            .ToListAsync(ct);

        if (pending.Count == 0)
            return;

        logger.LogInformation("Processing {Count} pending torrent cleanups", pending.Count);

        foreach (PendingTorrentCleanup p in pending)
        {
            // Retries are bounded so one unparseable history record cannot pin the row forever.
            if (p.AttemptCount >= MaxAttempts)
            {
                p.CompletedAt = DateTime.UtcNow;
                p.LastError ??= $"Gave up after {MaxAttempts} attempts";
                logger.LogWarning(
                    "Giving up on re-search for torrent {Hash} on {Service} after {Attempts} attempts: {Error}",
                    p.TorrentHash, p.TargetService, p.AttemptCount, p.LastError);
                continue;
            }

            try
            {
                FailedDownloadResolution resolution;
                if (p.TargetService == "Sonarr")
                {
                    resolution = await sonarr.ResolveFailedDownload(p.TorrentHash, ct);
                }
                else if (p.TargetService == "Radarr")
                {
                    resolution = await radarr.ResolveFailedDownload(p.TorrentHash, ct);
                }
                else
                {
                    p.CompletedAt = DateTime.UtcNow;
                    continue;
                }

                p.CompletedAt = DateTime.UtcNow;
                p.LastError = null;
                logger.LogInformation(
                    "Resolved removed torrent {Hash} on {Service}: {Detail} (blocklisted={Blocklisted}, searched={Searched})",
                    p.TorrentHash, p.TargetService, resolution.Detail, resolution.Blocklisted, resolution.SearchTriggered);
            }
            catch (Exception ex)
            {
                p.AttemptCount++;
                p.LastError = ex.Message;
                logger.LogWarning(ex, "Failed cleanup for torrent {Hash} on {Service}, attempt {Attempt}",
                    p.TorrentHash, p.TargetService, p.AttemptCount);
            }
        }

        await db.SaveChangesAsync(ct);
    }
}
