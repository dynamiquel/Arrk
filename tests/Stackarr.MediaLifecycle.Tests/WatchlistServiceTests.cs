using Microsoft.Extensions.Logging.Abstractions;
using Stackarr.Core.Enums;
using Stackarr.Core.Notifications;
using Stackarr.Core.Playback;

namespace Stackarr.MediaLifecycle.Tests;

public class WatchlistServiceTests
{
    static WatchlistService Create(TestDatabase db) => new(
        db.Factory,
        new NotificationSubscriptionService(db.Factory, db.Time, NullLogger<NotificationSubscriptionService>.Instance),
        db.Time);

    [Test]
    public async Task AddsItems_ToMultipleLists_AndReportsProtection()
    {
        await using TestDatabase db = await TestDatabase.CreateAsync();
        WatchlistService lists = Create(db);

        WatchlistDetail keep = (await lists.CreateAsync(1, "Luna 3 Years"))!;
        WatchlistDetail later = (await lists.CreateAsync(1, "Luna 5 Years"))!;
        WatchlistItemRef item = new(
            WatchActivityServices.Tv,
            new MediaDomain("movie"),
            MediaItemKeys.Movie(42),
            "Arrival",
            "2016",
            "poster.jpg",
            2016,
            42);

        await Assert.That(await lists.AddItemAsync(1, keep.Id, item)).IsTrue();
        await Assert.That(await lists.AddItemAsync(1, later.Id, item)).IsTrue();

        IReadOnlyList<int> containing = await lists.GetContainingAsync(1, item.ProviderKey, item.ItemKey);
        await Assert.That(containing.Count).IsEqualTo(2);
        await Assert.That(await lists.IsTrackedAsync(1, item.ProviderKey, item.ItemKey)).IsTrue();

        IReadOnlySet<string> tracked = await lists.GetTrackedItemKeysAsync(WatchActivityServices.Tv);
        await Assert.That(tracked.Contains(item.ItemKey)).IsTrue();

        WatchlistDetail? detail = await lists.GetAsync(1, keep.Id);
        await Assert.That(detail).IsNotNull();
        await Assert.That(detail!.Items.Count).IsEqualTo(1);
        await Assert.That(detail.Items[0].Title).IsEqualTo("Arrival");
    }

    [Test]
    public async Task DuplicateNames_AreMadeUniquePerUser_AndDeleteCascadesItems()
    {
        await using TestDatabase db = await TestDatabase.CreateAsync();
        WatchlistService lists = Create(db);

        WatchlistDetail first = (await lists.CreateAsync(1, "Favourites"))!;
        WatchlistDetail second = (await lists.CreateAsync(1, "Favourites"))!;
        WatchlistDetail otherUser = (await lists.CreateAsync(2, "Favourites"))!;

        await Assert.That(first.Name).IsEqualTo("Favourites");
        await Assert.That(second.Name).IsEqualTo("Favourites (2)");
        await Assert.That(otherUser.Name).IsEqualTo("Favourites");

        await lists.AddItemAsync(1, first.Id, new WatchlistItemRef(
            WatchActivityServices.Tv, new MediaDomain("movie"), MediaItemKeys.Movie(7), "Movie"));

        await Assert.That(await lists.DeleteAsync(1, first.Id)).IsTrue();
        await Assert.That(await lists.GetAsync(1, first.Id)).IsNull();

        using Stackarr.Core.StackarrDbContext context = db.Factory.CreateDbContext();
        await Assert.That(context.WatchlistItems.Count()).IsEqualTo(0);
    }

    [Test]
    public async Task Changed_RaisesForEveryListAndItemMutation_SoShelvesRefresh()
    {
        await using TestDatabase db = await TestDatabase.CreateAsync();
        WatchlistService lists = Create(db);
        int changes = 0;
        lists.Changed += () => changes++;

        WatchlistDetail list = (await lists.CreateAsync(1, "Later"))!;
        WatchlistItemRef item = new(
            WatchActivityServices.Tv, new MediaDomain("movie"), MediaItemKeys.Movie(11), "Movie");

        await lists.AddItemAsync(1, list.Id, item);
        await lists.RemoveItemAsync(1, list.Id, item.ItemKey);
        await lists.RenameAsync(1, list.Id, "Renamed");
        await lists.DeleteAsync(1, list.Id);

        await Assert.That(changes).IsEqualTo(5);
    }

    [Test]
    public async Task Create_StopsAtThePerUserLimit()
    {
        await using TestDatabase db = await TestDatabase.CreateAsync();
        WatchlistService lists = Create(db);

        for (int i = 0; i < WatchlistLimits.MaxListsPerUser; i++)
            await Assert.That(await lists.CreateAsync(1, $"List {i}")).IsNotNull();

        WatchlistDetail? tooMany = await lists.CreateAsync(1, "One too many");
        await Assert.That(tooMany).IsNull();
        await Assert.That((await lists.GetForUserAsync(1)).Count).IsEqualTo(WatchlistLimits.MaxListsPerUser);

        WatchlistDetail? otherUser = await lists.CreateAsync(2, "Another user's list");
        await Assert.That(otherUser).IsNotNull();
    }

    [Test]
    public async Task Names_AreCappedAtThirtyCharacters_EvenWhenMadeUnique()
    {
        await using TestDatabase db = await TestDatabase.CreateAsync();
        WatchlistService lists = Create(db);
        string longName = new('a', WatchlistLimits.MaxNameLength + 10);

        WatchlistDetail first = (await lists.CreateAsync(1, longName))!;
        WatchlistDetail second = (await lists.CreateAsync(1, longName))!;
        WatchlistDetail? renamed = await lists.RenameAsync(1, first.Id, longName + " renamed");

        await Assert.That(first.Name.Length).IsEqualTo(WatchlistLimits.MaxNameLength);
        await Assert.That(first.Name).IsEqualTo(new string('a', WatchlistLimits.MaxNameLength));
        await Assert.That(second.Name.Length).IsEqualTo(WatchlistLimits.MaxNameLength);
        await Assert.That(second.Name.EndsWith(" (2)", StringComparison.Ordinal)).IsTrue();
        await Assert.That(renamed).IsNotNull();
        await Assert.That(renamed!.Name.Length).IsEqualTo(WatchlistLimits.MaxNameLength);
    }

    [Test]
    public async Task RenameKeepsTheSameListAndItems()
    {
        await using TestDatabase db = await TestDatabase.CreateAsync();
        WatchlistService lists = Create(db);

        WatchlistDetail list = (await lists.CreateAsync(1, "Old name"))!;
        await lists.AddItemAsync(1, list.Id, new WatchlistItemRef(
            WatchActivityServices.Tv, new MediaDomain("movie"), MediaItemKeys.Movie(9), "Movie"));

        WatchlistDetail? renamed = await lists.RenameAsync(1, list.Id, "New name");
        await Assert.That(renamed).IsNotNull();
        await Assert.That(renamed!.Name).IsEqualTo("New name");
        await Assert.That(renamed.Items.Count).IsEqualTo(1);
    }
}
