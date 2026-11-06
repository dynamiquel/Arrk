using Microsoft.Extensions.Logging.Abstractions;
using Arrk.Core;
using Arrk.Core.Configuration;
using Arrk.Core.Enums;
using Arrk.Core.Events;
using Arrk.Core.Lifecycle;
using Arrk.Core.Models;
using Arrk.Core.Playback;
using Arrk.TestSupport;

namespace Arrk.MediaLifecycle.Tests;

public class MediaLifecycleServiceTests
{
    [Test]
    public async Task EvaluatesFreshExpiringAndStaleItems()
    {
        await using TestDatabase db = await TestDatabase.CreateAsync(new DateTimeOffset(2026, 2, 1, 12, 0, 0, TimeSpan.Zero));
        FakeProvider provider = new("tv");
        provider.Items.Add(Item("fresh", db.Time.Now.UtcDateTime, addedDaysAgo: 10));
        provider.Items.Add(Item("expiring", db.Time.Now.UtcDateTime, addedDaysAgo: 25));
        provider.Items.Add(Item("stale", db.Time.Now.UtcDateTime, addedDaysAgo: 31));

        MutableOptionsMonitor<ArrkConfig> config = new(TestConfig.WithStaleness(30, 20, autoCleanup: false));
        MediaLifecycleService service = new(
            [provider],
            new WatchActivityService(db.Factory, db.Time),
            new MediaPinService(db.Factory, db.Time),
            config,
            db.Time,
            NullLogger<MediaLifecycleService>.Instance);

        StaleMediaReport report = await service.GetReportAsync(forceRefresh: true);

        await Assert.That(report.Items.Single(x => x.ItemKey == "fresh").Level).IsEqualTo(StaleLevel.Fresh);
        await Assert.That(report.Items.Single(x => x.ItemKey == "expiring").Level).IsEqualTo(StaleLevel.Expiring);
        await Assert.That(report.Items.Single(x => x.ItemKey == "stale").Level).IsEqualTo(StaleLevel.Stale);
        await Assert.That(report.StaleBytes).IsEqualTo(100);
    }

    [Test]
    public async Task WatchingAnItem_ResetsItsClock()
    {
        await using TestDatabase db = await TestDatabase.CreateAsync(new DateTimeOffset(2026, 2, 1, 12, 0, 0, TimeSpan.Zero));
        FakeProvider provider = new("tv");
        provider.Items.Add(Item("movie-1", db.Time.Now.UtcDateTime, addedDaysAgo: 40));

        WatchActivityService watch = new(db.Factory, db.Time);
        await watch.RecordAsync(new WatchActivityRecord
        {
            Username = "Luna",
            ProviderKey = WatchActivityServices.Tv,
            Domain = new MediaDomain("movie"),
            ItemType = "movie",
            ItemKey = "movie-1",
            Title = "Movie",
            Source = WatchActivitySources.Jellyfin,
            StartedAt = db.Time.Now.UtcDateTime.AddDays(-3)
        });

        MutableOptionsMonitor<ArrkConfig> config = new(TestConfig.WithStaleness(30, 20, autoCleanup: false));
        MediaLifecycleService service = new([provider], watch, new MediaPinService(db.Factory, db.Time), config, db.Time, NullLogger<MediaLifecycleService>.Instance);

        StaleMediaReport report = await service.GetReportAsync(forceRefresh: true);
        StaleMediaItem item = report.Items.Single();
        await Assert.That(item.Level).IsEqualTo(StaleLevel.Fresh);
        await Assert.That(item.LastConsumedAt).IsNotNull();
        await Assert.That(item.AgeDays).IsLessThan(4);
    }

    [Test]
    public async Task ProtectedItems_UseTheLongerKeepThreshold()
    {
        await using TestDatabase db = await TestDatabase.CreateAsync(new DateTimeOffset(2026, 2, 1, 12, 0, 0, TimeSpan.Zero));
        FakeProvider provider = new("tv");
        provider.Items.Add(Item("watchlisted", db.Time.Now.UtcDateTime, addedDaysAgo: 40) with { IsBookmarked = true, BookmarkLabel = "Watchlist" });

        MutableOptionsMonitor<ArrkConfig> config = new(TestConfig.WithStaleness(30, 20, autoCleanup: false, keep: 45, keepLower: 35));
        MediaLifecycleService service = new(
            [provider],
            new WatchActivityService(db.Factory, db.Time),
            new MediaPinService(db.Factory, db.Time),
            config,
            db.Time,
            NullLogger<MediaLifecycleService>.Instance);

        StaleMediaReport report = await service.GetReportAsync(forceRefresh: true);
        StaleMediaItem item = report.Items.Single();
        await Assert.That(item.Level).IsEqualTo(StaleLevel.Expiring);
        await Assert.That(item.ThresholdDays).IsEqualTo(45);
    }

    [Test]
    public async Task Delete_UsesTheOwningProviderAndReturnsSpaceFreed()
    {
        await using TestDatabase db = await TestDatabase.CreateAsync();
        FakeProvider provider = new("music");
        provider.Items.Add(Item("album-1", db.Time.Now.UtcDateTime, addedDaysAgo: 40, size: 12345) with { ProviderKey = "music" });

        MediaLifecycleService service = new(
            [provider],
            new WatchActivityService(db.Factory, db.Time),
            new MediaPinService(db.Factory, db.Time),
            new MutableOptionsMonitor<ArrkConfig>(TestConfig.WithStaleness(30, 20, autoCleanup: false)),
            db.Time,
            NullLogger<MediaLifecycleService>.Instance);

        ManagedMediaDeleteResult result = await service.DeleteAsync("music", "album-1");

        await Assert.That(result.Success).IsTrue();
        await Assert.That(result.BytesFreed).IsEqualTo(12345);
        await Assert.That(provider.Deleted.Count).IsEqualTo(1);
    }


    [Test]
    public async Task PinnedItems_NeverGoStale_AndCannotBeDeleted()
    {
        await using TestDatabase db = await TestDatabase.CreateAsync(new DateTimeOffset(2026, 2, 1, 12, 0, 0, TimeSpan.Zero));
        FakeProvider provider = new("tv");
        provider.Items.Add(Item("keep-me", db.Time.Now.UtcDateTime, addedDaysAgo: 400));

        MediaPinService pins = new(db.Factory, db.Time);
        await pins.ToggleAsync("tv", "keep-me", "Keep me", "admin");

        MediaLifecycleService service = new(
            [provider],
            new WatchActivityService(db.Factory, db.Time),
            pins,
            new MutableOptionsMonitor<ArrkConfig>(TestConfig.WithStaleness(30, 20, autoCleanup: true)),
            db.Time,
            NullLogger<MediaLifecycleService>.Instance);

        StaleMediaReport report = await service.GetReportAsync(forceRefresh: true);
        StaleMediaItem item = report.Items.Single();
        await Assert.That(item.Level).IsEqualTo(StaleLevel.Fresh);
        await Assert.That(item.IsPinned).IsTrue();
        await Assert.That(report.Stale.Count()).IsEqualTo(0);

        ManagedMediaDeleteResult result = await service.DeleteAsync("tv", "keep-me");
        await Assert.That(result.Success).IsFalse();
    }

    [Test]
    public async Task PinningAParent_KeepsItsChildrenFresh()
    {
        await using TestDatabase db = await TestDatabase.CreateAsync(new DateTimeOffset(2026, 2, 1, 12, 0, 0, TimeSpan.Zero));
        FakeProvider provider = new("tv");
        provider.Items.Add(Item("season-1", db.Time.Now.UtcDateTime, addedDaysAgo: 400) with { ParentItemKey = "series-1" });

        MediaPinService pins = new(db.Factory, db.Time);
        await pins.ToggleAsync("tv", "series-1", "Series", "admin");

        MediaLifecycleService service = new(
            [provider],
            new WatchActivityService(db.Factory, db.Time),
            pins,
            new MutableOptionsMonitor<ArrkConfig>(TestConfig.WithStaleness(30, 20, autoCleanup: true)),
            db.Time,
            NullLogger<MediaLifecycleService>.Instance);

        StaleMediaReport report = await service.GetReportAsync(forceRefresh: true);
        await Assert.That(report.Items.Single().IsPinned).IsTrue();
        await Assert.That(report.Items.Single().Level).IsEqualTo(StaleLevel.Fresh);
    }

    internal static ManagedMediaItem Item(string key, DateTime now, int addedDaysAgo, long size = 100) => new(
        "tv",
        key,
        new MediaDomain("movie"),
        "movie",
        key,
        null,
        null,
        size,
        now.AddDays(-addedDaysAgo));
}

internal sealed class FakeProvider(string key) : IManagedMediaProvider
{
    public List<ManagedMediaItem> Items { get; } = [];

    public List<ManagedMediaItem> Deleted { get; } = [];

    public string Key => key;

    public string DisplayName => key;

    public Task<IReadOnlyList<ManagedMediaItem>> GetItemsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ManagedMediaItem>>(Items.ToList());

    public Task<ManagedMediaDeleteResult> DeleteAsync(ManagedMediaItem item, CancellationToken ct = default)
    {
        Deleted.Add(item);
        Items.RemoveAll(x => x.ItemKey == item.ItemKey);
        return Task.FromResult(new ManagedMediaDeleteResult(true, item.SizeOnDisk));
    }
}

