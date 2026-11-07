using Arrk.Notifications;
using Arrk.Core.Services;

namespace Arrk.Core.Tests;

/// <summary>
/// Specifies which live activity reaches the inbox: background jobs are opt-in (off by default) because
/// recurring jobs would otherwise keep a running entry there all day, while user-visible operations
/// (searches, imports, syncs, container actions) always show.
/// </summary>
public class LiveActivityPolicyTests
{
    [Test]
    public async Task JobActivity_IsHiddenByDefault()
    {
        await Assert.That(LiveActivityPolicy.ShouldPublish(LiveActivityKind.Job, new NotificationConfig())).IsFalse();
    }

    [Test]
    public async Task JobActivity_IsShownWhenEnabled()
    {
        NotificationConfig config = new() { ShowJobActivity = true };

        await Assert.That(LiveActivityPolicy.ShouldPublish(LiveActivityKind.Job, config)).IsTrue();
    }

    [Test]
    public async Task OtherActivity_IsAlwaysShown()
    {
        NotificationConfig config = new();

        await Assert.That(LiveActivityPolicy.ShouldPublish(LiveActivityKind.Search, config)).IsTrue();
        await Assert.That(LiveActivityPolicy.ShouldPublish(LiveActivityKind.Import, config)).IsTrue();
        await Assert.That(LiveActivityPolicy.ShouldPublish(LiveActivityKind.Sync, config)).IsTrue();
        await Assert.That(LiveActivityPolicy.ShouldPublish(LiveActivityKind.Container, config)).IsTrue();
        await Assert.That(LiveActivityPolicy.ShouldPublish(LiveActivityKind.Warning, config)).IsTrue();
    }
}
