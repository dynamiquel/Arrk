using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Stackarr.Auth.Abstractions;
using Stackarr.Core.Events;
using Stackarr.Core.Notifications;

namespace Stackarr.Core;

/// <summary>
/// Registers shared services that every Stackarr module can rely on.
/// </summary>
public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddStackarrCore(this IServiceCollection services)
    {
        // Shared clock: modules should use TimeProvider instead of DateTime.UtcNow so
        // time-based logic is testable. Tests can override this with a fake TimeProvider.
        services.AddSingleton(TimeProvider.System);

        // Durable event log. Registered as a concrete type so the notifying decorator can wrap it.
        services.AddSingleton<EfEventStore>();

        // Unified event log used by every subsystem for user-visible activity. The decorator
        // projects any attached notification into the notification pipeline after persistence.
        services.AddSingleton<IEventStore>(sp => new NotifyingEventStore(
            sp.GetRequiredService<EfEventStore>(),
            sp.GetServices<INotificationService>(),
            sp.GetService<ILogger<NotifyingEventStore>>()));

        // Notification catalogue. Modules contribute their own event definitions.
        services.AddSingleton<NotificationEventRegistry>();

        // Per-user entity subscriptions and per-transport consent. Shared by every delivery
        // transport; the dispatcher resolves subscribers before a channel sees the message.
        services.AddSingleton<NotificationSubscriptionService>();
        services.AddSingleton<UserNotificationChannelService>();
        services.AddSingleton<NotificationStackInfo>();

        // Watch-state facade. Always resolvable; player modules contribute IWatchStateProvider
        // implementations (Jellyfin today). With none registered it simply returns empty.
        services.AddSingleton<Stackarr.Core.Playback.WatchStateService>();

        // Durable play history and per-user watchlists. Both live in Core so TV, music, the
        // lifecycle module and third-party plugins share one model.
        services.AddSingleton<Stackarr.Core.Playback.WatchActivityService>();
        services.AddSingleton<Stackarr.Core.Playback.WatchlistService>();

        // Staleness facade. The lifecycle module contributes the evaluator; with it absent the
        // UI simply reports that staleness tracking is unavailable.
        services.AddSingleton<Stackarr.Core.Lifecycle.StaleMediaService>();

        // Admin "never stale" pins, shared by every module and plugin.
        services.AddSingleton<Stackarr.Core.Lifecycle.MediaPinService>();

        // Process-wide registry of in-flight operations (searches, imports, playlist syncs,
        // container lifecycle). Modules publish here; the app mirrors it into the activity centre.
        services.AddSingleton<Stackarr.Core.Services.LiveActivityRegistry>();

        // Mirrors background job runs into the live activity centre and the event log.
        services.AddSingleton<Stackarr.Core.Services.JobTelemetry>();

        // Durable key/value settings shared by auth, UI preferences and generated secrets.
        services.AddSingleton<Stackarr.Core.Services.SettingsService>();

        // Per-account presentation documents (dashboard layout YAML and custom CSS).
        services.AddSingleton<Stackarr.Core.Services.UserUiPreferenceService>();

        return services;
    }

}
