using Hangfire;
using Arrk.Core.Services;

namespace Arrk.Transmission;

public class TransmissionLimitsJob(
    ServiceRegistry serviceRegistry,
    ITorrentDownloader torrentDownloader)
{
    public const string JobName = "transmission-limits";
    public const string DefaultCron = "* * * * *";

    [JobDisplayName("Transmission Limits")]
    public async Task Execute(CancellationToken ct)
    {
        Service? torrentService = serviceRegistry.Get("torrent_downloader");
        if (torrentService?.State.IsHealthy != true)
        {
            return;
        }

        if (torrentDownloader is TransmissionService transmission)
        {
            await transmission.ApplyConfiguredLimits(ct);
        }
    }
}
