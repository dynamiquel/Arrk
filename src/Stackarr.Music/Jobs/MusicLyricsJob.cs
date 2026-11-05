using Hangfire;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Stackarr.Core.Modules;
using Stackarr.Music.Lyrics;
using Stackarr.Navidrome;

namespace Stackarr.Music.Jobs;

/// <summary>
/// Sweeps the library for songs that have no lyrics yet and downloads them beside the audio files.
/// Lyrics are cosmetic, so this never runs on the import path, takes only a small batch per run (the
/// provider is free and rate-limited), and treats a genuine miss as final, only a failed lookup is
/// cleared and retried, by <see cref="MusicLyricsResetJob"/>.
/// </summary>
public class MusicLyricsJob(
    LyricsService lyrics,
    NavidromeService navidrome,
    IOptionsMonitor<MusicConfig> config,
    ILogger<MusicLyricsJob> logger)
{
    /// <summary>Every 30 minutes, steady progress that leaves the library readable sooner than a
    /// daily sweep, while the small batch keeps the provider happy.</summary>
    public const string DefaultCron = "0 */30 * * * *";

    [SingleFlight]
    [JobDisplayName("Music Lyrics")]
    public async Task Execute(CancellationToken ct)
    {
        int written = await lyrics.SweepAsync(ct);
        if (written <= 0 || !config.CurrentValue.Lyrics.RescanNavidrome)
            return;

        // Navidrome reads lyrics sidecars from disk, so it only sees them after a scan.
        try
        {
            await navidrome.StartScanAsync(ct);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not trigger a Navidrome scan after writing lyrics");
        }
    }
}

/// <summary>
/// Daily retry for lyrics lookups that failed for an infrastructure reason (the provider was
/// unreachable). A song that simply has no lyrics is <c>NotFound</c> and is never retried
/// automatically, retrying it would only waste the provider's goodwill on every run.
/// </summary>
public class MusicLyricsResetJob(LyricsService lyrics)
{
    /// <summary>Daily, 04:00 UTC.</summary>
    public const string DefaultCron = "0 0 4 * * *";

    [JobDisplayName("Music Lyrics Retry")]
    public Task Execute(CancellationToken ct) => lyrics.ResetFailuresAsync(ct);
}
