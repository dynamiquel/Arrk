using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Arrk.Core.Events;
using Arrk.Core.Models;

namespace Arrk.DownloadManagement;

/// <summary>
/// In-memory penalty state for the download manager. Each check adds penalty minutes for an
/// unhealthy torrent (faster when it is still under 5%) and removes penalty minutes while it is
/// healthy; when the penalty reaches the configured limit the torrent is removed. The UI reads a
/// snapshot, and admins can revoke a penalty to restart the countdown.
/// </summary>
/// <remarks>
/// State is process-local: a restart clears penalties. Torrents are keyed by hash.
/// The penalty is shared across reasons, so a torrent that flips between slow and stalled keeps its
/// accumulated time instead of resetting, only the applicable limit changes.
/// </remarks>
public class TorrentPenaltyTracker(
    IEventStore eventStore,
    TimeProvider time,
    ILogger<TorrentPenaltyTracker> logger) : ITorrentPenaltyService
{
    sealed record Record(
        string Name,
        TorrentHealthReason Reason,
        double PenaltyMinutes,
        double LimitMinutes,
        bool ZeroProgress,
        DateTime LastUpdatedAt);

    readonly ConcurrentDictionary<string, Record> _penalties = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Add penalty minutes accumulated since the torrent was last evaluated. <paramref name="weightPerMinute"/>
    /// is 1 for a normal slow/stalled minute, or higher while the torrent is still under 5% (very likely
    /// fake seeders). <paramref name="limitMinutes"/> is the limit for the current reason.
    /// </summary>
    public PenaltyResult Accrue(
        TorrentItem torrent,
        TorrentHealthReason reason,
        bool zeroProgress,
        DateTime now,
        double weightPerMinute,
        double limitMinutes)
    {
        bool firstSighting = !_penalties.ContainsKey(torrent.Hash);

        Record record = _penalties.AddOrUpdate(
            torrent.Hash,
            _ => new Record(torrent.Name, reason, 0, limitMinutes, zeroProgress, now),
            (_, existing) =>
            {
                double elapsed = Math.Max(0, (now - existing.LastUpdatedAt).TotalMinutes);
                return existing with
                {
                    Name = torrent.Name,
                    Reason = reason,
                    PenaltyMinutes = existing.PenaltyMinutes + elapsed * weightPerMinute,
                    LimitMinutes = limitMinutes,
                    ZeroProgress = zeroProgress,
                    LastUpdatedAt = now
                };
            });

        return new PenaltyResult(
            firstSighting, record.Reason, record.PenaltyMinutes, record.LimitMinutes, record.ZeroProgress);
    }

    /// <summary>
    /// Remove penalty minutes for a torrent that is healthy again. Returns true once its record is
    /// dropped (penalty exhausted).
    /// </summary>
    public bool Decay(string hash, DateTime now, double recoveryPerMinute)
    {
        if (!_penalties.TryGetValue(hash, out Record? record))
            return false;

        double elapsed = Math.Max(0, (now - record.LastUpdatedAt).TotalMinutes);
        double remaining = record.PenaltyMinutes - elapsed * Math.Max(0, recoveryPerMinute);
        if (remaining <= 0)
            return _penalties.TryRemove(hash, out _);

        _penalties[hash] = record with { PenaltyMinutes = remaining, LastUpdatedAt = now };
        return false;
    }

    /// <summary>
    /// Advance a tracked torrent's clock without changing its penalty. Used while our own
    /// connection is unhealthy so the downtime is not later counted against the torrent.
    /// </summary>
    public void Hold(string hash, DateTime now)
    {
        if (_penalties.TryGetValue(hash, out Record? record))
            _penalties[hash] = record with { LastUpdatedAt = now };
    }

    /// <summary>Atomically claim a torrent for deletion so overlapping runs delete at most once.</summary>
    public bool TryClaimDeletion(string hash) => _penalties.TryRemove(hash, out _);

    /// <summary>Drop a torrent's penalty state. Returns true if it was tracked.</summary>
    public bool Clear(string hash) => _penalties.TryRemove(hash, out _);

    /// <summary>Drop state for every tracked torrent that is no longer a candidate.</summary>
    public void Prune(IEnumerable<string> activeHashes)
    {
        HashSet<string> active = new(activeHashes, StringComparer.OrdinalIgnoreCase);
        foreach (string key in _penalties.Keys)
        {
            if (!active.Contains(key))
                _penalties.TryRemove(key, out _);
        }
    }

    public IReadOnlyList<TorrentPenalty> GetPenalties() =>
        _penalties
            .Select(kv => new TorrentPenalty(
                kv.Key,
                kv.Value.Name,
                kv.Value.Reason,
                kv.Value.PenaltyMinutes,
                kv.Value.LimitMinutes,
                kv.Value.ZeroProgress,
                kv.Value.LastUpdatedAt))
            .OrderByDescending(p => p.Fraction)
            .ThenByDescending(p => p.PenaltyMinutes)
            .ToList();

    public async Task<bool> Revoke(string hash, string? actor, CancellationToken ct = default)
    {
        if (!_penalties.TryRemove(hash, out Record? removed))
            return false;

        DateTime now = time.GetUtcNow().UtcDateTime;
        string label = removed.Reason == TorrentHealthReason.Stalled ? "stalled" : "slow";
        string message = string.IsNullOrWhiteSpace(actor)
            ? $"Penalty revoked on {removed.Name} (was {removed.PenaltyMinutes:F0} min {label})"
            : $"{actor} revoked a {label} penalty on {removed.Name} (was {removed.PenaltyMinutes:F0} min)";

        logger.LogInformation(
            "Penalty revoked for torrent {Name} ({Hash}): was {Minutes:F0} min {Reason} by {Actor}",
            removed.Name, hash, removed.PenaltyMinutes, label, actor ?? "unknown");

        await eventStore.RecordAsync(new SystemEventRecord
        {
            Category = EventCategory.Download,
            EventType = DownloadEventType.PenaltyRevoked,
            Severity = EventSeverity.Info,
            Message = message,
            Source = hash,
            SourceName = removed.Name,
            Timestamp = now
        }, ct);

        return true;
    }
}

/// <summary>Result of accruing a penalty: the torrent's current penalty and whether it is new.</summary>
public readonly record struct PenaltyResult(
    bool FirstSighting,
    TorrentHealthReason Reason,
    double PenaltyMinutes,
    double LimitMinutes,
    bool ZeroProgress);
