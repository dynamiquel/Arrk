using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Arrk.Core;
using Arrk.Core.Services;
using Arrk.Docker;

namespace Arrk.Navidrome;

/// <summary>
/// Ensures Navidrome has the Arrk API account. Navidrome (release) can only create its first
/// user through a terminal, so Arrk drives <c>navidrome user create</c> over a TTY exec once the
/// container is healthy. Credential is generated and persisted by <see cref="NavidromeApiSecret"/>.
/// </summary>
public sealed class NavidromeAccountProvisioner(
    NavidromeClient client,
    NavidromeApiSecret secret,
    IOptionsMonitor<NavidromeConfig> config,
    ServiceRegistry serviceRegistry,
    ArrkInstance arrk,
    ContainerManager containers,
    ILogger<NavidromeAccountProvisioner> logger) : BackgroundService
{
    const int MaxAttempts = 40;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        for (int attempt = 0; attempt < MaxAttempts && !stoppingToken.IsCancellationRequested; attempt++)
        {
            await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);

            try
            {
                if (!config.CurrentValue.Enabled)
                    return;

                Service? navidrome = serviceRegistry.Get(NavidromeServiceDefinition.StaticKey);
                if (navidrome?.State.IsHealthy != true)
                    continue;

                if (await client.PingAsync(stoppingToken))
                    return; // credentials already work

                string container = arrk.GetContainerName("navidrome");
                string output = await containers.ExecWithTtyInput(
                    container,
                    ["navidrome", "user", "create", "-u", secret.Username, "-a", "-n"],
                    secret.Password + "\n" + secret.Password + "\n",
                    stoppingToken);

                logger.LogInformation("Auto-provisioned Navidrome API user '{User}'", secret.Username);

                if (await client.PingAsync(stoppingToken))
                    return;
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Navidrome account provisioning attempt failed");
            }
        }

        if (!await client.PingAsync(CancellationToken.None))
        {
            logger.LogWarning(
                "Could not auto-provision the Navidrome API user '{User}'. Set services:navidrome:apiPassword and create the user in Navidrome manually if the automatic attempt failed.",
                secret.Username);
        }
    }
}
