using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MudBlazor;
using Arrk.Core.Configuration;
using Arrk.Core.Services;
using Arrk.Ui;

namespace Arrk.App.Services;

/// <summary>
/// Mirrors the module-level <see cref="LiveActivityRegistry"/> into the app's <see cref="ActivityService"/>
/// (the activity centre). Each in-flight operation becomes one live entry, updated as its detail
/// changes and dismissed the moment it finishes, so searches, imports, playlist syncs and container
/// actions show up here and clear themselves. Background job runs are opt-in via
/// <see cref="NotificationConfig.ShowJobActivity"/> because recurring jobs are noisy.
/// </summary>
public sealed class LiveActivityBridge(
    LiveActivityRegistry registry,
    ActivityService activity,
    IOptionsMonitor<NotificationConfig> config,
    ILogger<LiveActivityBridge> logger) : BackgroundService
{
    static readonly TimeSpan Interval = TimeSpan.FromSeconds(1.5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Dictionary<Guid, Tracked> live = [];
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                NotificationConfig current = config.CurrentValue;
                List<LiveActivityEntry> active = [.. registry.Active
                    .Where(entry => LiveActivityPolicy.ShouldPublish(entry.Kind, current))];
                HashSet<Guid> running = [.. active.Select(e => e.Id)];

                foreach (LiveActivityEntry entry in active)
                {
                    if (!live.TryGetValue(entry.Id, out Tracked? tracked))
                    {
                        ActivityHandle handle = activity.Begin(entry.Title, entry.Detail, Icon(entry.Kind));
                        live[entry.Id] = new Tracked(handle, entry.Detail);
                    }
                    else if (tracked.Detail != entry.Detail)
                    {
                        tracked.Handle.Report(null, entry.Detail);
                        live[entry.Id] = tracked with { Detail = entry.Detail };
                    }
                }

                foreach (Guid id in live.Keys.Where(id => !running.Contains(id)).ToList())
                {
                    live[id].Handle.Dismiss();
                    live.Remove(id);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Could not update the live activity bridge");
            }

            try { await Task.Delay(Interval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    static string Icon(LiveActivityKind kind) => kind switch
    {
        LiveActivityKind.Search => Icons.Material.Filled.Search,
        LiveActivityKind.Import => Icons.Material.Filled.Download,
        LiveActivityKind.Sync => Icons.Material.Filled.Sync,
        LiveActivityKind.Container => Icons.Material.Filled.Dns,
        LiveActivityKind.Warning => Icons.Material.Filled.CloudOff,
        LiveActivityKind.Job => Icons.Material.Filled.Schedule,
        _ => Icons.Material.Filled.Autorenew
    };

    sealed record Tracked(ActivityHandle Handle, string? Detail);
}
