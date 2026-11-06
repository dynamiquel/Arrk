using Arrk.Ui;

namespace Arrk.Ui.Tests;

/// <summary>
/// Covers the live activity tracking: a transient operation can be dismissed so it disappears the moment
/// it finishes (used by the per-search music acquisition activity), rather than lingering as history.
/// </summary>
public class ActivityServiceTests
{
    [Test]
    public async Task Dismiss_RemovesTheItem_InsteadOfLeavingItCompleted()
    {
        ActivityService activity = new();
        ActivityHandle handle = activity.Begin("Searching \"Song\"");

        await Assert.That(activity.ActiveCount).IsEqualTo(1);

        handle.Dismiss();

        await Assert.That(activity.ActiveCount).IsEqualTo(0);
        await Assert.That(activity.Items).IsEmpty();
    }

    [Test]
    public async Task Complete_KeepsTheItemAsHistory()
    {
        ActivityService activity = new();
        ActivityHandle handle = activity.Begin("Add album");

        handle.Complete();

        await Assert.That(activity.ActiveCount).IsEqualTo(0);
        await Assert.That(activity.Items).HasCount(1);
    }
}
