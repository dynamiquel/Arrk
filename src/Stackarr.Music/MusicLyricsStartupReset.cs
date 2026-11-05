using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Stackarr.Music.Lyrics;

namespace Stackarr.Music;

/// <summary>
/// Clears lyrics lookups that failed on our side at startup. A restart is a natural retry point after
/// fixing a full disk, a locked file or a bad mount, so those songs do not wait for the daily reset.
/// </summary>
public class MusicLyricsStartupReset(
    LyricsService lyrics,
    ILogger<MusicLyricsStartupReset> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(15), CancellationToken.None);
                await lyrics.ResetFailuresAsync();
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Startup lyrics failure reset failed");
            }
        }, CancellationToken.None);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
