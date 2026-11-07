using Arrk.Core.Configuration;
using Arrk.Notifications;

namespace Arrk.Notifications.Tests;

public class NotificationPolicyTests
{
    static NotificationConfig Config(params (string Topic, bool Enabled)[] topics)
    {
        NotificationConfig config = new();
        foreach ((string topic, bool enabled) in topics)
            config.Topics[topic] = enabled;
        return config;
    }

    [Test]
    public async Task UnknownTopic_IsDisabled()
    {
        await Assert.That(NotificationPolicy.IsEnabled(Config(), "not_a_real_event", TestCatalog.Registry())).IsFalse();
    }

    [Test]
    public async Task DefaultEnabledTopic_WithNoToggle_IsEnabled()
    {
        await Assert.That(NotificationPolicy.IsEnabled(Config(), TestNotificationTopics.Info, TestCatalog.Registry())).IsTrue();
    }

    [Test]
    public async Task DefaultDisabledTopic_WithNoToggle_IsDisabled()
    {
        await Assert.That(NotificationPolicy.IsEnabled(Config(), TestNotificationTopics.Noisy, TestCatalog.Registry())).IsFalse();
    }

    [Test]
    public async Task DisablingAGroup_DisablesEveryEventUnderIt()
    {
        NotificationConfig config = Config(("test.group", false));
        NotificationEventRegistry registry = TestCatalog.Registry();

        await Assert.That(NotificationPolicy.IsEnabled(config, TestNotificationTopics.GroupAlpha, registry)).IsFalse();
        await Assert.That(NotificationPolicy.IsEnabled(config, TestNotificationTopics.GroupBeta, registry)).IsFalse();
        await Assert.That(NotificationPolicy.IsEnabled(config, TestNotificationTopics.GroupNoisy, registry)).IsFalse();
        // Other branches unaffected.
        await Assert.That(NotificationPolicy.IsEnabled(config, TestNotificationTopics.Info, registry)).IsTrue();
    }

    [Test]
    public async Task DisablingADomain_DisablesEveryEventUnderIt()
    {
        NotificationConfig config = Config(("downloads", false));
        NotificationEventRegistry registry = TestCatalog.Registry();

        await Assert.That(NotificationPolicy.IsEnabled(config, DownloadNotificationTopics.TorrentAdded, registry)).IsFalse();
        await Assert.That(NotificationPolicy.IsEnabled(config, DownloadNotificationTopics.DownloadCompleted, registry)).IsFalse();
        await Assert.That(NotificationPolicy.IsEnabled(config, TestNotificationTopics.Info, registry)).IsTrue();
    }

    [Test]
    public async Task ExplicitFalse_OverridesDefaultEnabled()
    {
        NotificationConfig config = Config((TestNotificationTopics.Success, false));

        await Assert.That(NotificationPolicy.IsEnabled(config, TestNotificationTopics.Success, TestCatalog.Registry())).IsFalse();
    }

    [Test]
    public async Task ExplicitTrue_OverridesDefaultDisabled()
    {
        NotificationConfig config = Config((TestNotificationTopics.Noisy, true));

        await Assert.That(NotificationPolicy.IsEnabled(config, TestNotificationTopics.Noisy, TestCatalog.Registry())).IsTrue();
    }

    [Test]
    public async Task MoreSpecificToggle_OverridesBroaderGroup()
    {
        NotificationConfig config = Config(
            ("test", false),
            (TestNotificationTopics.Success, true));
        NotificationEventRegistry registry = TestCatalog.Registry();

        await Assert.That(NotificationPolicy.IsEnabled(config, TestNotificationTopics.Success, registry)).IsTrue();
        await Assert.That(NotificationPolicy.IsEnabled(config, TestNotificationTopics.Info, registry)).IsFalse();
    }

    [Test]
    public async Task LongestPrefixWins_AcrossDomainAndGroup()
    {
        NotificationConfig config = Config(
            ("downloads", false),
            ("downloads.torrent", true));
        NotificationEventRegistry registry = TestCatalog.Registry();

        await Assert.That(NotificationPolicy.IsEnabled(config, DownloadNotificationTopics.TorrentAdded, registry)).IsTrue();
        await Assert.That(NotificationPolicy.IsEnabled(config, DownloadNotificationTopics.DownloadCompleted, registry)).IsFalse();
        await Assert.That(NotificationPolicy.IsEnabled(config, DownloadNotificationTopics.DownloadProgress, registry)).IsFalse();
    }

    [Test]
    public async Task UnrelatedPrefix_DoesNotMatch()
    {
        NotificationConfig config = Config(("test", false));

        await Assert.That(NotificationPolicy.IsEnabled(config, DownloadNotificationTopics.DownloadCompleted, TestCatalog.Registry())).IsTrue();
    }

    [Test]
    public async Task HashWildcardToggle_DisablesWholeDomain()
    {
        NotificationConfig config = Config(("downloads.#", false));
        NotificationEventRegistry registry = TestCatalog.Registry();

        await Assert.That(NotificationPolicy.IsEnabled(config, DownloadNotificationTopics.TorrentAdded, registry)).IsFalse();
        await Assert.That(NotificationPolicy.IsEnabled(config, DownloadNotificationTopics.DownloadCompleted, registry)).IsFalse();
        await Assert.That(NotificationPolicy.IsEnabled(config, TestNotificationTopics.Info, registry)).IsTrue();
    }

    [Test]
    public async Task StarWildcardToggle_DisablesWholeDomain()
    {
        NotificationConfig config = Config(("downloads.*", false));
        NotificationEventRegistry registry = TestCatalog.Registry();

        await Assert.That(NotificationPolicy.IsEnabled(config, DownloadNotificationTopics.TorrentAdded, registry)).IsFalse();
        await Assert.That(NotificationPolicy.IsEnabled(config, DownloadNotificationTopics.DownloadCompleted, registry)).IsFalse();
    }

    [Test]
    public async Task PlusWildcardToggle_MatchesOnlyOneLevel()
    {
        NotificationConfig config = Config(("downloads.+", false));
        NotificationEventRegistry registry = TestCatalog.Registry();

        await Assert.That(NotificationPolicy.IsEnabled(config, DownloadNotificationTopics.DownloadCompleted, registry)).IsFalse();
        // downloads.torrent.added is deeper than one level, so its catalogue default (on) still applies.
        await Assert.That(NotificationPolicy.IsEnabled(config, DownloadNotificationTopics.TorrentAdded, registry)).IsTrue();
    }

    [Test]
    public async Task MoreSpecificWildcardToggle_OverridesBroaderWildcard()
    {
        NotificationConfig config = Config(
            ("downloads.#", false),
            ("downloads.torrent.#", true));
        NotificationEventRegistry registry = TestCatalog.Registry();

        await Assert.That(NotificationPolicy.IsEnabled(config, DownloadNotificationTopics.TorrentAdded, registry)).IsTrue();
        await Assert.That(NotificationPolicy.IsEnabled(config, DownloadNotificationTopics.DownloadCompleted, registry)).IsFalse();
    }

    [Test]
    public async Task BlankToggleKey_IsIgnored()
    {
        NotificationConfig config = Config(("", false));

        await Assert.That(NotificationPolicy.IsEnabled(config, DownloadNotificationTopics.DownloadCompleted, TestCatalog.Registry())).IsTrue();
    }

    [Test]
    public async Task Matches_RequiresSegmentBoundary()
    {
        await Assert.That(NotificationPolicy.Matches("test.group", "test.group.alpha")).IsTrue();
        await Assert.That(NotificationPolicy.Matches("test.group.alpha", "test.group.alpha")).IsTrue();
        await Assert.That(NotificationPolicy.Matches("test.gro", "test.group.alpha")).IsFalse();
        await Assert.That(NotificationPolicy.Matches("downloads", "downloads2.torrent.added")).IsFalse();
    }
}
