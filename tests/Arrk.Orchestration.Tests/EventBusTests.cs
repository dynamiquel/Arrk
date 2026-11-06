using Arrk.Core.Events;
using Arrk.Core.Models;

namespace Arrk.Orchestration.Tests;

/// <summary>
/// Behaviour of the core <see cref="EventBus"/>, subscribe, publish, and dispose semantics;
/// plus the event-argument payloads that handlers receive.
/// </summary>
public class EventBusTests
{
    [Test]
    public async Task Publish_ToSubscriber_InvokesHandler()
    {
        EventBus bus = new();
        List<TorrentsChangedEventArgs> received = [];

        using IDisposable subscription = bus.Subscribe<TorrentsChangedEventArgs>((e, _) =>
        {
            received.Add(e);
            return ValueTask.CompletedTask;
        });

        TorrentsChangedEventArgs payload = new()
        {
            Torrents = [new TorrentItem { Hash = "abc", Name = "Test" }]
        };
        await bus.Publish(payload);

        await Assert.That(received).Count().IsEqualTo(1);
        await Assert.That(received[0].Torrents[0].Hash).IsEqualTo("abc");
    }

    [Test]
    public async Task Publish_ToMultipleSubscribers_InvokesAll()
    {
        EventBus bus = new();
        int first = 0;
        int second = 0;

        using IDisposable a = bus.Subscribe<TorrentsChangedEventArgs>((_, _) =>
        {
            first++;
            return ValueTask.CompletedTask;
        });
        using IDisposable b = bus.Subscribe<TorrentsChangedEventArgs>((_, _) =>
        {
            second++;
            return ValueTask.CompletedTask;
        });

        await bus.Publish(new TorrentsChangedEventArgs());

        await Assert.That(first).IsEqualTo(1);
        await Assert.That(second).IsEqualTo(1);
    }

    [Test]
    public async Task Publish_OnlyDeliversToMatchingEventType()
    {
        EventBus bus = new();
        int torrentEvents = 0;
        int movieEvents = 0;

        using IDisposable a = bus.Subscribe<TorrentsChangedEventArgs>((_, _) =>
        {
            torrentEvents++;
            return ValueTask.CompletedTask;
        });
        using IDisposable b = bus.Subscribe<MoviesChangedEventArgs>((_, _) =>
        {
            movieEvents++;
            return ValueTask.CompletedTask;
        });

        await bus.Publish(new TorrentsChangedEventArgs());

        await Assert.That(torrentEvents).IsEqualTo(1);
        await Assert.That(movieEvents).IsEqualTo(0);
    }

    [Test]
    public async Task Dispose_Subscription_StopsDelivery()
    {
        EventBus bus = new();
        int count = 0;

        IDisposable subscription = bus.Subscribe<TorrentsChangedEventArgs>((_, _) =>
        {
            count++;
            return ValueTask.CompletedTask;
        });

        await bus.Publish(new TorrentsChangedEventArgs());
        subscription.Dispose();
        await bus.Publish(new TorrentsChangedEventArgs());

        await Assert.That(count).IsEqualTo(1);
    }

    [Test]
    public async Task Publish_WithNoSubscribers_Completes()
    {
        EventBus bus = new();

        // Should simply complete without throwing when nothing is subscribed.
        await bus.Publish(new TorrentsChangedEventArgs());
    }

    [Test]
    public async Task Dispose_Subscription_IsIdempotent()
    {
        EventBus bus = new();
        int first = 0;
        int second = 0;

        IDisposable a = bus.Subscribe<TorrentsChangedEventArgs>((_, _) => { first++; return ValueTask.CompletedTask; });
        using IDisposable b = bus.Subscribe<TorrentsChangedEventArgs>((_, _) => { second++; return ValueTask.CompletedTask; });

        // Disposing the same subscription twice must not throw (or remove the other subscriber).
        a.Dispose();
        a.Dispose();

        await bus.Publish(new TorrentsChangedEventArgs());

        await Assert.That(first).IsEqualTo(0);
        await Assert.That(second).IsEqualTo(1);
    }

    [Test]
    public async Task SubscribeAndDispose_Concurrently_DoesNotThrow()
    {
        EventBus bus = new();

        await Assert.That(async () => await Task.WhenAll(Enumerable.Range(0, 200).Select(_ => Task.Run(async () =>
        {
            IDisposable subscription = bus.Subscribe<TorrentsChangedEventArgs>((_, _) => ValueTask.CompletedTask);
            await bus.Publish(new TorrentsChangedEventArgs());
            subscription.Dispose();
        })))).ThrowsNothing();
    }
}
