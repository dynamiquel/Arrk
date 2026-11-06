using Hangfire;
using Microsoft.Extensions.Logging;
using Arrk.Core.Modules;
using Arrk.Music.Services;

namespace Arrk.Music.Jobs;

/// <summary>
/// Background acquisition for one album or track. Adding an album or syncing a subscribed playlist
/// enqueues this instead of blocking on a slow indexer search, so the UI and the sync job stay
/// responsive while the grab happens in the background.
/// </summary>
public class MusicAcquireJob(MusicAcquisitionService acquisition, ILogger<MusicAcquireJob> logger)
{
    /// <summary>
    /// Runs grabs queued without a Deezer track id. Hangfire resolves a job by method name, so this
    /// must not be an overload of <see cref="ExecuteWithTrack"/>.
    /// </summary>
    [Queue(ArrkHangfireJobProvider.AcquisitionQueue)]
    [JobDisplayName("Music Acquire")]
    public Task Execute(string artist, string title, int? albumId, bool requireArtist, CancellationToken ct) =>
        AcquireAsync(artist, title, albumId, requireArtist, deezerTrackId: 0, ct);

    /// <summary>Acquires a requested track, carrying its Deezer id so the file binds to it exactly.</summary>
    [Queue(ArrkHangfireJobProvider.AcquisitionQueue)]
    [JobDisplayName("Music Acquire")]
    public Task ExecuteWithTrack(string artist, string title, int? albumId, bool requireArtist, long deezerTrackId, CancellationToken ct) =>
        AcquireAsync(artist, title, albumId, requireArtist, deezerTrackId, ct);

    // Long (multi-minute) searches run on their own queue so they never block the default queue.
    async Task AcquireAsync(string artist, string title, int? albumId, bool requireArtist, long deezerTrackId, CancellationToken ct)
    {
        try
        {
            MusicGrabResult result = await acquisition.SearchAndGrabAsync(artist, title, albumId, requireArtist, deezerTrackId, ct);
            if (result.Success)
                logger.LogInformation("Acquired \"{Title}\": {Message}", title, result.Message);
            else
                logger.LogDebug("No grab for \"{Title}\": {Message}", title, result.Message);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Background acquisition failed for {Title}", title);
        }
    }
}
