using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Stackarr.Core;
using Stackarr.Core.Events;
using Stackarr.Music.Jobs;
using Stackarr.Music.Models;

namespace Stackarr.Music.Services;

/// <summary>
/// Re-acquires Stackarr-grabbed music after its torrent is removed because it failed
/// (stalled/slow/errored). Music is grabbed through Prowlarr + Transmission rather than an arr, so
/// there is no queue to blocklist: instead the failed release is excluded and the next-best match is
/// grabbed. The album is resolved from the recorded grab (by infohash), so no per-album torrent
/// label is needed.
/// </summary>
public sealed class MusicFailureResolver : IHostedService, IDisposable
{
    readonly IBackgroundJobClient _backgroundJobs;
    readonly IDbContextFactory<StackarrDbContext> _dbFactory;
    readonly MusicLibrary _library;
    readonly MusicAcquisitionService _acquisition;
    readonly ILogger<MusicFailureResolver> _logger;
    readonly IDisposable _subscription;

    public MusicFailureResolver(
        IEventBus eventBus,
        IBackgroundJobClient backgroundJobs,
        IDbContextFactory<StackarrDbContext> dbFactory,
        MusicLibrary library,
        MusicAcquisitionService acquisition,
        ILogger<MusicFailureResolver> logger)
    {
        _backgroundJobs = backgroundJobs;
        _dbFactory = dbFactory;
        _library = library;
        _acquisition = acquisition;
        _logger = logger;
        _subscription = eventBus.Subscribe<TorrentDeletedEvent>(HandleTorrentDeletedAsync);
    }

    async ValueTask HandleTorrentDeletedAsync(TorrentDeletedEvent evt, CancellationToken ct)
    {
        // Only genuine download failures warrant re-acquiring the album. A torrent removed because
        // it reached its seeding goal (or to free storage) completed successfully; re-grabbing it
        // would just download the same release again.
        if (evt.Reason is not (TorrentDeleteReason.Slow or TorrentDeleteReason.Error or TorrentDeleteReason.Stalled))
            return;

        if (evt.Labels is null
            || !evt.Labels.Contains(MusicAcquisitionService.DownloadLabel, StringComparer.OrdinalIgnoreCase))
            return;

        int albumId = 0;
        try
        {
            await using StackarrDbContext db = await _dbFactory.CreateDbContextAsync(ct);
            string hash = (evt.TorrentHash ?? "").Trim().ToLowerInvariant();
            GrabRecord? record = await db.Set<GrabRecord>().AsNoTracking()
                .FirstOrDefaultAsync(g => g.Identifier == hash, ct);
            albumId = record?.AlbumId ?? 0;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not look up the album for failed torrent {Hash}", evt.TorrentHash);
        }

        if (albumId <= 0)
        {
            _logger.LogInformation(
                "Music torrent {Hash} ({Name}) was removed but no album was recorded; cannot re-acquire",
                evt.TorrentHash, evt.TorrentName);
            return;
        }

        _logger.LogInformation(
            "Music torrent {Hash} ({Name}) was {Reason}; re-queueing album {AlbumId}",
            evt.TorrentHash, evt.TorrentName, evt.Reason, albumId);

        // Blacklist the failed release so the queue's next search skips it, then re-join the queue
        // (the album's attempt count rises, pushing it behind healthier requests).
        _acquisition.BlacklistAlbumRelease(albumId, evt.TorrentName);
        await _library.RequeueAcquireAsync("", evt.TorrentName, albumId, deezerTrackId: 0, requireArtist: false, "torrent failed", ct);
        MusicAcquireQueueJob.Kick(_backgroundJobs);
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public void Dispose() => _subscription.Dispose();
}
