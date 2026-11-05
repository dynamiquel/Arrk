namespace Stackarr.Core.Services;

/// <summary>Broad category of a live operation; drives the icon shown in the activity centre.</summary>
public enum LiveActivityKind
{
    /// <summary>Something is being searched for (an indexer, Soulseek, Prowlarr).</summary>
    Search,

    /// <summary>Completed downloads are being placed into the library.</summary>
    Import,

    /// <summary>A subscription/playlist is being refreshed.</summary>
    Sync,

    /// <summary>A managed container is being started, stopped, restarted or recreated.</summary>
    Container,

    /// <summary>A dependency is unreachable and Stackarr is waiting on it (e.g. Soulseek).</summary>
    Warning,

    /// <summary>A scheduled/enqueued background job is executing.</summary>
    Job,

    /// <summary>Anything that doesn't fit the categories above.</summary>
    Generic
}

/// <summary>One operation running right now, as published to the live activity registry.</summary>
public sealed record LiveActivityEntry(Guid Id, LiveActivityKind Kind, string Title, string? Detail);

/// <summary>
/// Process-wide registry of the operations that are running right now, searches, imports, playlist
/// syncs and container lifecycle actions. Modules publish here (they cannot reach the app's activity
/// centre directly) and the app mirrors it into the notification inbox as live activity.
///
/// Entries are transient: a scope removes its entry when it is disposed, so an operation that has
/// finished simply disappears instead of lingering as history. Long-lived conditions (a dependency
/// outage) hold their scope open until they clear.
/// </summary>
public sealed class LiveActivityRegistry
{
    readonly Lock _gate = new();
    readonly Dictionary<Guid, LiveActivityEntry> _entries = [];

    /// <summary>Raised whenever an entry is added, updated or removed.</summary>
    public event Action? Changed;

    /// <summary>Every operation currently in flight (unordered).</summary>
    public IReadOnlyList<LiveActivityEntry> Active
    {
        get
        {
            lock (_gate)
            {
                return [.. _entries.Values];
            }
        }
    }

    /// <summary>How many operations are in flight.</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _entries.Count;
            }
        }
    }

    /// <summary>Publishes a new live operation. Dispose (or <see cref="LiveActivityScope.Update"/> the
    /// returned scope) to keep it current; disposing it removes the entry.</summary>
    public LiveActivityScope Begin(LiveActivityKind kind, string title, string? detail = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);

        LiveActivityEntry entry = new(Guid.NewGuid(), kind, title, detail);
        lock (_gate)
        {
            _entries[entry.Id] = entry;
        }
        Notify();
        return new LiveActivityScope(this, entry.Id);
    }

    internal void Update(Guid id, string? detail)
    {
        bool changed;
        lock (_gate)
        {
            if (_entries.TryGetValue(id, out LiveActivityEntry? entry))
            {
                _entries[id] = entry with { Detail = detail };
                changed = true;
            }
            else
            {
                changed = false;
            }
        }

        if (changed)
            Notify();
    }

    internal void Remove(Guid id)
    {
        bool removed;
        lock (_gate)
        {
            removed = _entries.Remove(id);
        }

        if (removed)
            Notify();
    }

    void Notify() => Changed?.Invoke();
}

/// <summary>
/// Handle to a published <see cref="LiveActivityEntry"/>. Keeps the entry's detail current while the
/// operation runs and removes it when the scope is disposed.
/// </summary>
public sealed class LiveActivityScope(LiveActivityRegistry owner, Guid id) : IDisposable
{
    bool _finished;

    public Guid Id { get; } = id;

    /// <summary>Replaces the entry's detail (e.g. from "Searching torrents" to "Using Soulseek").</summary>
    public void Update(string? detail = null)
    {
        if (_finished)
            return;
        owner.Update(Id, detail);
    }

    public void Dispose()
    {
        if (_finished)
            return;
        _finished = true;
        owner.Remove(Id);
    }
}
