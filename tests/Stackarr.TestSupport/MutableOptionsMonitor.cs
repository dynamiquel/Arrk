using Microsoft.Extensions.Options;

namespace Stackarr.TestSupport;

/// <summary>
/// Mutable <see cref="IOptionsMonitor{T}"/> for tests. Assigning <see cref="CurrentValue"/> swaps
/// the value silently, while <see cref="Update"/> also raises the registered change callbacks,
/// exactly like a real configuration reload. Prefer <see cref="Update"/> when the test asserts that
/// live consumers pick up a config edit.
/// </summary>
public sealed class MutableOptionsMonitor<T>(T initialValue) : IOptionsMonitor<T>
{
    readonly List<Action<T, string?>> _listeners = [];
    T _current = initialValue;

    public T CurrentValue
    {
        get => _current;
        set => _current = value;
    }

    public T Get(string? name) => _current;

    public IDisposable OnChange(Action<T, string?> listener)
    {
        _listeners.Add(listener);
        return new Subscription(_listeners, listener);
    }

    public void Update(T value)
    {
        _current = value;
        foreach (Action<T, string?> listener in _listeners.ToArray())
            listener(value, null);
    }

    sealed class Subscription(List<Action<T, string?>> listeners, Action<T, string?> listener) : IDisposable
    {
        public void Dispose() => listeners.Remove(listener);
    }
}
