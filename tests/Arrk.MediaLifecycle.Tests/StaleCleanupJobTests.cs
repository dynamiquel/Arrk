using Microsoft.Extensions.Logging.Abstractions;
using Arrk.Core.Configuration;
using Arrk.Core.Lifecycle;
using Arrk.Notifications;
using Arrk.Core.Playback;
using Arrk.TestSupport;

namespace Arrk.MediaLifecycle.Tests;

public class StaleCleanupJobTests
{
    [Test]
    public async Task AlertsOncePerTransition_AndAutoDeletesWhenEnabled()
    {
        await using TestDatabase db = await TestDatabase.CreateAsync(new DateTimeOffset(2026, 2, 1, 12, 0, 0, TimeSpan.Zero));
        FakeProvider provider = new("tv");
        provider.Items.Add(MediaLifecycleServiceTests.Item("movie-1", db.Time.Now.UtcDateTime, addedDaysAgo: 25));

        MutableOptionsMonitor<ArrkConfig> config = new(TestConfig.WithStaleness(30, 20, autoCleanup: false));
        MediaLifecycleService lifecycle = new(
            [provider],
            new WatchActivityService(db.Factory, db.Time),
            new MediaPinService(db.Factory, db.Time),
            config,
            db.Time,
            NullLogger<MediaLifecycleService>.Instance);

        FakeEventStore events = new();
        StaleCleanupJob job = new(lifecycle, db.Factory, events, db.Time, NullLogger<StaleCleanupJob>.Instance);

        await job.Execute(CancellationToken.None);
        await Assert.That(events.Records.Count).IsEqualTo(1);
        await Assert.That(events.Records[0].Topic).IsEqualTo(MediaLifecycleTopics.AboutToBeStale);
        await Assert.That(events.Records[0].Notification!.Audience.Global).IsFalse();
        await Assert.That(events.Records[0].Notification!.Audience.FollowersOf!.ItemKey).IsEqualTo("movie-1");

        // Same level again: no duplicate alert.
        await job.Execute(CancellationToken.None);
        await Assert.That(events.Records.Count).IsEqualTo(1);

        // Cross the stale threshold: one stale alert.
        db.Time.Advance(TimeSpan.FromDays(10));
        await job.Execute(CancellationToken.None);
        await Assert.That(events.Records.Any(x => x.Topic == MediaLifecycleTopics.Stale)).IsTrue();

        // Turn on automatic cleanup: the next pass deletes it and records the reclaimed space.
        config.Update(TestConfig.WithStaleness(30, 20, autoCleanup: true));
        db.Time.Advance(TimeSpan.FromDays(1));
        await job.Execute(CancellationToken.None);

        await Assert.That(provider.Deleted.Count).IsEqualTo(1);
        await Assert.That(events.Records.Any(x => x.Topic == MediaLifecycleTopics.Deleted)).IsTrue();
    }

    [Test]
    public async Task FreshItems_ProduceNoAlerts()
    {
        await using TestDatabase db = await TestDatabase.CreateAsync(new DateTimeOffset(2026, 2, 1, 12, 0, 0, TimeSpan.Zero));
        FakeProvider provider = new("tv");
        provider.Items.Add(MediaLifecycleServiceTests.Item("fresh", db.Time.Now.UtcDateTime, addedDaysAgo: 2));

        MutableOptionsMonitor<ArrkConfig> config = new(TestConfig.WithStaleness(30, 20, autoCleanup: false));
        MediaLifecycleService lifecycle = new(
            [provider],
            new WatchActivityService(db.Factory, db.Time),
            new MediaPinService(db.Factory, db.Time),
            config,
            db.Time,
            NullLogger<MediaLifecycleService>.Instance);

        FakeEventStore events = new();
        StaleCleanupJob job = new(lifecycle, db.Factory, events, db.Time, NullLogger<StaleCleanupJob>.Instance);
        await job.Execute(CancellationToken.None);

        await Assert.That(events.Records.Count).IsEqualTo(0);
        await Assert.That(provider.Deleted.Count).IsEqualTo(0);
    }
}
