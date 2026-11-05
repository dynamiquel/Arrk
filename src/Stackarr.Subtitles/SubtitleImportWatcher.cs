using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Stackarr.Core.Events;

namespace Stackarr.Subtitles;

/// <summary>Requests missing subtitles as soon as the TV module reports a completed import.</summary>
public sealed class SubtitleImportWatcher(
    IEventBus bus,
    SubtitleAutoDownloader downloader,
    ILogger<SubtitleImportWatcher> logger) : BackgroundService
{
    IDisposable? _subscription;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _subscription = bus.Subscribe<MediaImportedEvent>(async (evt, ct) =>
        {
            try
            {
                await downloader.HandleImportAsync(evt, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Shutting down.
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Subtitle import watcher failed");
            }
        });

        return Task.CompletedTask;
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        _subscription?.Dispose();
        _subscription = null;
        return base.StopAsync(cancellationToken);
    }
}
