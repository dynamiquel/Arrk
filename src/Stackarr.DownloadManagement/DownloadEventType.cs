namespace Stackarr.DownloadManagement;

/// <summary>
/// Subject-based download event subjects. Uses dotted, hierarchical names
/// (e.g. <c>torrent.added</c>) so subscriptions can use hierarchical wildcards later
/// (<c>torrent.*</c>, <c>torrent.#</c>, <c>#</c>).
/// </summary>
public static class DownloadEventType
{
    public const string Added = "torrent.added";
    public const string Progress = "torrent.progress";
    public const string StateChanged = "torrent.state";
    public const string Completed = "torrent.completed";
    public const string Paused = "torrent.paused";
    public const string Resumed = "torrent.resumed";
    public const string Stalled = "torrent.stalled";
    public const string Error = "torrent.error";
    public const string Struck = "torrent.struck";
    public const string Slow = "torrent.slow";
    public const string PenaltyRevoked = "torrent.penalty.revoked";
    public const string SeedingCompleted = "torrent.seeding.completed";
    public const string Removed = "torrent.removed";
}
