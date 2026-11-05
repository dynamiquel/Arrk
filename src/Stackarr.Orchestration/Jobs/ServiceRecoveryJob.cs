using System.Collections.Concurrent;
using Hangfire;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Stackarr.Core;
using Stackarr.Core.Enums;
using Stackarr.Core.Models;
using Stackarr.Core.Services;
using Stackarr.Docker;

namespace Stackarr.Orchestration.Jobs;

public class ServiceRecoveryJob(
    ResourceInventory inventory,
    ServiceProvisioner provisioner,
    ServiceRegistry serviceRegistry,
    ImageManager imageManager,
    StackarrInstance stackarr,
    IConfiguration configuration,
    ILogger<ServiceRecoveryJob> logger)
{
    public const string DefaultCron = "*/10 * * * * *";

    const int MaxRecoveryAttempts = 5;
    const int BackoffMultiplier = 10;
    const int MaxBackoffSeconds = 300;
    const int MaxExponent = 6;
    readonly ConcurrentDictionary<string, ServiceRecoveryState>
        _recoveryStates = new();

    [JobDisplayName("Service Recovery")]
    public async Task Execute(CancellationToken ct)
    {
        List<ContainerInfo> containers =
            await inventory.GetOwnedContainers(ct);
        List<string> enabledServices =
            ConfigSyncer.GetEnabledServices(
                configuration, serviceRegistry);

        foreach (ContainerInfo container in containers)
        {
            if (!container.Labels.TryGetValue(
                    ContainerLabels.Service,
                    out string? serviceKey))
            {
                continue;
            }

            if (!enabledServices.Contains(serviceKey))
            {
                continue;
            }

            if (imageManager.IsImageBeingRepulled(
                    container.Image))
            {
                continue;
            }

            if (container.State != ContainerState.Running)
            {
                logger.LogWarning(
                    "Service {Service} is not running " +
                    "(state: {State}), initiating recovery",
                    serviceKey, container.State);
                await RecoverService(serviceKey, ct);
            }
        }
    }

    async Task RecoverService(
        string serviceName, CancellationToken ct)
    {
        ServiceRecoveryState recoveryState =
            _recoveryStates.GetOrAdd(
                serviceName,
                _ => new());

        if (recoveryState.Attempts >= MaxRecoveryAttempts)
        {
            double exponent = Math.Min(
                recoveryState.Attempts, MaxExponent);
            double backoff =
                Math.Pow(2, exponent)
                * BackoffMultiplier;
            double cappedBackoff =
                Math.Min(backoff, MaxBackoffSeconds);
            if (DateTime.UtcNow
                - recoveryState.LastAttemptTime
                < TimeSpan.FromSeconds(cappedBackoff))
            {
                return;
            }
        }

        recoveryState.Attempts++;
        recoveryState.LastAttemptTime = DateTime.UtcNow;

        try
        {
            Service? service =
                serviceRegistry.Get(serviceName);
            if (service == null)
            {
                return;
            }

            IReadOnlyList<ContainerSpec> specs =
                service.Definition.BuildContainerSpecs(
                    configuration,
                    new(
                        new()),
                    stackarr);
            bool anyRecovered = false;

            foreach (ContainerSpec spec in specs)
            {
                if (await provisioner.EnsureContainerRunning(
                        spec.Name, ct))
                {
                    anyRecovered = true;
                }
                else
                {
                    await provisioner.ProvisionContainer(
                        spec, ct);
                    anyRecovered = true;
                }
            }

            if (anyRecovered)
            {
                recoveryState.Attempts = 0;
                recoveryState.ConsecutiveFailures = 0;
            }
        }
        catch (Exception ex)
        {
            recoveryState.ConsecutiveFailures++;
            logger.LogError(
                ex,
                "Failed to recover service {Service}",
                serviceName);
        }
    }

    class ServiceRecoveryState
    {
        public int Attempts { get; set; }
        public int ConsecutiveFailures { get; set; }
        public DateTime LastAttemptTime { get; set; }
    }
}
