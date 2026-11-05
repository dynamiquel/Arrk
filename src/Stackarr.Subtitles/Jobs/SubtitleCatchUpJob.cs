using Hangfire;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Stackarr.Subtitles.Jobs;

/// <summary>
/// Safety net for imports that happened while Stackarr was down or while the event watcher was
/// busy. It only looks at recent library additions, so a full library backfill stays manual.
/// </summary>
public sealed class SubtitleCatchUpJob(
    ILogger<SubtitleCatchUpJob> logger,
    IOptionsMonitor<SubtitlesConfig> config,
    SubtitleAutoDownloader downloader)
{
    public const string DefaultCron = "0 */6 * * *";

    [JobDisplayName("Subtitle Catch-up")]
    public async Task Execute(CancellationToken ct)
    {
        if (!config.CurrentValue.Enabled || !config.CurrentValue.AutoDownload)
            return;

        try
        {
            await downloader.CatchUpAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Subtitle catch-up failed");
        }
    }
}
