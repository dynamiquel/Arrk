using Stackarr.Core.Events;

namespace Stackarr.Tvarr.Models;

public class PendingTorrentCleanup
{
    public long Id { get; set; }
    public string TorrentHash { get; set; } = "";
    public string TorrentName { get; set; } = "";
    public TorrentDeleteReason Reason { get; set; }
    public string TargetService { get; set; } = "";
    public int AttemptCount { get; set; }
    public string? LastError { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
}