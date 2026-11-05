using System.Collections.Concurrent;

namespace Stackarr.Core.Events;

public interface IEventBus
{
    ValueTask Publish<T>(T @event, CancellationToken ct = default) where T : class;
    IDisposable Subscribe<T>(Func<T, CancellationToken, ValueTask> handler) where T : class;
}

public sealed class EventBus : IEventBus
{
    readonly ConcurrentDictionary<Type, List<Func<object, CancellationToken, ValueTask>>> _handlers = new();

    public ValueTask Publish<T>(T @event, CancellationToken ct = default) where T : class
    {
        if (!_handlers.TryGetValue(typeof(T), out List<Func<object, CancellationToken, ValueTask>>? handlers))
            return ValueTask.CompletedTask;

        // Snapshot under the list lock so a concurrent Subscribe/Dispose cannot mutate the list
        // while it is being enumerated.
        Func<object, CancellationToken, ValueTask>[] snapshot;
        lock (handlers)
            snapshot = handlers.ToArray();

        if (snapshot.Length == 0)
            return ValueTask.CompletedTask;

        return new ValueTask(Task.WhenAll(snapshot.Select(h => h(@event!, ct).AsTask())));
    }

    public IDisposable Subscribe<T>(Func<T, CancellationToken, ValueTask> handler) where T : class
    {
        List<Func<object, CancellationToken, ValueTask>> list = _handlers.GetOrAdd(typeof(T), _ => new());
        Func<object, CancellationToken, ValueTask> wrapper = (o, ct) => handler((T)o, ct);
        lock (list)
            list.Add(wrapper);
        return new Subscription(list, wrapper);
    }

    sealed class Subscription(List<Func<object, CancellationToken, ValueTask>> list, Func<object, CancellationToken, ValueTask> handler) : IDisposable
    {
        bool _disposed;

        public void Dispose()
        {
            // Idempotent: disposing twice (or racing with the host stopping) must never remove
            // another subscriber's handler or throw.
            if (_disposed)
                return;

            _disposed = true;
            lock (list)
                list.Remove(handler);
        }
    }
}