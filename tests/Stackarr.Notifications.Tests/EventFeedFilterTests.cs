using Stackarr.Notifications.Components.Shared;
using Stackarr.Ui.Components;
using Stackarr.Core.Events;
using Stackarr.Core.Models;

namespace Stackarr.Notifications.Tests;

/// <summary>
/// Tests the pure filtering logic behind the unified events page: the independent category toggles
/// and their default "everything on" state. A notification is modelled as an event carrying a topic.
/// </summary>
public class EventFeedFilterTests
{
    static EventFeedItem Item(
        string category = EventCategory.System,
        string? topic = null,
        string eventType = "SomethingHappened",
        string message = "something happened") => EventFeedItem.FromEvent(new SystemEvent
        {
            Id = 1,
            Timestamp = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            Category = category,
            EventType = eventType,
            Message = message,
            Severity = "Info",
            Topic = topic
        });

    static HashSet<string> Enabled(params string[] filters) =>
        filters.ToHashSet(StringComparer.OrdinalIgnoreCase);

    [Test]
    public async Task FromEvent_MapsFields_AndFlagsNotifications()
    {
        EventFeedItem auditOnly = Item();
        EventFeedItem notification = Item(topic: "downloads.completed");

        await Assert.That(auditOnly.IsNotification).IsFalse();
        await Assert.That(notification.IsNotification).IsTrue();
        await Assert.That(notification.Topic).IsEqualTo("downloads.completed");
        await Assert.That(notification.Category).IsEqualTo(EventCategory.System);
    }

    [Test]
    public async Task MissingFilter_MatchesEverything()
    {
        await Assert.That(EventFeedFilter.MatchesCategory(Item(), null)).IsTrue();
        await Assert.That(EventFeedFilter.MatchesCategory(Item(topic: "media.tv.added"), null)).IsTrue();
    }

    [Test]
    public async Task DownloadsFilter_MatchesByCategoryOrTopic()
    {
        await Assert.That(EventFeedFilter.MatchesCategory(
            Item(category: EventCategory.Download), EventFeedFilter.Downloads)).IsTrue();
        await Assert.That(EventFeedFilter.MatchesCategory(
            Item(category: EventCategory.System, topic: "downloads.completed"), EventFeedFilter.Downloads)).IsTrue();
        // Soulseek grabs belong to the downloads domain now.
        await Assert.That(EventFeedFilter.MatchesCategory(
            Item(category: EventCategory.System, topic: "downloads.soulseek.grabbed"), EventFeedFilter.Downloads)).IsTrue();
        await Assert.That(EventFeedFilter.MatchesCategory(
            Item(category: EventCategory.Media, topic: "media.tv.added"), EventFeedFilter.Downloads)).IsFalse();
    }

    [Test]
    public async Task MediaFilter_MatchesByCategoryOrTopic()
    {
        await Assert.That(EventFeedFilter.MatchesCategory(
            Item(category: EventCategory.Media), EventFeedFilter.Media)).IsTrue();
        await Assert.That(EventFeedFilter.MatchesCategory(
            Item(category: EventCategory.System, topic: "media.tv.upgraded"), EventFeedFilter.Media)).IsTrue();
        await Assert.That(EventFeedFilter.MatchesCategory(
            Item(category: EventCategory.Download), EventFeedFilter.Media)).IsFalse();
    }

    [Test]
    public async Task HealthFilter_CoversHealthStorageServiceAndHealthTopics()
    {
        await Assert.That(EventFeedFilter.MatchesCategory(
            Item(category: EventCategory.Health), EventFeedFilter.Health)).IsTrue();
        await Assert.That(EventFeedFilter.MatchesCategory(
            Item(category: EventCategory.Storage), EventFeedFilter.Health)).IsTrue();
        await Assert.That(EventFeedFilter.MatchesCategory(
            Item(category: EventCategory.Service), EventFeedFilter.Health)).IsTrue();
        await Assert.That(EventFeedFilter.MatchesCategory(
            Item(category: EventCategory.System, topic: "system.health.network.degraded"), EventFeedFilter.Health)).IsTrue();
        await Assert.That(EventFeedFilter.MatchesCategory(
            Item(category: EventCategory.Download), EventFeedFilter.Health)).IsFalse();
    }

    [Test]
    public async Task NotificationsFilter_ShowsOnlyEventsWithATopic()
    {
        await Assert.That(EventFeedFilter.MatchesCategory(
            Item(topic: "downloads.completed"), EventFeedFilter.Notifications)).IsTrue();
        await Assert.That(EventFeedFilter.MatchesCategory(
            Item(), EventFeedFilter.Notifications)).IsFalse();
    }

    [Test]
    public async Task JobsFilter_MatchesOnlyJobCategory()
    {
        await Assert.That(EventFeedFilter.MatchesCategory(
            Item(category: EventCategory.Job), EventFeedFilter.Jobs)).IsTrue();
        await Assert.That(EventFeedFilter.MatchesCategory(
            Item(category: EventCategory.System), EventFeedFilter.Jobs)).IsFalse();
    }

    [Test]
    public async Task JobEvents_CarryATopic_ButAreNotNotifications()
    {
        EventFeedItem item = Item(category: EventCategory.Job, topic: "job.config-sync.started");

        await Assert.That(item.IsNotification).IsFalse();
        await Assert.That(EventFeedFilter.MatchesCategory(item, EventFeedFilter.Jobs)).IsTrue();
        await Assert.That(EventFeedFilter.MatchesCategory(item, EventFeedFilter.Notifications)).IsFalse();
    }

    [Test]
    public async Task DefaultSelection_EnablesEveryToggle()
    {
        HashSet<string> enabled = EventFeedFilter.CreateDefaultSelection();

        await Assert.That(enabled.Count).IsEqualTo(EventFeedFilter.Filters.Count);
        await Assert.That(EventFeedFilter.Filters.Select(f => f.Id))
            .IsEquivalentTo([EventFeedFilter.Downloads, EventFeedFilter.Media, EventFeedFilter.System,
                EventFeedFilter.Jobs, EventFeedFilter.Health, EventFeedFilter.Notifications]);
    }

    [Test]
    public async Task IsVisible_WithEveryToggleOn_ShowsEveryCategory()
    {
        HashSet<string> enabled = EventFeedFilter.CreateDefaultSelection();

        await Assert.That(EventFeedFilter.IsVisible(Item(category: EventCategory.Download), enabled)).IsTrue();
        await Assert.That(EventFeedFilter.IsVisible(Item(category: EventCategory.Job), enabled)).IsTrue();
        await Assert.That(EventFeedFilter.IsVisible(Item(category: EventCategory.Health), enabled)).IsTrue();
        await Assert.That(EventFeedFilter.IsVisible(Item(topic: "downloads.completed"), enabled)).IsTrue();
    }

    [Test]
    public async Task IsVisible_WhenJobsAreSwitchedOff_HidesJobEventsButKeepsEverythingElse()
    {
        HashSet<string> enabled = EventFeedFilter.CreateDefaultSelection();
        enabled.Remove(EventFeedFilter.Jobs);

        await Assert.That(EventFeedFilter.IsVisible(Item(category: EventCategory.Job), enabled)).IsFalse();
        await Assert.That(EventFeedFilter.IsVisible(Item(category: EventCategory.System), enabled)).IsTrue();
        await Assert.That(EventFeedFilter.IsVisible(Item(category: EventCategory.Download), enabled)).IsTrue();
        await Assert.That(EventFeedFilter.IsVisible(Item(category: EventCategory.Health), enabled)).IsTrue();
    }

    [Test]
    public async Task IsVisible_WhenDownloadsAreSwitchedOff_HidesDownloadOnlyEvents()
    {
        HashSet<string> enabled = EventFeedFilter.CreateDefaultSelection();
        enabled.Remove(EventFeedFilter.Downloads);

        await Assert.That(EventFeedFilter.IsVisible(
            Item(category: EventCategory.Download), enabled)).IsFalse();
        await Assert.That(EventFeedFilter.IsVisible(
            Item(category: EventCategory.System, topic: "downloads.completed"), enabled)).IsTrue();
    }

    [Test]
    public async Task IsVisible_KeepsAnEventWhileAnyOfItsCategoriesIsEnabled()
    {
        HashSet<string> enabled = Enabled(EventFeedFilter.Notifications);

        // A notification is also a downloads event, but the Notifications toggle alone keeps it.
        await Assert.That(EventFeedFilter.IsVisible(
            Item(category: EventCategory.Download, topic: "downloads.completed"), enabled)).IsTrue();
        await Assert.That(EventFeedFilter.IsVisible(
            Item(category: EventCategory.Download), enabled)).IsFalse();
    }

    [Test]
    public async Task IsVisible_WithNoTogglesOn_HidesEverything()
    {
        await Assert.That(EventFeedFilter.IsVisible(
            Item(category: EventCategory.System), Enabled())).IsFalse();
        await Assert.That(EventFeedFilter.IsVisible(
            Item(category: EventCategory.Job), Enabled())).IsFalse();
    }

    [Test]
    public async Task IsVisible_UnknownCategory_StaysVisibleWhileAnyToggleIsOn()
    {
        HashSet<string> enabled = Enabled(EventFeedFilter.System);

        await Assert.That(EventFeedFilter.IsVisible(Item(category: "FutureCategory"), enabled)).IsTrue();
    }

    [Test]
    public async Task Filters_ExposeFriendlyLabels()
    {
        await Assert.That(EventFeedFilter.Filters.Select(f => f.Label))
            .Contains("Downloads");
        await Assert.That(EventFeedFilter.Filters.Select(f => f.Label))
            .Contains("System");
        await Assert.That(EventFeedFilter.Filters.Select(f => f.Label))
            .Contains("Jobs");
        await Assert.That(EventFeedFilter.Filters.Select(f => f.Label))
            .Contains("Media");
        await Assert.That(EventFeedFilter.Filters.Select(f => f.Label))
            .Contains("Notifications");
        await Assert.That(EventFeedFilter.Filters.Select(f => f.Label))
            .DoesNotContain("All");
    }
}
