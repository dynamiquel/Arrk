using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Stackarr.App.Services;
using Stackarr.Core.Configuration;
using Stackarr.Core.Services;
using Stackarr.Ui;

namespace Stackarr.App.Tests;

/// <summary>
/// Covers the bridge between the module-level live activity registry and the inbox activity centre:
/// background jobs stay hidden unless <see cref="NotificationConfig.ShowJobActivity"/> is enabled,
/// while user-visible operations keep flowing.
/// </summary>
public class LiveActivityBridgeTests
{
    sealed class Monitor(NotificationConfig value) : IOptionsMonitor<NotificationConfig>
    {
        public NotificationConfig CurrentValue { get; } = value;

        public NotificationConfig Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<NotificationConfig, string?> listener) => null;
    }

    static async Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
                return true;
            await Task.Delay(100);
        }

        return condition();
    }

    [Test]
    public async Task JobActivity_IsHiddenByDefault_ButOtherActivityIsShown()
    {
        LiveActivityRegistry registry = new();
        ActivityService activity = new();
        LiveActivityBridge bridge = new(
            registry, activity, new Monitor(new NotificationConfig()),
            NullLogger<LiveActivityBridge>.Instance);

        using LiveActivityScope job = registry.Begin(LiveActivityKind.Job, "Running \"Speed test\"");
        using LiveActivityScope search = registry.Begin(LiveActivityKind.Search, "Searching \"Song\"");

        await bridge.StartAsync(CancellationToken.None);
        try
        {
            await WaitUntilAsync(
                () => activity.Items.Any(i => i.Title.Contains("Searching")),
                TimeSpan.FromSeconds(5));

            await Assert.That(activity.Items.Any(i => i.Title.Contains("Searching"))).IsTrue();
            await Assert.That(activity.Items.Any(i => i.Title.Contains("Running"))).IsFalse();
        }
        finally
        {
            await bridge.StopAsync(CancellationToken.None);
            bridge.Dispose();
        }
    }

    [Test]
    public async Task JobActivity_IsShownWhenEnabled()
    {
        LiveActivityRegistry registry = new();
        ActivityService activity = new();
        LiveActivityBridge bridge = new(
            registry, activity, new Monitor(new NotificationConfig { ShowJobActivity = true }),
            NullLogger<LiveActivityBridge>.Instance);

        using LiveActivityScope job = registry.Begin(LiveActivityKind.Job, "Running \"Speed test\"");

        await bridge.StartAsync(CancellationToken.None);
        try
        {
            await WaitUntilAsync(
                () => activity.Items.Any(i => i.Title.Contains("Running")),
                TimeSpan.FromSeconds(5));

            await Assert.That(activity.Items.Any(i => i.Title.Contains("Running"))).IsTrue();
        }
        finally
        {
            await bridge.StopAsync(CancellationToken.None);
            bridge.Dispose();
        }
    }
}
