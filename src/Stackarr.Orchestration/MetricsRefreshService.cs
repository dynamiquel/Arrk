using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Stackarr.Orchestration;

public class MetricsRefreshService(ServiceStateManager stateManager, ILogger<MetricsRefreshService> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Metrics refresh service started");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await stateManager.RefreshMetrics(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error refreshing metrics");
            }

            await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
        }
    }
}
