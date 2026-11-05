using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Stackarr.Core.Events;

namespace Stackarr.Requests;

/// <summary>
/// Listens for <see cref="MediaRemovedEvent"/> and cancels any approved request for a title that
/// was deleted, so it can be requested again. Subscribed at startup so it is always active.
/// </summary>
public sealed class RequestMediaRemovalListener(
    IEventBus bus,
    IServiceScopeFactory scopeFactory,
    ILogger<RequestMediaRemovalListener> logger) : IHostedService
{
    IDisposable? _subscription;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _subscription = bus.Subscribe<MediaRemovedEvent>((removed, ct) => HandleAsync(removed, ct));
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _subscription?.Dispose();
        _subscription = null;
        return Task.CompletedTask;
    }

    async ValueTask HandleAsync(MediaRemovedEvent removed, CancellationToken ct)
    {
        try
        {
            using IServiceScope scope = scopeFactory.CreateScope();
            RequestService requests = scope.ServiceProvider.GetRequiredService<RequestService>();
            int cancelled = await requests.CancelForMediaAsync(removed.Domain, removed.ProviderIds, ct);
            if (cancelled > 0)
                logger.LogInformation("Cancelled {Count} request(s) for removed media '{Title}'", cancelled, removed.Title);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to reconcile requests after removing '{Title}'", removed.Title);
        }
    }
}
