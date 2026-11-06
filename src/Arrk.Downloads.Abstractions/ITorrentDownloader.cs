
namespace Arrk.Downloads.Abstractions;

public enum TorrentFileCleanup
{
    KeepFiles,
    DeleteFiles
}

/// <summary>Read-only torrent state. A UI or plugin that only lists downloads needs this.</summary>
public interface ITorrentReader
{
    Task<TorrentItem?> GetTorrent(string id, CancellationToken ct = default);

    Task<List<TorrentItem>> GetTorrents(string filter = "all", CancellationToken ct = default);
}

/// <summary>Torrent mutations. A client that cannot pause/resume simply does not implement this.</summary>
public interface ITorrentCommands
{
    Task<string> AddTorrent(byte[] torrentData, string category = "", string savePath = "", CancellationToken ct = default);

    Task<string> AddTorrent(Uri torrentUri, string category = "", string savePath = "", CancellationToken ct = default);

    Task DeleteTorrent(string id, TorrentFileCleanup fileCleanup = TorrentFileCleanup.DeleteFiles, CancellationToken ct = default);

    Task PauseTorrent(string id, CancellationToken ct = default);

    Task ResumeTorrent(string id, CancellationToken ct = default);

    Task PauseTorrents(string filter = "all", CancellationToken ct = default);

    Task ResumeTorrents(string filter = "all", CancellationToken ct = default);
}

/// <summary>Session-wide transfer counters.</summary>
public interface ITorrentStats
{
    Task<TorrentTransferStats> GetTransferStats(CancellationToken ct = default);
}

/// <summary>
/// Full torrent client. Consumers that only need one capability should depend on
/// <see cref="ITorrentReader"/>, <see cref="ITorrentCommands"/> or <see cref="ITorrentStats"/>.
/// </summary>
public interface ITorrentDownloader : ITorrentReader, ITorrentCommands, ITorrentStats
{
}
