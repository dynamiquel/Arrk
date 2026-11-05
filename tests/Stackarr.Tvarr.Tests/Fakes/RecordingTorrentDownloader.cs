using Stackarr.Core.Models;
using Stackarr.Core.Services;

namespace Stackarr.Tvarr.Tests.Fakes;

/// <summary>Minimal <see cref="ITorrentDownloader"/> that records deletions for assertions.</summary>
public sealed class RecordingTorrentDownloader : ITorrentDownloader
{
    public List<TorrentItem> Torrents { get; } = [];
    public List<(string Id, TorrentFileCleanup Cleanup)> Deleted { get; } = [];

    public Task<List<TorrentItem>> GetTorrents(string filter = "all", CancellationToken ct = default)
        => Task.FromResult(Torrents.ToList());

    public Task DeleteTorrent(string id, TorrentFileCleanup fileCleanup = TorrentFileCleanup.DeleteFiles, CancellationToken ct = default)
    {
        Deleted.Add((id, fileCleanup));
        return Task.CompletedTask;
    }

    public Task<TorrentItem?> GetTorrent(string id, CancellationToken ct = default)
        => Task.FromResult(Torrents.FirstOrDefault(t => t.Hash.Equals(id, StringComparison.OrdinalIgnoreCase)));

    public Task<string> AddTorrent(byte[] torrentData, string category = "", string savePath = "", CancellationToken ct = default)
        => Task.FromResult("");

    public Task<string> AddTorrent(Uri torrentUri, string category = "", string savePath = "", CancellationToken ct = default)
        => Task.FromResult("");

    public Task PauseTorrent(string id, CancellationToken ct = default) => Task.CompletedTask;

    public Task ResumeTorrent(string id, CancellationToken ct = default) => Task.CompletedTask;

    public Task<TorrentTransferStats> GetTransferStats(CancellationToken ct = default)
        => Task.FromResult(new TorrentTransferStats());

    public Task PauseTorrents(string filter = "all", CancellationToken ct = default) => Task.CompletedTask;

    public Task ResumeTorrents(string filter = "all", CancellationToken ct = default) => Task.CompletedTask;
}
