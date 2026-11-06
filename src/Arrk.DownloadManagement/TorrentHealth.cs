using Arrk.Core.Models;

namespace Arrk.DownloadManagement;

/// <summary>
/// Single definition of the torrent health predicates shared by the health detector and the
/// download event monitor, so "slow" and "stalled" never drift between them.
/// </summary>
public static class TorrentHealth
{
    /// <summary>
    /// A torrent that is actively downloading and not finished. Only these torrents are health
    /// checked: finished torrents are seeding candidates, errored / missing-file torrents are
    /// handled by error cleanup, and paused / queued / unknown torrents are not transferring.
    /// </summary>
    public static bool IsIncompleteCandidate(TorrentItem torrent) =>
        torrent.Progress < 1.0 && torrent.State == TorrentState.Downloading;

    /// <summary>
    /// A downloading torrent with no connected peers and no speed. Stalled takes precedence over
    /// slow: a stalled torrent is also below the slow threshold, but is counted only once.
    /// </summary>
    public static bool IsStalled(TorrentItem torrent) =>
        torrent.Progress < 1.0
        && torrent.State == TorrentState.Downloading
        && torrent.DownloadSpeed <= 0
        && torrent.Peers <= 0
        && torrent.Seeds <= 0;

    /// <summary>Progress at or below which a torrent is treated as having made no real progress.</summary>
    public const double ZeroProgressThreshold = 0.05;

    /// <summary>
    /// A torrent that has barely started (at or below <see cref="ZeroProgressThreshold"/>). This is
    /// the "fake seeder" case: the indexer advertised peers/seeds that never delivered, so it is
    /// penalised faster than a torrent that is merely slow or stalled after starting.
    /// </summary>
    public static bool IsZeroProgress(TorrentItem torrent) => torrent.Progress <= ZeroProgressThreshold;

    /// <summary>
    /// Classify a candidate for penalty accrual, or <see langword="null"/> when it is healthy or
    /// not downloading. Stalled detection wins when enabled; a stalled torrent falls through to
    /// the slow rule when stalled detection is disabled, so a stuck torrent is still cleaned up.
    /// </summary>
    public static TorrentHealthReason? Classify(
        TorrentItem torrent,
        double slowThresholdBytes,
        bool slowEnabled,
        bool stalledEnabled)
    {
        if (!IsIncompleteCandidate(torrent))
            return null;

        if (stalledEnabled && IsStalled(torrent))
            return TorrentHealthReason.Stalled;

        if (slowEnabled && torrent.DownloadSpeed <= slowThresholdBytes)
            return TorrentHealthReason.Slow;

        return null;
    }
}
