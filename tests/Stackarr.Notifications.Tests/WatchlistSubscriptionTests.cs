using Microsoft.Extensions.Logging.Abstractions;
using Stackarr.Core.Notifications;
using Stackarr.Core.Playback;
using Stackarr.Media.Abstractions;

namespace Stackarr.Notifications.Tests;

public class WatchlistSubscriptionTests
{
    static (WatchlistService Lists, NotificationSubscriptionService Subscriptions) Services(TestDatabase db)
    {
        NotificationSubscriptionService subscriptions = TestNotifications.Subscriptions(db);
        WatchlistService lists = new(db.Factory, subscriptions, TimeProvider.System);
        return (lists, subscriptions);
    }

    static WatchlistItemRef Series(string key = "tv:series:tvdb:500") => new(
        "tv", new MediaDomain("tv"), key, "Severance", PosterUrl: "https://img/poster.jpg");

    static WatchlistItemRef Movie(int tmdb = 603) => new(
        "tv", new MediaDomain("movie"), $"tv:movie:tmdb:{tmdb}", "The Matrix");

    [Test]
    public async Task Bookmarking_a_series_also_follows_it_and_its_children()
    {
        await using TestDatabase db = new();
        (WatchlistService lists, NotificationSubscriptionService subscriptions) = Services(db);
        WatchlistDetail list = (await lists.CreateAsync(7, "Watchlist"))!;

        await lists.AddItemAsync(7, list.Id, Series());

        IReadOnlyList<NotificationSubscription> following = await subscriptions.GetForUserAsync(7);
        await Assert.That(following.Count).IsEqualTo(1);
        await Assert.That(following[0].Source).IsEqualTo("watchlist");
        await Assert.That(following[0].IncludeDescendants).IsTrue();
    }

    [Test]
    public async Task Bookmarking_a_movie_follows_only_that_movie()
    {
        await using TestDatabase db = new();
        (WatchlistService lists, NotificationSubscriptionService subscriptions) = Services(db);
        WatchlistDetail list = (await lists.CreateAsync(7, "Watchlist"))!;

        await lists.AddItemAsync(7, list.Id, Movie());

        IReadOnlyList<NotificationSubscription> following = await subscriptions.GetForUserAsync(7);
        await Assert.That(following[0].IncludeDescendants).IsFalse();
    }

    [Test]
    public async Task Removing_the_last_bookmark_removes_the_follow()
    {
        await using TestDatabase db = new();
        (WatchlistService lists, NotificationSubscriptionService subscriptions) = Services(db);
        WatchlistDetail list = (await lists.CreateAsync(7, "Watchlist"))!;
        await lists.AddItemAsync(7, list.Id, Series());

        await lists.RemoveItemAsync(7, list.Id, "tv:series:tvdb:500");

        await Assert.That(await subscriptions.GetForUserAsync(7)).IsEmpty();
    }

    [Test]
    public async Task The_follow_survives_while_the_item_is_in_another_list()
    {
        await using TestDatabase db = new();
        (WatchlistService lists, NotificationSubscriptionService subscriptions) = Services(db);
        WatchlistDetail first = (await lists.CreateAsync(7, "First"))!;
        WatchlistDetail second = (await lists.CreateAsync(7, "Second"))!;
        await lists.AddItemAsync(7, first.Id, Series());
        await lists.AddItemAsync(7, second.Id, Series());

        await lists.RemoveItemAsync(7, first.Id, "tv:series:tvdb:500");

        await Assert.That(await subscriptions.GetForUserAsync(7)).IsNotEmpty();
    }

    [Test]
    public async Task Deleting_a_list_removes_its_watchlist_follows()
    {
        await using TestDatabase db = new();
        (WatchlistService lists, NotificationSubscriptionService subscriptions) = Services(db);
        WatchlistDetail list = (await lists.CreateAsync(7, "Watchlist"))!;
        await lists.AddItemAsync(7, list.Id, Series());

        await lists.DeleteAsync(7, list.Id);

        await Assert.That(await subscriptions.GetForUserAsync(7)).IsEmpty();
    }

    [Test]
    public async Task Removing_a_bookmark_does_not_remove_a_manual_follow()
    {
        await using TestDatabase db = new();
        (WatchlistService lists, NotificationSubscriptionService subscriptions) = Services(db);
        MediaEntityRef entity = new("tv", new MediaDomain("tv"), "tv:series:tvdb:500", "Severance");
        await subscriptions.SubscribeAsync(7, entity, new NotificationSubscriptionOptions { IncludeDescendants = true });
        WatchlistDetail list = (await lists.CreateAsync(7, "Watchlist"))!;
        await lists.AddItemAsync(7, list.Id, Series());

        await lists.RemoveItemAsync(7, list.Id, "tv:series:tvdb:500");

        IReadOnlyList<NotificationSubscription> following = await subscriptions.GetForUserAsync(7);
        await Assert.That(following.Count).IsEqualTo(1);
        await Assert.That(following[0].Source).IsEqualTo("manual");
    }
}
