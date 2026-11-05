namespace Stackarr.Ui;

public enum ActivityState
{
    Running,
    Success,
    Error,
    Info
}

/// <summary>A single tracked background/foreground operation shown in the activity center.</summary>
public sealed class ActivityItem
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Title { get; set; } = "";
    public string? Detail { get; set; }
    public ActivityState State { get; set; } = ActivityState.Running;
    public double? Progress { get; set; }
    public string? Icon { get; set; }
    public DateTime StartedAt { get; init; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }

    public bool IsRunning => State == ActivityState.Running;
    public TimeSpan Duration => (CompletedAt ?? DateTime.UtcNow) - StartedAt;
}

/// <summary>
/// Tracks operations (searching, adding to library, refreshing, fetching metadata, …)
/// so the UI can show meaningful, persistent status instead of transient toasts.
/// </summary>
public sealed class ActivityService
{
    const int MaxItems = 60;
    static readonly TimeSpan CompletedRetention = TimeSpan.FromMinutes(15);

    readonly Lock _gate = new();
    readonly List<ActivityItem> _items = [];

    public event Action? Changed;

    public IReadOnlyList<ActivityItem> Items
    {
        get
        {
            lock (_gate)
            {
                Prune();
                return _items.OrderByDescending(i => i.StartedAt).ToList();
            }
        }
    }

    public int ActiveCount
    {
        get
        {
            lock (_gate)
            {
                return _items.Count(i => i.IsRunning);
            }
        }
    }

    public ActivityHandle Begin(string title, string? detail = null, string? icon = null)
    {
        ActivityItem item = new()
        {
            Title = title,
            Detail = detail,
            Icon = icon
        };
        lock (_gate)
        {
            _items.Add(item);
            Prune();
        }
        Notify();
        return new ActivityHandle(this, item);
    }

    /// <summary>Convenience helper that runs an async operation while tracking its status.</summary>
    public async Task<T> RunAsync<T>(string title, Func<Task<T>> operation, string? detail = null, string? icon = null)
    {
        ActivityHandle handle = Begin(title, detail, icon);
        try
        {
            T result = await operation();
            handle.Complete();
            return result;
        }
        catch (Exception ex)
        {
            handle.Fail(ex.Message);
            throw;
        }
    }

    public async Task RunAsync(string title, Func<Task> operation, string? detail = null, string? icon = null)
    {
        ActivityHandle handle = Begin(title, detail, icon);
        try
        {
            await operation();
            handle.Complete();
        }
        catch (Exception ex)
        {
            handle.Fail(ex.Message);
            throw;
        }
    }

    public void ClearCompleted()
    {
        lock (_gate)
        {
            _items.RemoveAll(i => !i.IsRunning);
        }
        Notify();
    }

    public void Clear()
    {
        lock (_gate)
        {
            _items.Clear();
        }
        Notify();
    }

    internal void Update(ActivityItem item)
    {
        lock (_gate)
        {
            Prune();
        }
        Notify();
    }

    /// <summary>Removes a single tracked item (used by <see cref="ActivityHandle.Dismiss"/>).</summary>
    internal void Remove(ActivityItem item)
    {
        lock (_gate)
        {
            _items.Remove(item);
        }
        Notify();
    }

    void Prune()
    {
        DateTime cutoff = DateTime.UtcNow - CompletedRetention;
        _items.RemoveAll(i => i.CompletedAt.HasValue && i.CompletedAt.Value < cutoff);
        if (_items.Count > MaxItems)
        {
            int overflow = _items.Count - MaxItems;
            IEnumerable<ActivityItem> removable = _items
                .Where(i => !i.IsRunning)
                .OrderBy(i => i.StartedAt)
                .Take(overflow)
                .ToList();
            foreach (ActivityItem item in removable)
            {
                _items.Remove(item);
            }
        }
    }

    void Notify() => Changed?.Invoke();
}

/// <summary>Handle returned by <see cref="ActivityService.Begin"/>; update and complete the operation with it.</summary>
public sealed class ActivityHandle
{
    readonly ActivityService _owner;
    bool _finished;

    internal ActivityHandle(ActivityService owner, ActivityItem item)
    {
        _owner = owner;
        Item = item;
    }

    public ActivityItem Item { get; }

    public void Report(double? progress, string? detail = null)
    {
        Item.Progress = progress;
        if (detail != null) Item.Detail = detail;
        Item.State = ActivityState.Running;
        _owner.Update(Item);
    }

    public void Complete(string? detail = null)
    {
        if (_finished) return;
        _finished = true;
        Item.State = ActivityState.Success;
        if (detail != null) Item.Detail = detail;
        Item.Progress = 1;
        Item.CompletedAt = DateTime.UtcNow;
        _owner.Update(Item);
    }

    public void Fail(string? detail = null)
    {
        if (_finished) return;
        _finished = true;
        Item.State = ActivityState.Error;
        if (detail != null) Item.Detail = detail;
        Item.CompletedAt = DateTime.UtcNow;
        _owner.Update(Item);
    }

    public void Info(string? detail = null)
    {
        if (_finished) return;
        _finished = true;
        Item.State = ActivityState.Info;
        if (detail != null) Item.Detail = detail;
        Item.CompletedAt = DateTime.UtcNow;
        _owner.Update(Item);
    }

    /// <summary>
    /// Removes the item instead of leaving it in the list. Use for transient, live-only operations
    /// (e.g. an in-flight search) that should disappear the moment they finish.
    /// </summary>
    public void Dismiss()
    {
        if (_finished) return;
        _finished = true;
        _owner.Remove(Item);
    }
}
