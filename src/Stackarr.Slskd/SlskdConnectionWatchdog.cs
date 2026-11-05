namespace Stackarr.Slskd;

/// <summary>
/// Tracks how long slskd has been disconnected from Soulseek. slskd reconnects with a growing backoff
/// (1→2→4→…→64s), so a brief network blip can leave it out for many minutes; restarting the container
/// resets that backoff. This decides when a restart is warranted, without restart-looping.
/// </summary>
public sealed class SlskdConnectionWatchdog
{
    /// <summary>How long Soulseek may be unreachable before we restart slskd.</summary>
    public static readonly TimeSpan DisconnectThreshold = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Minimum gap between restarts. Kept short because a restart is the actual fix: slskd can get
    /// stuck in a Soulseek state that a fresh start clears, so we retry every couple of minutes until
    /// it takes.
    /// </summary>
    public static readonly TimeSpan RestartCooldown = TimeSpan.FromMinutes(2);

    readonly Lock _gate = new();
    DateTime? _disconnectedSince;
    DateTime _lastRestart = DateTime.MinValue;

    /// <summary>
    /// Whether slskd should be restarted now: it has been disconnected for at least
    /// <see cref="DisconnectThreshold"/> and has not been restarted within <see cref="RestartCooldown"/>.
    /// A healthy check clears the disconnected timer.
    /// </summary>
    public bool ShouldRestart(bool available, DateTime now)
    {
        lock (_gate)
        {
            if (available)
            {
                _disconnectedSince = null;
                return false;
            }

            _disconnectedSince ??= now;
            if (now - _disconnectedSince.Value < DisconnectThreshold)
                return false;

            return now - _lastRestart >= RestartCooldown;
        }
    }

    /// <summary>Records that a restart was issued; the next one waits a full threshold + cooldown.</summary>
    public void RecordRestart(DateTime now)
    {
        lock (_gate)
        {
            _lastRestart = now;
            _disconnectedSince = now;
        }
    }
}
