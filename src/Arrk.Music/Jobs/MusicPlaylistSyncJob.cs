using Hangfire;
using Microsoft.Extensions.Logging;
using Arrk.Music.Services;

namespace Arrk.Music.Jobs;

/// <summary>
/// Periodically refreshes every playlist subscription: pulls new tracks from the source, downloads
/// any missing songs (when auto-download is on) and re-projects the playlist into Navidrome.
/// </summary>
public class MusicPlaylistSyncJob(
    MusicSubscriptionService subscriptions,
    ILogger<MusicPlaylistSyncJob> logger)
{
    public const string DefaultCron = "0 0 */6 * * *";

    [JobDisplayName("Music Playlist Sync")]
    public async Task Execute(CancellationToken ct)
    {
        int count = await subscriptions.SyncAllAsync(ct);
        if (count > 0)
            logger.LogInformation("Refreshed {Count} music playlist subscription(s)", count);
    }
}
