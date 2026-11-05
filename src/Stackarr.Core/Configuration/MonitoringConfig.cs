using System.ComponentModel;
using System.ComponentModel.DataAnnotations;

namespace Stackarr.Core.Configuration;

public class MonitoringConfig
{
    [Description("Internet speed test schedule")]
    public SpeedtestConfig Speedtest { get; set; } = new();

    [Description("Disk usage monitoring")]
    public StorageMonitoringConfig Storage { get; set; } = new();

    [Description("Movie and TV staleness monitoring")]
    public ServiceMonitoringConfig Tv { get; set; } = new();

    [Description("Music staleness monitoring")]
    public ServiceMonitoringConfig Music { get; set; } = new();
}

/// <summary>Monitoring settings for one media service (TV or music).</summary>
public class ServiceMonitoringConfig
{
    [Description("Staleness thresholds and cleanup")]
    public StalenessConfig Stale { get; set; } = new();
}

/// <summary>
/// Per-service staleness thresholds. An item is stale when it has not been consumed (watched or
/// listened to) for the upper threshold. The lower threshold sends an early warning; 0 disables it.
/// Items kept in a watchlist or playlist use the longer keep thresholds.
/// </summary>
public class StalenessConfig
{
    [Description("Days without consumption before an item is considered stale")]
    [Range(0.0, 36500.0)]
    public double UpperThreshold { get; set; } = 30;

    [Description("Days without consumption before an item is announced as about to become stale (0 disables the warning)")]
    [Range(0.0, 36500.0)]
    public double LowerThreshold { get; set; } = 0;

    [Description("Automatically delete stale items when the cleanup job runs")]
    public bool AutoCleanup { get; set; }

    [Description("Longer stale threshold in days for items kept in a watchlist or playlist")]
    [Range(0.0, 36500.0)]
    public double KeepThreshold { get; set; } = 45;

    [Description("Warning threshold in days for watchlist/playlist items (0 disables the warning)")]
    [Range(0.0, 36500.0)]
    public double KeepLowerThreshold { get; set; } = 0;
}

public class SpeedtestConfig
{
    [Description("Enable periodic speed tests")]
    public bool Enabled { get; set; } = true;

    [Description("Cron schedule for speed tests (e.g. */30 * * * *)")]
    public string Schedule { get; set; } = "*/30 * * * *";

    [Description("Alert when download speed drops below this many Mbps (0 disables)")]
    public double MinDownloadMbps { get; set; } = 0;

    [Description("Alert when upload speed drops below this many Mbps (0 disables)")]
    public double MinUploadMbps { get; set; } = 0;

    [Description("Minimum download speed in Mbps that counts as a healthy connection. Torrent slow/stalled cleanup is paused while the last speed test is below this (0 disables the check). About 8 Mbps is 1 MB/s.")]
    public double MinHealthyDownloadMbps { get; set; } = 8;
}

public class StorageMonitoringConfig
{
    [Description("Enable disk usage monitoring")]
    public bool Enabled { get; set; } = true;

    [Description("Warning threshold percentage")]
    public int WarnAtPercent { get; set; } = 80;

    [Description("Pause downloads when disk usage exceeds this percentage")]
    public int PauseDownloadsAtPercent { get; set; } = 90;
}
