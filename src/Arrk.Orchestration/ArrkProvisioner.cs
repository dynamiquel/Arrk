using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Arrk.Core;
using Arrk.Core.Configuration;
using Arrk.Core.Models;
using Arrk.Core.Services;
using Arrk.Docker;

namespace Arrk.Orchestration;

public class ArrkProvisioner(
    NetworkManager networkManager,
    ImageManager imageManager,
    IDbContextFactory<ArrkDbContext> dbContextFactory,
    ServiceRegistry serviceRegistry,
    IConfiguration configuration,
    ArrkInstance arrk,
    ILogger<ArrkProvisioner> logger)
{
    public async Task Provision(ArrkConfig? config = null, CancellationToken ct = default)
    {
        logger.LogInformation("Starting Arrk provisioning...");

        logger.LogInformation("Creating Docker network...");
        await networkManager.CreateOrGetNetwork(arrk.NetworkName, new()
        {
            [ContainerLabels.Managed] = ContainerLabels.ManagedValue
        }, ct);

        logger.LogInformation("Initializing database...");
        await using ArrkDbContext dbContext = await dbContextFactory.CreateDbContextAsync(ct);
        await dbContext.Database.MigrateAsync(ct);

        WriteReverseProxyRoutes(config);

        logger.LogInformation("Pulling required images...");
        await PullImage(TraefikServiceDefinition.Image, ct);

        if (configuration.IsVpnTunnelRequired())
            await PullImage("qmcgaw/gluetun:latest", ct);

        logger.LogInformation("Arrk provisioning complete.");
    }

    void WriteReverseProxyRoutes(ArrkConfig? config)
    {
        try
        {
            ArrkConfig effective = config ?? new ArrkConfig();
            VolumePathResolver volumeResolver = new(effective.Storage);
            TraefikDynamicConfig.WriteAllRoutes(effective, volumeResolver, arrk, serviceRegistry, configuration);
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
