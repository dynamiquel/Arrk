using Arrk.Core.Events;
using Arrk.Notifications;
using Arrk.Notifications.Discord;
using Arrk.TestSupport;

namespace Arrk.Notifications.Tests;

/// <summary>
/// Live smoke test that posts a real embed to the Discord webhook in
/// <c>ARRK_DISCORD_WEBHOOK</c>. The test returns early when the variable is unset or
/// <c>ARRK_SKIP_LIVE_TESTS=1</c>. Network-level failures are treated as skips so offline runs
/// stay green; an HTTP error from Discord itself is a failure.
/// </summary>
public class LiveDiscordSmokeTests
{
    static string? WebhookUrl() =>
        Environment.GetEnvironmentVariable("ARRK_DISCORD_WEBHOOK");

    static bool Skip() =>
        Environment.GetEnvironmentVariable("ARRK_SKIP_LIVE_TESTS") == "1"
        || string.IsNullOrWhiteSpace(WebhookUrl());

    [Test]
    public async Task Discord_RealWebhook_AcceptsEmbed()
    {
        if (Skip())
            return;

        DiscordNotificationConfig config = new()
        {
            Enabled = true,
            Routing = [new() { Url = WebhookUrl()! }],
            Username = "Arrk"
        };

        await using TestDatabase db = new();
        DiscordNotificationClient channel = TestNotifications.DiscordChannel(db, config);

        NotificationMessage message = new()
        {
            Key = DownloadNotificationTopics.DownloadCompleted,
            Title = "Download completed",
            Body = "The Matrix (1999) finished downloading and is ready to import.",
            Severity = EventSeverity.Success,
            SourceName = "The Matrix",
            Timestamp = DateTime.UtcNow,
            Fields =
            [
                new("Quality", "4K HDR"),
                new("Size", "18.4 GB"),
                new("Source", "Arrk live smoke test")
            ]
        };

        NotificationDeliveryResult result = await channel.SendAsync(message, NotificationRecipient.Everyone).SingleResultAsync();

        // Offline / DNS failure: skip rather than fail the suite.
        if (!result.Success && result.Error is not null && result.Error.Contains("request failed"))
            return;

        await Assert.That(result.Success).IsTrue();
    }
}
