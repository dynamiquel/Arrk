using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Stackarr.Core.Models;

namespace Stackarr.Docker;

public class ContainerStatsMonitor(ContainerManager containerManager, ILogger<ContainerStatsMonitor> logger)
    : BackgroundService
{
    readonly TimeSpan _interval = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                List<ContainerInfo> containers = await containerManager.ListContainers(
                    labelFilter: $"{ContainerLabels.Managed}={ContainerLabels.ManagedValue}", ct: stoppingToken);

                foreach (ContainerInfo container in containers)
                {
                    logger.LogDebug("Container {Name} state: {State}", container.Name, container.State);
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error monitoring container stats");
            }

            await Task.Delay(_interval, stoppingToken);
        }
    }
}
