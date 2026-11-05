using Stackarr.Core.Models;
using Stackarr.Core.Services;

namespace Stackarr.DownloadManagement.Tests;

/// <summary>
/// Configurable <see cref="ITorrentDownloader"/> that records every mutation and can be
/// driven to throw, delay, or return different torrents per filter.
/// </summary>
public sealed class FakeTorrentDownloader : ITorrentDownloader
{
    readonly object _lock = new();
    readonly List<(string Id, TorrentFileCleanup Cleanup)> _deleted = [];
    readonly List<string> _paused = [];
    readonly List<string> _resumed = [];
    int _pauseAll;
    int _resumeAll;

    public Dictionary<string, List<TorrentItem>> TorrentsByFilter { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Func<string, List<TorrentItem>>? OnGetTorrents { get; set; }
    public Exception? GetTorrentsException { get; set; }
    public TimeSpan GetTorrentsDelay { get; set; }
    public List<string> FiltersRequested { get; } = [];

    public IReadOnlyList<(string Id, TorrentFileCleanup Cleanup)> Deleted
    {
        get { lock (_lock) return _deleted.ToList(); }
    }

    public IReadOnlyList<string> Paused
    {
        get { lock (_lock) return _paused.ToList(); }
    }

    public IReadOnlyList<string> Resumed
    {
        get { lock (_lock) return _resumed.ToList(); }
    }

    public int PauseAllCount => _pauseAll;
    public int ResumeAllCount => _resumeAll;

    public async Task<List<TorrentItem>> GetTorrents(string filter = "all", CancellationToken ct = default)
    {
        lock (_lock)
            FiltersRequested.Add(filter);

        if (GetTorrentsDelay > TimeSpan.Zero)
            await Task.Delay(GetTorrentsDelay, ct);

        if (GetTorrentsException is not null)
            throw GetTorrentsException;

        if (OnGetTorrents is not null)
            return OnGetTorrents(filter);

        return TorrentsByFilter.TryGetValue(filter, out List<TorrentItem>? list) ? list : [];
    }

    public Task DeleteTorrent(string id, TorrentFileCleanup fileCleanup = TorrentFileCleanup.DeleteFiles, CancellationToken ct = default)
    {
        lock (_lock)
            _deleted.Add((id, fileCleanup));
        return Task.CompletedTask;
    }

    public Task DeleteTorrents(IEnumerable<string> ids, TorrentFileCleanup fileCleanup = TorrentFileCleanup.DeleteFiles, CancellationToken ct = default)
    {
        lock (_lock)
        {
            foreach (string id in ids)
                _deleted.Add((id, fileCleanup));
        }
        return Task.CompletedTask;
    }

    public Task PauseTorrent(string id, CancellationToken ct = default)
    {
        lock (_lock)
            _paused.Add(id);
        return Task.CompletedTask;
    }

    public Task ResumeTorrent(string id, CancellationToken ct = default)
    {
        lock (_lock)
            _resumed.Add(id);
        return Task.CompletedTask;
    }

    public Task PauseTorrents(string filter = "all", CancellationToken ct = default)
    {
        Interlocked.Increment(ref _pauseAll);
        return Task.CompletedTask;
    }

    public Task ResumeTorrents(string filter = "all", CancellationToken ct = default)
    {
        Interlocked.Increment(ref _resumeAll);
        return Task.CompletedTask;
    }

    // Not used by download-management services, but required by the interface.
    public Task<TorrentItem?> GetTorrent(string id, CancellationToken ct = default)
        => Task.FromResult<TorrentItem?>(null);

    public Task<string> AddTorrent(byte[] torrentData, string category = "", string savePath = "", CancellationToken ct = default)
        => Task.FromResult("");

    public Task<string> AddTorrent(Uri torrentUri, string category = "", string savePath = "", CancellationToken ct = default)
        => Task.FromResult("");

    public Task<TorrentTransferStats> GetTransferStats(CancellationToken ct = default)
        => Task.FromResult(new TorrentTransferStats());
}
