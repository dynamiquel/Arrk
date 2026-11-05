using Stackarr.Core.Notifications;

namespace Stackarr.Notifications.Tests;

/// <summary>
/// Exercises the hierarchical matcher that powers the events page topic filter. Covers the two
/// wildcards (<c>+</c>, <c>#</c>), implicit prefix matching, and case-insensitivity.
/// </summary>
public class TopicFilterTests
{
    [Test]
    public async Task BlankPattern_MatchesEverything()
    {
        await Assert.That(TopicFilter.Matches(null, "downloads.completed")).IsTrue();
        await Assert.That(TopicFilter.Matches("", "downloads.completed")).IsTrue();
        await Assert.That(TopicFilter.Matches("   ", "anything")).IsTrue();
    }

    [Test]
    public async Task BlankTopic_NeverMatchesNonBlankPattern()
    {
        await Assert.That(TopicFilter.Matches("downloads", null)).IsFalse();
        await Assert.That(TopicFilter.Matches("downloads", "")).IsFalse();
    }

    [Test]
    public async Task ExactPattern_MatchesOnlyThatTopic()
    {
        await Assert.That(TopicFilter.Matches("downloads.completed", "downloads.completed")).IsTrue();
        await Assert.That(TopicFilter.Matches("downloads.completed", "downloads.failed")).IsFalse();
    }

    [Test]
    public async Task PlainPattern_ActsAsPrefix()
    {
        await Assert.That(TopicFilter.Matches("downloads", "downloads")).IsTrue();
        await Assert.That(TopicFilter.Matches("downloads", "downloads.completed")).IsTrue();
        await Assert.That(TopicFilter.Matches("downloads", "downloads.torrent.added")).IsTrue();
        await Assert.That(TopicFilter.Matches("downloads", "downloads2.completed")).IsFalse();
        await Assert.That(TopicFilter.Matches("system.health", "system.health.storage.low")).IsTrue();
    }

    [Test]
    public async Task HashWildcard_MatchesAnyDepthIncludingParent()
    {
        await Assert.That(TopicFilter.Matches("downloads.#", "downloads")).IsTrue();
        await Assert.That(TopicFilter.Matches("downloads.#", "downloads.completed")).IsTrue();
        await Assert.That(TopicFilter.Matches("downloads.#", "downloads.torrent.added")).IsTrue();
        await Assert.That(TopicFilter.Matches("system.health.#", "system.health.network.speedtest.completed")).IsTrue();
        await Assert.That(TopicFilter.Matches("downloads.#", "media.added")).IsFalse();
    }

    [Test]
    public async Task PlusWildcard_MatchesExactlyOneLevel()
    {
        await Assert.That(TopicFilter.Matches("downloads.+", "downloads.completed")).IsTrue();
        await Assert.That(TopicFilter.Matches("downloads.+", "downloads.torrent")).IsTrue();
        await Assert.That(TopicFilter.Matches("downloads.+", "downloads.torrent.added")).IsFalse();
        await Assert.That(TopicFilter.Matches("system.health.+", "system.health.storage")).IsTrue();
        await Assert.That(TopicFilter.Matches("system.health.+", "system.health.storage.low")).IsFalse();
    }

    [Test]
    public async Task StarWildcard_IsAnAliasForHash()
    {
        await Assert.That(TopicFilter.Matches("downloads.*", "downloads")).IsTrue();
        await Assert.That(TopicFilter.Matches("downloads.*", "downloads.torrent.added")).IsTrue();
        await Assert.That(TopicFilter.Matches("downloads.*", "media.added")).IsFalse();
        await Assert.That(TopicFilter.Matches("system.health.*", "system.health.network.speedtest.completed")).IsTrue();
    }

    [Test]
    public async Task IsMoreSpecific_OrdersByDepthThenLiteralThenWildcard()
    {
        // Deeper patterns beat shallower ones.
        await Assert.That(TopicFilter.IsMoreSpecific("media.requests.#", "media.#")).IsTrue();
        // More literal levels beat a wildcard at the same depth.
        await Assert.That(TopicFilter.IsMoreSpecific("downloads.torrent", "downloads.#")).IsTrue();
        // One-level beats any-depth.
        await Assert.That(TopicFilter.IsMoreSpecific("downloads.+", "downloads.#")).IsTrue();
        await Assert.That(TopicFilter.IsMoreSpecific("downloads.#", "downloads.torrent")).IsFalse();
    }

    [Test]
    public async Task Matching_IsCaseInsensitive_AndTrims()
    {
        await Assert.That(TopicFilter.Matches("Downloads.Completed", "downloads.completed")).IsTrue();
        await Assert.That(TopicFilter.Matches("  downloads.#  ", "Downloads.Torrent.Added")).IsTrue();
    }

    [Test]
    public async Task HasWildcard_DetectsOnlyPlusAndHash()
    {
        await Assert.That(TopicFilter.HasWildcard("downloads.#")).IsTrue();
        await Assert.That(TopicFilter.HasWildcard("downloads.+")).IsTrue();
        await Assert.That(TopicFilter.HasWildcard("downloads.*")).IsTrue();
        await Assert.That(TopicFilter.HasWildcard("downloads.completed")).IsFalse();
        await Assert.That(TopicFilter.HasWildcard(null)).IsFalse();
    }
}
