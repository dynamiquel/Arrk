using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Stackarr.Core.Events;

namespace Stackarr.Requests;

/// <summary>
/// Listens for imports and marks matching approved requests available. Media modules publish
/// <see cref="MediaImportedEvent"/> after the file is in the library, so Requests can update its
/// own state without the TV module referencing the request UI.
/// </summary>
public sealed class RequestAvailabilityListener(
    IEventBus bus,
    IServiceScopeFactory scopeFactory,
    ILogger<RequestAvailabilityListener> logger) : IHostedService
{
    IDisposable? _subscription;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _subscription = bus.Subscribe<MediaImportedEvent>((imported, ct) => HandleAsync(imported, ct));
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _subscription?.Dispose();
        _subscription = null;
        return Task.CompletedTask;
    }

    async ValueTask HandleAsync(MediaImportedEvent imported, CancellationToken ct)
    {
        if (imported.Entity is null)
            return;

        try
        {
            using IServiceScope scope = scopeFactory.CreateScope();
            RequestService requests = scope.ServiceProvider.GetRequiredService<RequestService>();
            int matched = await requests.MarkAvailableAsync(imported.Entity, ct);
            if (matched > 0)
                logger.LogInformation(
                    "Marked {Count} request(s) available after import of '{Title}'",
                    matched, imported.Entity.Title);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to reconcile requests after import of '{Title}'", imported.Entity.Title);
        }
    }
}
