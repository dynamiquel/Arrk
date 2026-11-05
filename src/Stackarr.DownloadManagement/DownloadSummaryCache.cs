using Stackarr.Downloads.Abstractions;

namespace Stackarr.DownloadManagement;

/// <summary>One shared snapshot behind the torrent tiles and the Downloads page.</summary>
public sealed record DownloadSummary(
    TorrentTransferStats Transfer,
    int Downloading,
    int Seeding,
    int Total);

/// <summary>
/// Coalesces the torrent list and transfer-stats calls so several download tiles on one dashboard
/// do not multiply the Transmission requests. The cache is short because the tiles are live.
/// </summary>
public sealed class DownloadSummaryCache(ITorrentDownloader torrents, TimeProvider time)
{
    static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(2);

    readonly SemaphoreSlim _gate = new(1, 1);
    DownloadSummary? _cached;
    DateTimeOffset _expires;

    public async Task<DownloadSummary> GetAsync(CancellationToken ct = default)
    {
        DateTimeOffset now = time.GetUtcNow();
        if (_cached is not null && now < _expires)
            return _cached;

        await _gate.WaitAsync(ct);
        try
        {
            now = time.GetUtcNow();
            if (_cached is not null && now < _expires)
                return _cached;

            List<TorrentItem> items = await torrents.GetTorrents("all", ct);
            TorrentTransferStats transfer = await torrents.GetTransferStats(ct);
            _cached = new(
                transfer,
                items.Count(t => t.State == TorrentState.Downloading),
                items.Count(t => t.State == TorrentState.Seeding),
                items.Count);
            _expires = now + Lifetime;
            return _cached;
        }
        finally
        {
            _gate.Release();
        }
    }
}
