using System.Collections.Concurrent;

namespace Stackarr.Jellyfin.Playback;

/// <summary>
/// A live playback session owned by one Stackarr user. It exists so the stream proxy can
/// (a) enforce that a session is only used by the user who created it and (b) know which Jellyfin
/// play/transcode session to clean up when playback stops. Sessions are ephemeral;
/// no database, so the whole feature can be removed without touching Stackarr's schema.
/// </summary>
public sealed class PlaybackSession
{
    public required string SessionId { get; init; }
    public required string Username { get; init; }
    public required string ItemId { get; init; }
    public string? MediaSourceId { get; init; }
    public string? PlaySessionId { get; init; }
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
    public DateTime LastSeenAt { get; set; } = DateTime.UtcNow;
}

/// <summary>In-memory registry of live playback sessions with a bounded lifetime.</summary>
public sealed class PlaybackSessionStore
{
    /// <summary>Idle sessions are reaped after this long without a stream request or progress ping.</summary>
    public static readonly TimeSpan IdleTimeout = TimeSpan.FromHours(6);

    readonly ConcurrentDictionary<string, PlaybackSession> _sessions = new(StringComparer.Ordinal);

    public PlaybackSession Create(string username, string itemId, string? mediaSourceId, string? playSessionId)
    {
        PlaybackSession session = new()
        {
            SessionId = Guid.NewGuid().ToString("N"),
            Username = username,
            ItemId = itemId,
            MediaSourceId = mediaSourceId,
            PlaySessionId = playSessionId
        };
        _sessions[session.SessionId] = session;
        return session;
    }

    public PlaybackSession? Get(string sessionId)
    {
        if (_sessions.TryGetValue(sessionId, out PlaybackSession? session))
        {
            session.LastSeenAt = DateTime.UtcNow;
            return session;
        }

        return null;
    }

    /// <summary>Returns the session only when it belongs to <paramref name="username"/>.</summary>
    public PlaybackSession? GetOwned(string sessionId, string username) =>
        _sessions.TryGetValue(sessionId, out PlaybackSession? session)
        && string.Equals(session.Username, username, StringComparison.OrdinalIgnoreCase)
            ? session
            : null;

    public void Remove(string sessionId) => _sessions.TryRemove(sessionId, out _);

    /// <summary>Removes and returns sessions idle longer than <see cref="IdleTimeout"/>.</summary>
    public IReadOnlyList<PlaybackSession> ReapIdle()
    {
        DateTime cutoff = DateTime.UtcNow - IdleTimeout;
        List<PlaybackSession> expired = [];
        foreach ((string id, PlaybackSession session) in _sessions)
        {
            if (session.LastSeenAt < cutoff && _sessions.TryRemove(id, out PlaybackSession? removed))
                expired.Add(removed);
        }

        return expired;
    }
}
