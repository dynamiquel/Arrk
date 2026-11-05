using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Stackarr.Core;
using Stackarr.Core.Configuration;
using Stackarr.Core.Models;
using Stackarr.Core.Services;

namespace Stackarr.Orchestration;

public class ConfigSyncer(
    IOptionsMonitor<StackarrConfig> configMonitor,
    IConfiguration configuration,
    ServiceProvisioner provisioner,
    ServiceRegistry serviceRegistry,
    StackarrInstance stackarr,
    ILogger<ConfigSyncer> logger)
    : BackgroundService
{
    IDisposable? _changeSubscription;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Config syncer started");

        _changeSubscription = configMonitor.OnChange(newConfig =>
        {
            logger.LogInformation("Configuration change detected, syncing...");
            _ = SyncConfigAsync(newConfig, stoppingToken);
        });

        return Task.CompletedTask;
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _changeSubscription?.Dispose();
        await base.StopAsync(cancellationToken);
    }

    public async Task SyncConfigAsync(StackarrConfig config, CancellationToken ct = default)
    {
        try
        {
            logger.LogInformation("Syncing configuration to running services...");
            VolumePathResolver volumeResolver = new(config.Storage);

            // Reconcile every enabled service in dependency (registry) order. ProvisionContainer
            // is idempotent: it reuses a matching container and recreates it only when the
            // desired spec changed (image, env, network mode, ports, mounts, proxy labels, ...).
            bool vpnRecreated = false;
            List<ContainerSpec> routedSpecs = [];

            foreach (Service service in serviceRegistry.All)
            {
                if (!service.Definition.IsEnabled(configuration))
                    continue;

                IReadOnlyList<ContainerSpec> specs =
                    service.Definition.BuildContainerSpecs(configuration, volumeResolver, stackarr);
                foreach (ContainerSpec spec in specs)
                {
                    if (ServiceProvisioner.UsesSharedNetwork(spec))
                        routedSpecs.Add(spec);

                    if (service.Definition.Key == GluetunServiceDefinition.StaticKey)
                    {
                        ContainerInfo? existing = await provisioner.GetContainerStatus(spec.Name, ct);
                        if (existing == null || !provisioner.MatchesSpec(spec, existing))
                            vpnRecreated = true;
                    }
                }
            }

            // Containers sharing Gluetun's network namespace hold a reference to it. If Gluetun
            // is being recreated, detach those containers first so they are rebuilt cleanly.
            if (vpnRecreated)
            {
                foreach (ContainerSpec spec in routedSpecs)
                    await provisioner.RemoveContainer(spec.Name, ct);
            }

            foreach (Service service in serviceRegistry.All)
            {
                if (!service.Definition.IsEnabled(configuration))
                    continue;

                try
                {
                    IReadOnlyList<ContainerSpec> specs =
                        service.Definition.BuildContainerSpecs(configuration, volumeResolver, stackarr);
                    foreach (ContainerSpec spec in specs)
                        await provisioner.ProvisionContainer(spec, ct);
                }
                catch (Exception ex)
                {
                    // A single misbehaving service must not prevent the rest of the stack (or the
                    // stop pass below) from being reconciled.
                    logger.LogError(ex, "Failed to reconcile service {Service}", service.Definition.Key);
                }
            }

            // Stop disabled services in reverse dependency order so dependents detach before
            // their infrastructure is stopped.
            foreach (Service service in serviceRegistry.All.Reverse())
            {
                if (service.Definition.IsEnabled(configuration))
                    continue;

                try
                {
                    IReadOnlyList<ContainerSpec> specs =
                        service.Definition.BuildContainerSpecs(configuration, volumeResolver, stackarr);
                    foreach (ContainerSpec spec in specs)
                        await provisioner.StopContainer(spec.Name, ct);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Failed to stop disabled service {Service}", service.Definition.Key);
                }
            }

            // Refresh the file-provider routes Traefik watches: the Stackarr UI itself (toggled by
            // routes.proxy) and the VPN-routed services (whose ports live in Gluetun's network
            // namespace). Traefik watches the dynamic directory, so both apply immediately.
            TraefikDynamicConfig.WriteAllRoutes(config, volumeResolver, stackarr, serviceRegistry, configuration);

            logger.LogInformation("Configuration sync complete");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to sync configuration");
        }
    }

    public async Task ForceSyncAsync(CancellationToken ct = default)
    {
        StackarrConfig config = configMonitor.CurrentValue;
        await SyncConfigAsync(config, ct);
    }

    public List<string> GetEnabledServicesFromRegistry(IConfiguration configuration)
    {
        return serviceRegistry.GetEnabledKeys(configuration).ToList();
    }

    public List<string> GetDisabledServicesFromRegistry(IConfiguration configuration)
    {
        return serviceRegistry.GetDisabledKeys(configuration).ToList();
    }

    public static List<string> GetEnabledServices(IConfiguration configuration, ServiceRegistry registry)
    {
        return registry.GetEnabledKeys(configuration).ToList();
    }

    public static List<string> GetDisabledServices(IConfiguration configuration, ServiceRegistry registry)
    {
        return registry.GetDisabledKeys(configuration).ToList();
    }
}
