using System.Globalization;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Stackarr.Core.Events;
using Stackarr.Core.Models;
using Stackarr.Core.Notifications;
using Stackarr.Subtitles.Abstractions;

namespace Stackarr.Subtitles;

/// <summary>Turns subtitle download events into the configured media notification.</summary>
public sealed class SubtitleDownloadedNotifier(
    IEventBus bus,
    INotificationService notifications,
    ILogger<SubtitleDownloadedNotifier> logger) : IHostedService, IDisposable
{
    IDisposable? _subscription;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _subscription = bus.Subscribe<SubtitleDownloadedEvent>(OnDownloadedAsync);
        return Task.CompletedTask;
    }

    async ValueTask OnDownloadedAsync(SubtitleDownloadedEvent evt, CancellationToken ct)
    {
        try
        {
            string language = DisplayName(evt.Language);
            string title = string.IsNullOrWhiteSpace(evt.Title) ? "Media" : evt.Title;
            string body = string.IsNullOrWhiteSpace(evt.Subtitle)
                ? $"{language} subtitle for {title}"
                : $"{language} subtitle for {title} · {evt.Subtitle}";

            await notifications.PublishAsync(new NotificationMessage
            {
                Key = SubtitleNotificationTopics.SubtitleDownloaded,
                Title = "Subtitle downloaded",
                Body = body,
                Severity = EventSeverity.Info,
                Source = evt.Provider,
                SourceName = evt.Title,
                Fields =
                [
                    new NotificationField("Language", language),
                    new NotificationField("Provider", evt.Provider)
                ]
            }, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not publish the subtitle-downloaded notification");
        }
    }

    static string DisplayName(string code)
    {
        CultureInfo? culture = CultureInfo.GetCultures(CultureTypes.NeutralCultures)
            .FirstOrDefault(item => string.Equals(
                item.TwoLetterISOLanguageName, code, StringComparison.OrdinalIgnoreCase));
        return culture is null || string.IsNullOrWhiteSpace(culture.EnglishName)
            ? code.ToUpperInvariant()
            : culture.EnglishName;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _subscription?.Dispose();
        _subscription = null;
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _subscription?.Dispose();
        _subscription = null;
    }
}
