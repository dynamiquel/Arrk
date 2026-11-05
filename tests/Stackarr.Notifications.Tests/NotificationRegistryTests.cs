using Stackarr.Core.Notifications;

namespace Stackarr.Notifications.Tests;

public class NotificationRegistryTests
{
    static NotificationEventRegistry Registry() => TestCatalog.Registry();

    [Test]
    public async Task Registry_AggregatesProviders()
    {
        NotificationEventRegistry registry = Registry();

        await Assert.That(registry.IsKnown(TestNotificationTopics.Info)).IsTrue();
        await Assert.That(registry.IsKnown(DownloadNotificationTopics.DownloadCompleted)).IsTrue();
        await Assert.That(registry.IsKnown("nope")).IsFalse();
    }

    [Test]
    public async Task Registry_RejectsDuplicateTopics()
    {
        await Assert.That(() => new NotificationEventRegistry(
            [new TestNotificationEvents(), new TestNotificationEvents()]))
            .Throws<InvalidOperationException>();
    }

    [Test]
    public async Task AllTopics_UseDottedSyntax()
    {
        foreach (NotificationEventDefinition definition in Registry().Events)
        {
            string topic = definition.Topic;
            await Assert.That(topic.Contains('.')).IsTrue();
            await Assert.That(topic).DoesNotStartWith(".");
            await Assert.That(topic).DoesNotEndWith(".");
            await Assert.That(topic.Contains("..")).IsFalse();
            await Assert.That(topic).IsEqualTo(topic.ToLowerInvariant());
        }
    }

    [Test]
    public async Task EveryTopic_ResolvesGroupAndDomainAsPrefixes()
    {
        NotificationEventRegistry registry = Registry();
        foreach (NotificationEventDefinition definition in registry.Events)
        {
            await Assert.That(definition.Topic.StartsWith(definition.Group)).IsTrue();
            await Assert.That(definition.Topic.StartsWith(definition.Domain)).IsTrue();
            await Assert.That(registry.GroupOf(definition.Topic)).IsEqualTo(definition.Group);
            await Assert.That(registry.DomainOf(definition.Topic)).IsEqualTo(definition.Domain);
        }
    }

    [Test]
    public async Task EveryCuratedGroup_HasAtLeastOneEvent()
    {
        NotificationEventRegistry registry = Registry();
        foreach (NotificationGroupDefinition group in registry.Groups)
        {
            await Assert.That(registry.InGroup(group.Topic)).IsNotEmpty();
        }
    }

    [Test]
    public async Task InGroup_ReturnsOnlyMatchingTopics()
    {
        IReadOnlyList<NotificationEventDefinition> testGroup = Registry().InGroup("test.group");

        await Assert.That(testGroup).IsNotEmpty();
        await Assert.That(testGroup.All(e => e.Topic.StartsWith("test.group"))).IsTrue();
        await Assert.That(testGroup.Any(e => e.Topic == TestNotificationTopics.GroupAlpha)).IsTrue();
        await Assert.That(testGroup.Any(e => e.Topic == TestNotificationTopics.Info)).IsFalse();
    }

    [Test]
    public async Task CoreDownloadProvider_RespectsDefaultFlags()
    {
        NotificationEventRegistry registry = Registry();

        await Assert.That(registry.DefaultEnabled(DownloadNotificationTopics.DownloadCompleted)).IsTrue();
        await Assert.That(registry.DefaultEnabled(DownloadNotificationTopics.TorrentStriked)).IsTrue();
        await Assert.That(registry.DefaultEnabled(DownloadNotificationTopics.DownloadProgress)).IsFalse();
        await Assert.That(registry.DefaultEnabled(DownloadNotificationTopics.DownloadPaused)).IsFalse();
    }

    [Test]
    public async Task StyleFor_EveryKnownTopic_HasEmojiAndColour()
    {
        NotificationEventRegistry registry = Registry();
        foreach (NotificationEventDefinition definition in registry.Events)
        {
            NotificationStyle style = registry.StyleFor(definition.Topic);
            await Assert.That(style.Emoji).IsNotNull();
            await Assert.That(style.Emoji).IsNotEmpty();
            await Assert.That(style.Color).IsNotNull();
        }
    }

}
