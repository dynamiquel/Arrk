using Hangfire;
using Microsoft.Extensions.Logging;
using Stackarr.Core.Modules;
using Stackarr.Music.Services;
using Stackarr.Navidrome;

namespace Stackarr.Music.Jobs;

/// <summary>
/// Finalises completed Stackarr-grabbed music downloads: places their audio into the music library
/// (hard-linked, where Navidrome indexes it) and tags it. Stackarr owns both the grab and the
/// placement, so partial releases and untagged rips still land correctly.
/// </summary>
public class MusicDownloadImportJob(
    MusicAcquisitionService acquisition,
    NavidromeService navidrome,
    IBackgroundJobClient backgroundJobs,
    ILogger<MusicDownloadImportJob> logger)
{
    public const string DefaultCron = "* * * * *";

    [SingleFlight]
    [JobDisplayName("Music Download Import")]
    public async Task Execute(CancellationToken ct)
    {
        int imported = await acquisition.ImportCompletedAsync(ct);
        if (imported <= 0)
            return;

        logger.LogInformation("Added {Count} completed music download(s) to the library", imported);

        // Playlist matching reads Navidrome, so newly placed files are invisible to it until Navidrome
        // has indexed them. Ask for a scan now instead of waiting for Navidrome's own schedule (which
        // otherwise made a freshly grabbed playlist take hours to fill in).
        try
        {
            await navidrome.StartScanAsync(ct);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not trigger a Navidrome scan after import");
        }

        // Newly placed music can satisfy subscribed playlists, so refresh them now, a short delay so
        // Navidrome has indexed the new files before we match against it (the scan is asynchronous).
        try
        {
            backgroundJobs.Schedule<MusicPlaylistSyncJob>(
                j => j.Execute(CancellationToken.None), TimeSpan.FromSeconds(20));
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not enqueue a playlist re-sync after import");
        }
    }
}
