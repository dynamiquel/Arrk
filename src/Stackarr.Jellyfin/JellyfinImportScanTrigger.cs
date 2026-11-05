using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Stackarr.Core.Events;
using Stackarr.Core.Services;
using Stackarr.Subtitles.Abstractions;

namespace Stackarr.Jellyfin;

/// <summary>
/// Scans Jellyfin immediately when an arr reports an import or a subtitle backend downloads a
/// track, so new media and sidecar subtitles become visible without waiting for the scheduled scan.
/// </summary>
public sealed class JellyfinImportScanTrigger(
    IEventBus bus,
    JellyfinClient client,
    ServiceRegistry serviceRegistry,
    ILogger<JellyfinImportScanTrigger> logger) : BackgroundService
{
    readonly List<IDisposable> _subscriptions = [];

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _subscriptions.Add(bus.Subscribe<MediaImportedEvent>(async (_, ct) => await TriggerAsync(ct)));
        _subscriptions.Add(bus.Subscribe<SubtitleDownloadedEvent>(async (_, ct) => await TriggerAsync(ct)));
        return Task.CompletedTask;
    }

    async ValueTask TriggerAsync(CancellationToken ct)
    {
        Service? service = serviceRegistry.Get(JellyfinServiceDefinition.StaticKey);
        if (service?.State.IsHealthy != true)
            return;

        try
        {
            await client.TriggerLibraryRefreshAsync(ct);
            logger.LogInformation("Triggered a Jellyfin library scan after a media or subtitle change");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not trigger a Jellyfin library scan after a media or subtitle change");
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        foreach (IDisposable subscription in _subscriptions)
            subscription.Dispose();
        _subscriptions.Clear();
        await base.StopAsync(cancellationToken);
    }
}
