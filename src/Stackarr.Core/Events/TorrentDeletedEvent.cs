namespace Stackarr.Core.Events;

public record TorrentDeletedEvent(
    string TorrentHash,
    string TorrentName,
    TorrentDeleteReason Reason,
    DateTime DeletedAt,
    string Category = "",
    IReadOnlyList<string>? Labels = null
);

public enum TorrentDeleteReason
{
    Slow,
    Error,
    Storage,
    Stalled,
    SeedingCompleted
}