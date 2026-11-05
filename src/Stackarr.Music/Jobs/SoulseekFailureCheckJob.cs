using Hangfire;
using Microsoft.Extensions.Logging;
using Stackarr.Music.Services;

namespace Stackarr.Music.Jobs;

/// <summary>
/// Watches Stackarr's Soulseek (slskd) grabs for failures and stalls (errored transfers, or transfers
/// that stay queued with no progress). A bad source is blacklisted for the album and the album is
/// re-acquired from another source, the Soulseek counterpart of the torrent failure resolver.
/// </summary>
public class SoulseekFailureCheckJob(
    MusicAcquisitionService acquisition,
    ILogger<SoulseekFailureCheckJob> logger)
{
    public const string DefaultCron = "*/5 * * * *";

    [JobDisplayName("Soulseek Failure Check")]
    public async Task Execute(CancellationToken ct)
    {
        int requeued = await acquisition.CheckSoulseekFailuresAsync(ct);
        if (requeued > 0)
            logger.LogInformation("Re-queued {Count} music album(s) after a failed Soulseek download", requeued);
    }
}
