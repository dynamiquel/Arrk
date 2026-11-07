using Arrk.Notifications;

namespace Arrk.Notifications.Tests;

/// <summary>
/// Exercises the per-channel delivery exclude filter. Unlike the global topic toggles, this only
/// suppresses transport delivery, so the matcher must mirror <see cref="TopicFilter"/> exactly.
/// </summary>
public class NotificationFilterTests
{
    [Test]
    public async Task EmptyOrBlankList_ExcludesNothing()
    {
        await Assert.That(NotificationFilter.IsExcluded([], "system.health.network.speedtest.completed")).IsFalse();
        await Assert.That(NotificationFilter.IsExcluded(null, "system.health.network.speedtest.completed")).IsFalse();
        await Assert.That(NotificationFilter.IsExcluded(["", "   "], "downloads.completed")).IsFalse();
    }

    [Test]
    public async Task ExactTopic_IsExcluded()
    {
        await Assert.That(NotificationFilter.IsExcluded(
            ["system.health.network.speedtest.completed"],
            "system.health.network.speedtest.completed")).IsTrue();
    }

    [Test]
    public async Task PrefixAndWildcards_AreExcluded()
    {
        await Assert.That(NotificationFilter.IsExcluded(
            ["system.health.network.speedtest"],
            "system.health.network.speedtest.completed")).IsTrue();
        await Assert.That(NotificationFilter.IsExcluded(
            ["system.health.*"],
            "system.health.network.speedtest.vpn.completed")).IsTrue();
        await Assert.That(NotificationFilter.IsExcluded(
            ["system.health.network.+"],
            "system.health.network.speedtest")).IsTrue();
    }

    [Test]
    public async Task NonMatchingTopic_IsDelivered()
    {
        await Assert.That(NotificationFilter.IsExcluded(
            ["system.health.network.speedtest.completed"],
            "system.health.network.speedtest.degraded")).IsFalse();
    }

    [Test]
    public async Task Matching_IsCaseInsensitive()
    {
        await Assert.That(NotificationFilter.IsExcluded(
            ["System.Health.Network.Speedtest.Completed"],
            "system.health.network.speedtest.completed")).IsTrue();
    }
}
