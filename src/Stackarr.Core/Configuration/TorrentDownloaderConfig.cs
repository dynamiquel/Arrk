using System.ComponentModel;
using System.ComponentModel.DataAnnotations;

namespace Stackarr.Core;

public class TorrentDownloaderConfig
{
    [Description("Expose the download client through the reverse proxy at /transmission")]
    public bool Proxy { get; set; }

    [Description("Route all torrent traffic through the VPN (Gluetun) container. Provisioning the tunnel is implied by this flag.")]
    public bool Vpn { get; set; }

    [Description("Seeding limits configuration")]
    public SeedingConfig Seeding { get; set; } = new();

    [Description("Upload and download speed limits")]
    public SpeedConfig Speed { get; set; } = new();

    [Description("Maximum active download/seed limits")]
    public TorrentLimitsConfig Limits { get; set; } = new();

    [Description("Download management rules (slow detection, errors, storage)")]
    public DownloadManagementConfig Management { get; set; } = new();
}

public class SeedingConfig
{
    [Description("Stop seeding when ratio reaches this value")]
    public double MaxRatio { get; set; } = 2.0;

    [Description("Maximum seeding time in minutes")]
    public int MaxTimeMinutes { get; set; } = 1440;

    [Description("Minimum seeding time in minutes before ratio checks")]
    public int MinTimeMinutes { get; set; } = 30;
}

public class SpeedConfig
{
    [Description("Upload speed limit in MB/s (0 = unlimited)")]
    public int UploadMbps { get; set; } = 10;

    [Description("Download speed limit in MB/s (0 = unlimited)")]
    public int DownloadMbps { get; set; } = 0;
}

public class TorrentLimitsConfig
{
    [Description("Maximum concurrent active downloads")]
    public int MaxActiveDownloads { get; set; } = 5;

    [Description("Maximum concurrent active seed tasks")]
    public int MaxActiveSeeds { get; set; } = 10;

    [Description("Maximum concurrent active torrents (downloads + seeds)")]
    public int MaxActiveTorrents { get; set; } = 15;
}

public class DownloadManagementConfig
{
    [Description("Slow torrent detection and cleanup")]
    public SlowTorrentConfig Slow { get; set; } = new();

    [Description("Stalled torrent detection (no peers, zero speed) and cleanup")]
    public StalledTorrentConfig Stalled { get; set; } = new();

    [Description("How many penalty minutes a minute of zero progress counts for. 4 means a torrent stuck at 0% (fake seeders / a lying indexer) reaches the limit four times faster.")]
    public double ZeroProgressMultiplier { get; set; } = 4.0;

    [Description("Penalty minutes removed per minute a torrent is healthy again (0.5 removes 1 minute every 2 minutes)")]
    public double RecoveryPerMinute { get; set; } = 0.5;

    [Description("Error torrent cleanup rules")]
    public ErrorTorrentConfig Errors { get; set; } = new();

    [Description("Storage-based cleanup rules")]
    public StorageCleanupConfig Storage { get; set; } = new();
}

public class SlowTorrentConfig
{
    [Description("Enable slow torrent detection")]
    public bool Enabled { get; set; } = true;

    [Description("Speed threshold in MB/s below which torrent is considered slow")]
    public double ThresholdMbps { get; set; } = 1.0;

    [Description("Penalty minutes of slow speed before the torrent is removed")]
    public int LimitMinutes { get; set; } = 30;
}

public class StalledTorrentConfig
{
    [Description("Enable stalled torrent detection")]
    public bool Enabled { get; set; } = true;

    [Description("Penalty minutes of being stalled with no peers before the torrent is removed. A stall at 0% accrues faster (see management.zeroProgressMultiplier).")]
    public int LimitMinutes { get; set; } = 20;
}

public class ErrorTorrentConfig
{
    [Description("Enable automatic error torrent cleanup")]
    public bool Enabled { get; set; } = true;

    [Description("Minutes after which errored torrents are removed")]
    public int DeleteAfterMinutes { get; set; } = 30;
}

public class StorageCleanupConfig
{
    [Description("Enable storage-based cleanup")]
    public bool Enabled { get; set; } = true;

    [Description("Pause new downloads when disk usage exceeds this percentage (0-100)")]
    public int PauseDownloadsAtPercent { get; set; } = 90;

    [Description("Start cleaning seeding torrents when disk usage exceeds this percentage (0-100)")]
    public int CleanupSeedingAtPercent { get; set; } = 85;

    [Description("Cleanup order: oldest, newest, or least_ratio")]
    [AllowedValues("oldest", "newest", "least_ratio")]
    public string CleanupStrategy { get; set; } = "oldest";

    [Description("Minimum seed time in minutes before cleanup eligibility")]
    public int MinSeedTimeMinutes { get; set; } = 30;
}
