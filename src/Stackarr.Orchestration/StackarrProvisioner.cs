using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Stackarr.Core;
using Stackarr.Core.Configuration;
using Stackarr.Core.Models;
using Stackarr.Core.Services;
using Stackarr.Docker;

namespace Stackarr.Orchestration;

public class StackarrProvisioner(
    NetworkManager networkManager,
    ImageManager imageManager,
    IDbContextFactory<StackarrDbContext> dbContextFactory,
    ServiceRegistry serviceRegistry,
    IConfiguration configuration,
    StackarrInstance stackarr,
    ILogger<StackarrProvisioner> logger)
{
    public async Task Provision(StackarrConfig? config = null, CancellationToken ct = default)
    {
        logger.LogInformation("Starting Stackarr provisioning...");

        logger.LogInformation("Creating Docker network...");
        await networkManager.CreateOrGetNetwork(stackarr.NetworkName, new()
        {
            [ContainerLabels.Managed] = ContainerLabels.ManagedValue
        }, ct);

        logger.LogInformation("Initializing database...");
        await using StackarrDbContext dbContext = await dbContextFactory.CreateDbContextAsync(ct);
        await dbContext.Database.MigrateAsync(ct);

        WriteReverseProxyRoutes(config);

        logger.LogInformation("Pulling required images...");
        await PullImage(TraefikServiceDefinition.Image, ct);

        if (configuration.IsVpnTunnelRequired())
            await PullImage("qmcgaw/gluetun:latest", ct);

        logger.LogInformation("Stackarr provisioning complete.");
    }

    void WriteReverseProxyRoutes(StackarrConfig? config)
    {
        try
        {
            StackarrConfig effective = config ?? new StackarrConfig();
            VolumePathResolver volumeResolver = new(effective.Storage);
            TraefikDynamicConfig.WriteAllRoutes(effective, volumeResolver, stackarr, serviceRegistry, configuration);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to write reverse proxy dynamic configuration");
        }
    }

    async Task PullImage(string image, CancellationToken ct)
    {
        logger.LogInformation("Pulling image {Image}...", image);
        await imageManager.PullImage(image, ct);
    }
}
