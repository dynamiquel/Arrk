using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Stackarr.Core;
using Stackarr.Core.Events;
using Stackarr.Tvarr.Models;

namespace Stackarr.Tvarr.Services;

public class DownloadEventHandler : IHostedService, IDisposable
{
    static readonly string[] ArrServices = ["Sonarr", "Radarr"];

    readonly IServiceScopeFactory _scopeFactory;
    readonly IOptionsMonitor<TvConfig> _tvConfig;
    readonly ILogger<DownloadEventHandler> _logger;
    readonly IDisposable _subscription;

    public DownloadEventHandler(
        IEventBus eventBus,
        IServiceScopeFactory scopeFactory,
        IOptionsMonitor<TvConfig> tvConfig,
        ILogger<DownloadEventHandler> logger)
    {
        _scopeFactory = scopeFactory;
        _tvConfig = tvConfig;
        _logger = logger;

        _subscription = eventBus.Subscribe<TorrentDeletedEvent>(HandleTorrentDeletedAsync);
    }

    /// <summary>
    /// Which arr service a removed torrent belongs to, based on its category/labels. Stackarr-owned
    /// music grabs (labelled <c>stackarr-*</c>) are excluded here: they never go through an arr, so
    /// the music resolver re-acquires them instead. An unknown category is tried against both arrs.
    /// </summary>
    static IEnumerable<string> ResolveTargets(string category, IReadOnlyList<string>? labels)
    {
        if (labels is not null
            && labels.Any(l => l.StartsWith("stackarr-", StringComparison.OrdinalIgnoreCase)))
        {
            return [];
        }

        return category.ToLowerInvariant() switch
        {
            "tv" => ["Sonarr"],
            "movies" => ["Radarr"],
            _ => ArrServices
        };
    }

    async ValueTask HandleTorrentDeletedAsync(TorrentDeletedEvent evt, CancellationToken ct)
    {
        _logger.LogInformation("Received torrent deleted event: {Hash} ({Name}) - Reason: {Reason}, Category: {Category}",
            evt.TorrentHash, evt.TorrentName, evt.Reason, evt.Category);

        // Slow/stalled cleanup always retries: the release was removed for being unhealthy and
        // another one may do better. Errored torrents honour the operator switch; the arrs' own
        // failed-download handling is synced to the same value (FailedDownloadHandlingSyncJob).
        if (evt.Reason == TorrentDeleteReason.Error
            && !_tvConfig.CurrentValue.FailedDownloads.RetriggerSearch)
        {
            _logger.LogInformation(
                "Re-trigger search is disabled; leaving errored torrent {Hash} unresolved", evt.TorrentHash);
            return;
        }

        List<string> targets = [.. ResolveTargets(evt.Category, evt.Labels)];
        if (targets.Count == 0)
        {
            _logger.LogDebug("Torrent {Hash} is not an arr download; skipping arr cleanup", evt.TorrentHash);
            return;
        }

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StackarrDbContext>();

        // A torrent can be deleted by more than one detector (slow/error/storage) and the
        // event may fire again before the arr queue is cleaned up. Reuse any existing row
        // for this hash/service instead of inserting a duplicate (which the unique index rejects).
        List<PendingTorrentCleanup> existing = await db.Set<PendingTorrentCleanup>()
            .Where(p => p.TorrentHash == evt.TorrentHash)
            .ToListAsync(ct);

        int pending = 0;
        int requeued = 0;

        foreach (string targetService in targets)
        {
            PendingTorrentCleanup? row = existing.FirstOrDefault(p => p.TargetService == targetService);

            if (row is null)
            {
                db.Set<PendingTorrentCleanup>().Add(new PendingTorrentCleanup
                {
                    TorrentHash = evt.TorrentHash,
                    TorrentName = evt.TorrentName,
                    Reason = evt.Reason,
                    TargetService = targetService,
                    AttemptCount = 0,
                    CreatedAt = DateTime.UtcNow
                });
                pending++;
            }
            else if (row.CompletedAt is not null)
            {
                // Already cleaned up once; re-arm it for this new deletion.
                row.CompletedAt = null;
                row.AttemptCount = 0;
                row.LastError = null;
                row.Reason = evt.Reason;
                row.TorrentName = evt.TorrentName;
                requeued++;
            }
            // else: already pending, leave as-is.
        }

        if (pending == 0 && requeued == 0)
        {
            _logger.LogInformation(
                "Pending cleanup already exists for torrent {Hash}; skipping", evt.TorrentHash);
            return;
        }

        await db.SaveChangesAsync(ct);
        _logger.LogInformation(
            "Queued cleanup for torrent {Hash}: {Created} new, {Requeued} re-queued",
            evt.TorrentHash, pending, requeued);
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public void Dispose()
    {
        _subscription.Dispose();
    }
}
