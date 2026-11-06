using Microsoft.Extensions.Logging.Abstractions;
using Arrk.Core.Events;
using Arrk.Core.Models;
using Arrk.Core.Notifications;
using Arrk.Subtitles;
using Arrk.TestSupport;

namespace Arrk.Subtitles.Tests;

public class SubtitleDownloadedNotifierTests
{
    [Test]
    public async Task SubtitleDownload_PublishesTheMediaNotification()
    {
        EventBus bus = new();
        RecordingNotificationService notifications = new();
        SubtitleDownloadedNotifier notifier = new(
            bus,
            notifications,
            NullLogger<SubtitleDownloadedNotifier>.Instance);

        await notifier.StartAsync(CancellationToken.None);
        try
        {
            await bus.Publish(new SubtitleDownloadedEvent(
                "bazarr",
                "fr",
                "Love, Rosie",
                "2014",
                MovieId: 15));
        }
        finally
        {
            await notifier.StopAsync(CancellationToken.None);
        }

        await Assert.That(notifications.Messages).HasCount(1);
        NotificationMessage message = notifications.Messages[0];
        await Assert.That(message.Key).IsEqualTo(SubtitleNotificationTopics.SubtitleDownloaded);
        await Assert.That(message.Title).IsEqualTo("Subtitle downloaded");
        await Assert.That(message.Body).Contains("French");
        await Assert.That(message.Body).Contains("Love, Rosie");
        await Assert.That(message.Fields.Select(field => field.Name)).Contains("Provider");
    }

}
