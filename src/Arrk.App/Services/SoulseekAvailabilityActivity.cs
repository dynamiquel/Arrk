using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Arrk.Core.Services;
using Arrk.Slskd;

namespace Arrk.App.Services;

/// <summary>
/// Surfaces a Soulseek outage in the activity centre while slskd is disconnected (it drops the
/// connection under a search flood), so it is obvious on the web UI why acquisitions aren't
/// progressing. The entry clears itself the moment Soulseek is reachable again.
/// </summary>
public sealed class SoulseekAvailabilityActivity(
    SlskdService slskd,
    LiveActivityRegistry registry,
    ILogger<SoulseekAvailabilityActivity> logger) : BackgroundService
{
    static readonly TimeSpan Interval = TimeSpan.FromSeconds(3);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        LiveActivityScope? waiting = null;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                bool unavailable = slskd.IsEnabled && !await slskd.IsAvailableAsync(stoppingToken);
                if (unavailable)
                {
                    waiting ??= registry.Begin(
                        LiveActivityKind.Warning,
                        "Waiting for Soulseek connection",
                        "Soulseek is disconnected; retrying automatically");
                }
                else if (waiting is not null)
                {
                    waiting.Dispose();
                    waiting = null;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Could not check Soulseek availability");
            }

            try { await Task.Delay(Interval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }

        waiting?.Dispose();
    }
}
