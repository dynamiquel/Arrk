using Arrk.Notifications;

namespace Arrk.Core.Services;

/// <summary>
/// Decides which live-activity entries are surfaced in the activity centre and notification inbox.
/// Background jobs are opt-in because recurring jobs would otherwise keep a running entry in the
/// inbox all day; searches, imports, syncs and container actions are always shown.
/// </summary>
public static class LiveActivityPolicy
{
    public static bool ShouldPublish(LiveActivityKind kind, NotificationConfig config) =>
        kind is not LiveActivityKind.Job || config.ShowJobActivity;
}
