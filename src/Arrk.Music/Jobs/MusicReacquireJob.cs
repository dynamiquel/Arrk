using Hangfire;
using Microsoft.Extensions.Logging;
using Arrk.Music.Services;

namespace Arrk.Music.Jobs;

/// <summary>
/// Re-acquires a music album after its Arrk-grabbed torrent failed (stalled/slow/errored).
/// Enqueued by <see cref="MusicFailureResolver"/> so the detector that removed the torrent is not
/// blocked on the Prowlarr search, and so a transient failure is retried by Hangfire.
/// </summary>
public class MusicReacquireJob(
    MusicAcquisitionService acquisition,
    ILogger<MusicReacquireJob> logger)
{
    [JobDisplayName("Music Re-acquire")]
    public async Task Execute(int albumId, string? failedReleaseName, CancellationToken ct)
    {
        MusicGrabResult result = await acquisition.ReacquireAlbumAsync(albumId, failedReleaseName, ct);
        if (result.Success)
            logger.LogInformation("Re-acquired album {AlbumId}: {Message}", albumId, result.Message);
        else
            logger.LogWarning("Could not re-acquire album {AlbumId}: {Message}", albumId, result.Message);
    }
}
