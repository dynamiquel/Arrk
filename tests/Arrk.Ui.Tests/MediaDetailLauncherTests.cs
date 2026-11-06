using Arrk.Ui;

namespace Arrk.Ui.Tests;

/// <summary>
/// Covers the player-to-media-module hand-off: clicking the player title must reach the contributed
/// detail component, and a missing media module must be a harmless no-op.
/// </summary>
public class MediaDetailLauncherTests
{
    [Test]
    public async Task ShowAsync_InvokesTheSubscriber()
    {
        MediaDetailLauncher launcher = new();
        MediaDetailRequest? observed = null;
        launcher.Requested += request =>
        {
            observed = request;
            return Task.CompletedTask;
        };

        await launcher.ShowAsync(new MediaDetailRequest("item1"));

        await Assert.That(observed).IsEqualTo(new MediaDetailRequest("item1"));
        await Assert.That(launcher.IsAvailable).IsTrue();
    }

    [Test]
    public async Task ShowAsync_WithoutASubscriber_IsANoOp()
    {
        MediaDetailLauncher launcher = new();

        await Assert.That(launcher.IsAvailable).IsFalse();
        await launcher.ShowAsync(new MediaDetailRequest("item1"));
    }
}
