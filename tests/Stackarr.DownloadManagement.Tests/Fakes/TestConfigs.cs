using Stackarr.Core;
using Stackarr.Core.Models;

namespace Stackarr.DownloadManagement.Tests;

public static class TestConfigs
{
    public static TorrentDownloaderConfig Slow(
        bool enabled = true,
        double thresholdMbps = 1.0,
        int limitMinutes = 30,
        double zeroProgressMultiplier = 4.0,
        double recoveryPerMinute = 0.5) => new()
    {
        Management = new DownloadManagementConfig
        {
            Slow = new SlowTorrentConfig
            {
                Enabled = enabled,
                ThresholdMbps = thresholdMbps,
                LimitMinutes = limitMinutes
            },
            Stalled = new StalledTorrentConfig { Enabled = false },
            ZeroProgressMultiplier = zeroProgressMultiplier,
            RecoveryPerMinute = recoveryPerMinute
        }
    };

    public static TorrentDownloaderConfig Stalled(
        bool enabled = true,
        int limitMinutes = 20,
        double zeroProgressMultiplier = 4.0,
        double recoveryPerMinute = 0.5,
        SlowTorrentConfig? slow = null,
        double thresholdMbps = 1.0) => new()
    {
        Management = new DownloadManagementConfig
        {
            Slow = slow ?? new SlowTorrentConfig { Enabled = false, ThresholdMbps = thresholdMbps },
            Stalled = new StalledTorrentConfig
            {
                Enabled = enabled,
                LimitMinutes = limitMinutes
            },
            ZeroProgressMultiplier = zeroProgressMultiplier,
            RecoveryPerMinute = recoveryPerMinute
        }
    };

    public static TorrentDownloaderConfig Health(
        bool slowEnabled = true,
        double thresholdMbps = 1.0,
        int slowLimitMinutes = 30,
        bool stalledEnabled = true,
        int stalledLimitMinutes = 20,
        double zeroProgressMultiplier = 4.0,
        double recoveryPerMinute = 0.5) => new()
    {
        Management = new DownloadManagementConfig
        {
            Slow = new SlowTorrentConfig { Enabled = slowEnabled, ThresholdMbps = thresholdMbps, LimitMinutes = slowLimitMinutes },
            Stalled = new StalledTorrentConfig { Enabled = stalledEnabled, LimitMinutes = stalledLimitMinutes },
            ZeroProgressMultiplier = zeroProgressMultiplier,
            RecoveryPerMinute = recoveryPerMinute
        }
    };

    public static TorrentDownloaderConfig Errors(bool enabled = true, int deleteAfterMinutes = 30) => new()
    {
        Management = new DownloadManagementConfig
        {
            Errors = new ErrorTorrentConfig
            {
                Enabled = enabled,
                DeleteAfterMinutes = deleteAfterMinutes
            }
        }
    };

    public static TorrentDownloaderConfig Storage(
        bool enabled = true,
        int pauseDownloadsAtPercent = 90,
        int cleanupSeedingAtPercent = 85,
        string cleanupStrategy = "oldest",
        int minSeedTimeMinutes = 30) => new()
    {
        Management = new DownloadManagementConfig
        {
            Storage = new StorageCleanupConfig
            {
                Enabled = enabled,
                PauseDownloadsAtPercent = pauseDownloadsAtPercent,
                CleanupSeedingAtPercent = cleanupSeedingAtPercent,
                CleanupStrategy = cleanupStrategy,
                MinSeedTimeMinutes = minSeedTimeMinutes
            }
        }
    };

    public static TorrentDownloaderConfig Seeding(
        double maxRatio = 2.0,
        int maxTimeMinutes = 1440,
        int minTimeMinutes = 30) => new()
    {
        Seeding = new SeedingConfig
        {
            MaxRatio = maxRatio,
            MaxTimeMinutes = maxTimeMinutes,
            MinTimeMinutes = minTimeMinutes
        }
    };

    public static TorrentItem Torrent(
        string hash,
        TorrentState state = TorrentState.Downloading,
        double progress = 0.5,
        double downloadSpeed = 0,
        double ratio = 1.0,
        DateTime? addedOn = null,
        int peers = 1,
        int seeds = 0,
        long secondsSeeding = 0) => new()
    {
        Hash = hash,
        Name = hash,
        State = state,
        Progress = progress,
        DownloadSpeed = downloadSpeed,
        Ratio = ratio,
        Peers = peers,
        Seeds = seeds,
        SecondsSeeding = secondsSeeding,
        AddedOn = addedOn ?? DateTime.UtcNow
    };
}
