namespace Stackarr.DownloadManagement;

/// <summary>Why a torrent is accruing cleanup penalties.</summary>
public enum TorrentHealthReason
{
    /// <summary>Downloading below the configured speed threshold.</summary>
    Slow,

    /// <summary>Downloading with no peers and no speed (stalled).</summary>
    Stalled
}

/// <summary>
/// A live, read-only view of one torrent's penalty state, used by the UI to render the health
/// badge and countdown. Penalty state is in-memory and resets on restart.
/// </summary>
public sealed record TorrentPenalty(
    string Hash,
    string Name,
    TorrentHealthReason Reason,
    double PenaltyMinutes,
    double LimitMinutes,
    bool ZeroProgress,
    DateTime LastUpdatedAt)
{
    /// <summary>How far the torrent is toward its cleanup limit (0–1+).</summary>
    public double Fraction => LimitMinutes <= 0 ? 1 : PenaltyMinutes / LimitMinutes;
}

/// <summary>
/// Live penalty tracking for the download manager. The health detector accrues penalties and the
/// UI reads them here; admins can revoke a penalty to reset the countdown.
/// </summary>
public interface ITorrentPenaltyService
{
    /// <summary>Snapshot of every torrent currently carrying penalties.</summary>
    IReadOnlyList<TorrentPenalty> GetPenalties();

    /// <summary>
    /// Reset a torrent's accumulated penalty and restart its countdown, recording an audit event.
    /// Returns false when the torrent has no active penalty.
    /// </summary>
    Task<bool> Revoke(string hash, string? actor, CancellationToken ct = default);
}
