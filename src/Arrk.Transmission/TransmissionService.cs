using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Arrk.Core;
using Arrk.Core.Events;
using Arrk.Core.Models;
using Arrk.Core.Services;
using TorrentState = Arrk.Downloads.Abstractions.TorrentState;

namespace Arrk.Transmission;

public class TransmissionService(
    TransmissionClient client,
    IOptionsMonitor<TorrentDownloaderConfig> config,
    ILogger<TransmissionService> logger)
    : ITorrentDownloader
{
    public event EventHandler<TorrentsChangedEventArgs>? TorrentsUpdated;
    public event EventHandler<TorrentStateChangedEventArgs>? TorrentStateChanged;

    public async Task<List<TorrentItem>> GetTorrents(string filter = "all", CancellationToken ct = default)
    {
        Dictionary<string, bool>? filterDict = filter.ToLowerInvariant() switch
        {
            "downloading" => new Dictionary<string, bool> { ["downloading"] = true },
            "seeding" => new Dictionary<string, bool> { ["seeding"] = true },
            "paused" => new Dictionary<string, bool> { ["paused"] = true },
            "error" => new Dictionary<string, bool> { ["error"] = true },
            "active" => new Dictionary<string, bool> { ["active"] = true },
            "queued" => new Dictionary<string, bool> { ["queued"] = true },
            _ => null
        };

        List<TransmissionTorrent> torrents = await client.GetTorrents(filter: filterDict, ct: ct);
        return torrents.Select(MapToTorrentItem).ToList();
    }

    public async Task<TorrentItem?> GetTorrent(string id, CancellationToken ct = default)
    {
        List<TorrentItem> torrents = await GetTorrents("all", ct);
        return torrents.FirstOrDefault(t => t.Hash.Equals(id, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<string> AddTorrent(
        byte[] torrentData,
        string category = "",
        string savePath = "",
        CancellationToken ct = default)
    {
        long id = await client.AddTorrent(torrentData, savePath, ct: ct);
        return id.ToString();
    }

    public async Task<string> AddTorrent(
        Uri torrentUri,
        string category = "",
        string savePath = "",
        CancellationToken ct = default)
    {
        long id = await client.AddTorrentByUri(torrentUri, savePath, ct: ct);
        return id.ToString();
    }

    public async Task PauseTorrent(string id, CancellationToken ct = default)
    {
        await client.PauseTorrent(id, ct);
        logger.LogInformation("Paused torrent {Id}", id);
    }

    public async Task ResumeTorrent(string id, CancellationToken ct = default)
    {
        await client.ResumeTorrent(id, ct);
        logger.LogInformation("Resumed torrent {Id}", id);
    }

    public async Task DeleteTorrent(
        string id,
        TorrentFileCleanup fileCleanup = TorrentFileCleanup.DeleteFiles,
        CancellationToken ct = default)
    {
        await client.RemoveTorrent(id, fileCleanup, ct);
        logger.LogInformation("Deleted torrent {Id}, fileCleanup={DeleteFiles}", id, fileCleanup);
    }

    public async Task DeleteTorrents(
        IEnumerable<string> ids,
        TorrentFileCleanup fileCleanup = TorrentFileCleanup.DeleteFiles,
        CancellationToken ct = default)
    {
        string[] idArray = ids.Where(id => !string.IsNullOrWhiteSpace(id)).ToArray();
        if (idArray.Length == 0)
            return;
        await client.RemoveTorrents(idArray, fileCleanup, ct);
        logger.LogInformation(
            "Deleted {Count} torrents, fileCleanup={DeleteFiles}",
            idArray.Length, fileCleanup);
    }

    public async Task PauseTorrents(string filter = "all", CancellationToken ct = default)
    {
        await client.PauseAllTorrents(ct);
        logger.LogInformation("Paused all torrents");
    }

    public async Task ResumeTorrents(string filter = "all", CancellationToken ct = default)
    {
        await client.ResumeAllTorrents(ct);
        logger.LogInformation("Resumed all torrents");
    }

    public async Task<TorrentTransferStats> GetTransferStats(CancellationToken ct = default)
    {
        TransmissionSessionStats stats = await client.GetSessionStats(ct);
        return new()
        {
            DownloadSpeed = stats.DownloadSpeed,
            UploadSpeed = stats.UploadSpeed,
            TotalDownloaded = stats.CumulativeStats?.DownloadedBytes ?? 0,
            TotalUploaded = stats.CumulativeStats?.UploadedBytes ?? 0
        };
    }

    public async Task ApplyConfiguredLimits(CancellationToken ct = default)
    {
        // Resolve the current config so live edits (speed limits, max active torrents) apply on the
        // next run of the limits job without an app restart.
        TorrentDownloaderConfig current = config.CurrentValue;
        int uploadLimit = current.Speed.UploadMbps > 0 ? (current.Speed.UploadMbps * 1024 / 8) : 0;
        int downloadLimit = current.Speed.DownloadMbps > 0 ? (current.Speed.DownloadMbps * 1024 / 8) : 0;

        await client.SetSessionLimits(
            uploadLimit: current.Speed.UploadMbps > 0 ? uploadLimit : 0,
            uploadLimitEnabled: current.Speed.UploadMbps > 0,
            downloadLimit: current.Speed.DownloadMbps > 0 ? downloadLimit : 0,
            downloadLimitEnabled: current.Speed.DownloadMbps > 0,
            maxActiveDownloads: current.Limits.MaxActiveDownloads,
            maxActiveSeeds: current.Limits.MaxActiveSeeds,
            ct: ct);

        logger.LogInformation("Applied Transmission limits: speed={SpeedDL}/{SpeedUP}MB/s, maxActiveDL={MaxActiveDLs}, maxActiveSeeds={MaxSeeds}",
            current.Speed.DownloadMbps, current.Speed.UploadMbps, current.Limits.MaxActiveDownloads, current.Limits.MaxActiveSeeds);
    }

    public void NotifyTorrentsUpdated(IReadOnlyList<TorrentItem> torrents)
    {
        TorrentsUpdated?.Invoke(this, new() { Torrents = torrents });
    }

    public void NotifyTorrentStateChanged(TorrentItem torrent)
    {
        TorrentStateChanged?.Invoke(this, new() { Torrent = torrent });
    }

    static TorrentItem MapToTorrentItem(TransmissionTorrent t)
    {
        // An error flag takes precedence over the status code: Transmission reports errors
        // alongside a normal status (e.g. status=downloading with error=tracker error).
        TorrentState state = t.Error != 0
            ? TorrentState.Error
            : t.Status switch
            {
                0 => TorrentState.Paused,        // Stopped
                1 => TorrentState.Queued,        // Queued to check files
                2 => TorrentState.Downloading,   // Checking files
                3 => TorrentState.Queued,        // Queued to download
                4 => TorrentState.Downloading,   // Downloading
                5 => TorrentState.Queued,        // Queued to seed
                6 => TorrentState.Seeding,       // Seeding
                _ => TorrentState.Unknown
            };

        DateTime added = DateTimeOffset.FromUnixTimeSeconds(t.AddedDate).DateTime;

        long speed = t.RateDownload;
        long remainingBytes = t.LeftUntilDone;
        int remainingSeconds = speed > 0 ? (int)(remainingBytes / speed) : 0;
        string remaining = remainingSeconds > 0
            ? TimeSpan.FromSeconds(remainingSeconds).ToString(@"hh\:mm\:ss")
            : "";

        return new()
        {
            Hash = t.HashString,
            Name = t.Name,
            Category = t.Labels?.FirstOrDefault() ?? "",
            Labels = t.Labels ?? [],
            Progress = t.PercentDone,
            DownloadSpeed = t.RateDownload,
            UploadSpeed = t.RateUpload,
            Ratio = t.UploadRatio,
            SecondsSeeding = t.SecondsSeeding,
            Seeds = t.Seeders,
            Peers = t.Leechers,
            State = state,
            AddedOn = added,
            Size = t.TotalSize,
            Downloaded = t.TotalSize - t.LeftUntilDone,
            RemainingTime = remaining,
        };
    }
}
