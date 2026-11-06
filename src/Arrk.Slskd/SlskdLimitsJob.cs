using Hangfire;
using Microsoft.Extensions.Options;
using Arrk.Core;

namespace Arrk.Slskd;

/// <summary>
/// Applies the same global speed limits the torrent client uses to slskd. Max concurrent transfers
/// (slots) are set through the container environment, which only takes effect on (re)start.
/// </summary>
public class SlskdLimitsJob(
    SlskdService slskd,
    IOptionsMonitor<TorrentDownloaderConfig> config)
{
    public const string JobName = "slskd-limits";
    public const string DefaultCron = "* * * * *";

    [JobDisplayName("Soulseek Limits")]
    public async Task Execute(CancellationToken ct)
    {
        if (!slskd.IsEnabled)
            return;

        TorrentDownloaderConfig cfg = config.CurrentValue;
        await slskd.ApplyConfiguredLimitsAsync(cfg.Speed.UploadMbps, cfg.Speed.DownloadMbps, ct);
    }
}
