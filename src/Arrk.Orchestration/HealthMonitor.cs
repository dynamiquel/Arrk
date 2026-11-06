using System.Collections.Concurrent;
using Docker.DotNet.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Arrk.Core;
using Arrk.Core.Events;
using Arrk.Core.Models;
using Arrk.Core.Notifications;
using Arrk.Docker;
using ContainerState = Arrk.Core.Enums.ContainerState;

namespace Arrk.Orchestration;

public class HealthMonitor(
    ContainerManager containerManager,
    ResourceInventory inventory,
    IDbContextFactory<ArrkDbContext> dbContextFactory,
    ILogger<HealthMonitor> logger,
    TimeSpan? checkInterval = null,
    TimeSpan? retryDelay = null,
    int maxRetries = 3,
    IEventStore? eventStore = null)
    : BackgroundService
{
    readonly TimeSpan _checkInterval = checkInterval ?? TimeSpan.FromSeconds(10);
    readonly TimeSpan _retryDelay = retryDelay ?? TimeSpan.FromSeconds(5);

    readonly ConcurrentDictionary<string, int> _consecutiveFailures = new();

    public event Action<string, ContainerState>? HealthChanged;

    public ConcurrentDictionary<string, ContainerState> LastContainerState { get; } = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Health monitor started (interval: {Interval})", _checkInterval);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CheckAllHealth(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error during health check cycle");
            }

            try
            {
                await Task.Delay(_checkInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        logger.LogInformation("Health monitor stopped");
    }

    public async Task CheckAllHealth(CancellationToken ct = default)
    {
        List<ContainerInfo> containers = await inventory.GetOwnedContainers(ct);
        logger.LogDebug("Checking health of {Count} owned containers", containers.Count);

        foreach (ContainerInfo container in containers)
        {
            bool isFirstCheck = !LastContainerState.ContainsKey(container.Name);
            ContainerState previousState = LastContainerState.GetValueOrDefault(container.Name, ContainerState.Unknown);
            ContainerState currentState = await GetContainerState(container, ct);

            LastContainerState[container.Name] = currentState;

            if (isFirstCheck || previousState != currentState)
            {
                await RecordHealthCheck(container.Name, currentState, ct);

                if (!isFirstCheck)
                {
                    logger.LogInformation("Container {Name} state changed: {Previous} -> {Current}",
                        container.Name, previousState, currentState);
                    HealthChanged?.Invoke(container.Name, currentState);
                    await RecordStateChange(container.Name, previousState, currentState, ct);
                }
            }
        }
    }

    public async Task<ContainerState> GetContainerState(ContainerInfo container, CancellationToken ct = default)
    {
        for (int attempt = 0; attempt <= maxRetries; attempt++)
        {
            try
            {
                ContainerState state = await InspectContainerState(container, ct);
                if (state != ContainerState.Unknown)
                {
                    if (_consecutiveFailures.TryRemove(container.Name, out _))
                        logger.LogInformation("Container {Name} recovered", container.Name);
                    return state;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Error checking container {Name} (attempt {Attempt}/{MaxRetries})",
                    container.Name, attempt + 1, maxRetries);
            }

            if (attempt < maxRetries)
                await Task.Delay(_retryDelay, ct);
        }

        int failures = _consecutiveFailures.AddOrUpdate(container.Name, 1, (_, count) => count + 1);
        if (failures >= 3)
            logger.LogError("Container {Name} has failed {Count} consecutive health checks",
                container.Name, failures);

        return ContainerState.Unknown;
    }

    async Task<ContainerState> InspectContainerState(ContainerInfo container, CancellationToken ct)
    {
        ContainerInspectResponse inspect = await containerManager.InspectContainer(container.Id, ct);

        if (inspect.State.Restarting)
            return ContainerState.Restarting;

        if (!inspect.State.Running)
        {
            return inspect.State.Status?.ToLowerInvariant() switch
            {
                "created" => ContainerState.Created,
                "exited" => ContainerState.Exited,
                "dead" => ContainerState.Dead,
                "removing" => ContainerState.Removing,
                _ => ContainerState.Stopped
            };
        }

        if (inspect.State.Health != null)
        {
            return inspect.State.Health.Status?.ToLowerInvariant() switch
            {
                "healthy" => ContainerState.Running,
                "unhealthy" => ContainerState.Running,
                "starting" => ContainerState.Starting,
                _ => ContainerState.Running
            };
        }

        return ContainerState.Running;
    }

    public static bool IsHealthyState(ContainerState state) => state == ContainerState.Running;
    public static bool IsStartingState(ContainerState state) => state == ContainerState.Starting;

    /// <summary>
    /// A container is considered "down" when it has settled into a non-running state. Transient
    /// states (Starting/Restarting/Created) are ignored so we don't alert on normal restarts.
    /// </summary>
    public static bool IsDownState(ContainerState state) => state
        is ContainerState.Exited
        or ContainerState.Dead
        or ContainerState.Stopped
        or ContainerState.Removing
        or ContainerState.Unknown;

    /// <summary>
    /// Records a service_down / service_recovered event (and its notification) on a health
    /// transition. Audit-only when no event store is wired.
    /// </summary>
    async Task RecordStateChange(
        string serviceName, ContainerState previous, ContainerState current, CancellationToken ct)
    {
        if (eventStore is null)
            return;

        if (IsDownState(current) && !IsDownState(previous))
        {
            string message = $"Service {serviceName} is down ({current})";
            await eventStore.RecordAsync(new SystemEventRecord
            {
                Category = EventCategory.Health,
                EventType = "service.down",
                Severity = EventSeverity.Error,
                Message = message,
                Source = serviceName,
                SourceName = serviceName,
                Notification = new NotificationMessage
                {
                    Key = HealthNotificationTopics.ServiceDown,
                    Title = "Service down",
                    Body = message,
                    Severity = EventSeverity.Error,
                    Source = serviceName,
                    SourceName = serviceName,
                    Timestamp = DateTime.UtcNow,
                    Fields = [new("State", current.ToString())]
                }
            }, ct);
        }
        else if (IsHealthyState(current) && IsDownState(previous))
        {
            string message = $"Service {serviceName} recovered";
            await eventStore.RecordAsync(new SystemEventRecord
            {
                Category = EventCategory.Health,
                EventType = "service.recovered",
                Severity = EventSeverity.Success,
                Message = message,
                Source = serviceName,
                SourceName = serviceName,
                Notification = new NotificationMessage
                {
                    Key = HealthNotificationTopics.ServiceRecovered,
                    Title = "Service recovered",
                    Body = message,
                    Severity = EventSeverity.Success,
                    Source = serviceName,
                    SourceName = serviceName,
                    Timestamp = DateTime.UtcNow
                }
            }, ct);
        }
    }

    async Task RecordHealthCheck(string serviceName, ContainerState state, CancellationToken ct)
    {
        try
        {
            await using ArrkDbContext db = await dbContextFactory.CreateDbContextAsync(ct);
            db.ServiceHealth.Add(new()
            {
                ServiceName = serviceName,
                IsHealthy = IsHealthyState(state),
                CheckedAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to record health check for {Service}", serviceName);
        }
    }
}
