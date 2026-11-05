using Stackarr.Notifications.Components.Shared;

namespace Stackarr.Notifications.Tests;

/// <summary>
/// Covers the events feed batching: render 50 rows, reveal another 50 whenever infinite scroll
/// reaches the bottom, and stop at the end of the filtered window.
/// </summary>
public class EventFeedPagingTests
{

    [Test]
    public async Task NextBatch_RevealsAnotherFifty()
    {
        await Assert.That(EventFeedPaging.NextBatch(50, 1000)).IsEqualTo(100);
        await Assert.That(EventFeedPaging.NextBatch(100, 1000)).IsEqualTo(150);
    }

    [Test]
    public async Task NextBatch_StopsAtTheEnd()
    {
        await Assert.That(EventFeedPaging.NextBatch(80, 93)).IsEqualTo(93);
        await Assert.That(EventFeedPaging.NextBatch(93, 93)).IsEqualTo(93);
    }

    [Test]
    public async Task Visible_CapsAtTheTotal()
    {
        await Assert.That(EventFeedPaging.Visible(50, 12)).IsEqualTo(12);
        await Assert.That(EventFeedPaging.Visible(50, 75)).IsEqualTo(50);
    }

    [Test]
    public async Task NextBatch_HandlesAnEmptyFeed()
    {
        await Assert.That(EventFeedPaging.NextBatch(50, 0)).IsEqualTo(0);
        await Assert.That(EventFeedPaging.Visible(50, 0)).IsEqualTo(0);
    }
}
