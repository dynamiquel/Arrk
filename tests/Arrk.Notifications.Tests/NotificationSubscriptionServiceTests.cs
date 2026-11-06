using Microsoft.Extensions.Logging.Abstractions;
using Arrk.Core.Notifications;
using Arrk.Media.Abstractions;

namespace Arrk.Notifications.Tests;

public class NotificationSubscriptionServiceTests
{
    static NotificationSubscriptionService Service(TestDatabase db) =>
        new(db.Factory, TimeProvider.System, NullLogger<NotificationSubscriptionService>.Instance);

    static MediaEntityRef Series(string key = "tv:series:tvdb:500") => new(
        "tv", new MediaDomain("tv"), key, "Severance");

    static MediaEntityRef Season(string seriesKey = "tv:series:tvdb:500", int season = 1) => new(
        "tv",
        new MediaDomain("tv"),
        $"{seriesKey}:s{season}",
        "Severance",
        $"Season {season}",
        ParentItemKey: seriesKey,
        AncestorItemKeys: [seriesKey]);

    static MediaEntityRef Episode(string seriesKey = "tv:series:tvdb:500", int season = 1, int episode = 2) => new(
        "tv",
        new MediaDomain("tv"),
        $"{seriesKey}:s{season}e{episode}",
        "Severance",
        $"S{season:00}E{episode:00}",
        ParentItemKey: $"{seriesKey}:s{season}",
        AncestorItemKeys: [$"{seriesKey}:s{season}", seriesKey]);

    [Test]
    public async Task Series_subscription_matches_episode_descendants()
    {
        await using TestDatabase db = new();
        NotificationSubscriptionService service = Service(db);

        await service.SubscribeAsync(7, Series(), new NotificationSubscriptionOptions { IncludeDescendants = true });

        NotificationSubscriptionAudience audience = await service.ResolveAudienceAsync(
            Episode(), "downloads.imported");

        await Assert.That(audience.UserIds).Contains(7);
        await Assert.That(audience.SubscriptionIds.Count).IsEqualTo(1);
    }

    [Test]
    public async Task Season_subscription_does_not_match_another_season()
    {
        await using TestDatabase db = new();
        NotificationSubscriptionService service = Service(db);

        await service.SubscribeAsync(7, Season(season: 1), new NotificationSubscriptionOptions { IncludeDescendants = true });

        NotificationSubscriptionAudience sameSeason = await service.ResolveAudienceAsync(
            Episode(season: 1), "downloads.imported");
        NotificationSubscriptionAudience otherSeason = await service.ResolveAudienceAsync(
            Episode(season: 2), "downloads.imported");

        await Assert.That(sameSeason.UserIds).Contains(7);
        await Assert.That(otherSeason.UserIds).IsEmpty();
    }

    [Test]
    public async Task Progress_topic_is_never_targeted_even_when_following()
    {
        await using TestDatabase db = new();
        NotificationSubscriptionService service = Service(db);
        await service.SubscribeAsync(7, Series(), new NotificationSubscriptionOptions { IncludeDescendants = true });

        NotificationSubscriptionAudience audience = await service.ResolveAudienceAsync(
            Episode(), DownloadNotificationTopics.DownloadProgress);

        await Assert.That(audience.UserIds).IsEmpty();
    }

    [Test]
    public async Task Disabled_category_filters_that_topic_only()
    {
        await using TestDatabase db = new();
        NotificationSubscriptionService service = Service(db);
        await service.SubscribeAsync(7, Series(), new NotificationSubscriptionOptions
        {
            IncludeDescendants = true,
            NewContent = false
        });

        NotificationSubscriptionAudience imports = await service.ResolveAudienceAsync(
            Episode(), "downloads.imported");
        NotificationSubscriptionAudience problems = await service.ResolveAudienceAsync(
            Episode(), DownloadNotificationTopics.TorrentError);

        await Assert.That(imports.UserIds).IsEmpty();
        await Assert.That(problems.UserIds).Contains(7);
    }

    [Test]
    public async Task Subscribe_is_idempotent_and_updates_the_display_snapshot()
    {
        await using TestDatabase db = new();
        NotificationSubscriptionService service = Service(db);

        await service.SubscribeAsync(7, Series("tv:series:tvdb:1"), source: "request", sourceId: "99");
        await service.SubscribeAsync(7, Series("tv:series:tvdb:1"), source: "manual");

        IReadOnlyList<NotificationSubscription> following = await service.GetForUserAsync(7);
        await Assert.That(following.Count).IsEqualTo(1);
        await Assert.That(following[0].Source).IsEqualTo("request");
        await Assert.That(following[0].SourceId).IsEqualTo("99");
    }

    [Test]
    public async Task Update_options_changes_only_the_requested_categories()
    {
        await using TestDatabase db = new();
        NotificationSubscriptionService service = Service(db);
        NotificationSubscription subscription = await service.SubscribeAsync(7, Series());

        bool updated = await service.UpdateOptionsAsync(
            subscription.Id,
            7,
            new NotificationSubscriptionOptions
            {
                Status = false,
                Download = false,
                NewContent = true,
                Problems = false,
                Lifecycle = false
            });

        IReadOnlyList<NotificationSubscription> following = await service.GetForUserAsync(7);
        await Assert.That(updated).IsTrue();
        await Assert.That(following[0].NotifyStatus).IsFalse();
        await Assert.That(following[0].NotifyDownload).IsFalse();
        await Assert.That(following[0].NotifyNewContent).IsTrue();
        await Assert.That(following[0].NotifyProblems).IsFalse();
        await Assert.That(following[0].NotifyLifecycle).IsFalse();
    }

    [Test]
    public async Task Resubscribe_does_not_reset_the_users_category_choices()
    {
        await using TestDatabase db = new();
        NotificationSubscriptionService service = Service(db);
        NotificationSubscription subscription = await service.SubscribeAsync(7, Series());
        await service.UpdateOptionsAsync(subscription.Id, 7, new NotificationSubscriptionOptions
        {
            Download = false,
            Lifecycle = false
        });

        await service.SubscribeAsync(7, Series(), new NotificationSubscriptionOptions
        {
            Download = true,
            Lifecycle = true
        });

        IReadOnlyList<NotificationSubscription> following = await service.GetForUserAsync(7);
        await Assert.That(following[0].NotifyDownload).IsFalse();
        await Assert.That(following[0].NotifyLifecycle).IsFalse();
    }

    [Test]
    public async Task Remove_source_removes_request_subscriptions_only()
    {
        await using TestDatabase db = new();
        NotificationSubscriptionService service = Service(db);
        await service.SubscribeAsync(7, Series("tv:series:tvdb:1"), source: "request", sourceId: "99");
        await service.SubscribeAsync(7, Series("tv:series:tvdb:2"), source: "manual");

        int removed = await service.RemoveSourceAsync("request", "99");

        await Assert.That(removed).IsEqualTo(1);
        IReadOnlyList<NotificationSubscription> following = await service.GetForUserAsync(7);
        await Assert.That(following.Count).IsEqualTo(1);
        await Assert.That(following[0].Source).IsEqualTo("manual");
    }
}
