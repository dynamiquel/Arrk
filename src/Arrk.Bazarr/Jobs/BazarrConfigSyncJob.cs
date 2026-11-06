using Hangfire;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Arrk.Core.Services;

namespace Arrk.Bazarr.Jobs;

/// <summary>Keeps Bazarr's Sonarr/Radarr links, language profile and provider list in sync with Arrk.</summary>
public sealed class BazarrConfigSyncJob(
    ILogger<BazarrConfigSyncJob> logger,
    IOptionsMonitor<BazarrConfig> config,
    IOptionsMonitor<SubtitlesConfig> subtitlesConfig,
    ServiceRegistry services,
    BazarrSubtitleProvider provider)
{
    public const string DefaultCron = "*/5 * * * *";

    [JobDisplayName("Subtitle Config Sync (Bazarr)")]
    public async Task Execute(CancellationToken ct)
    {
        if (!config.CurrentValue.Enabled)
            return;

        if (services.Get(BazarrServiceDefinition.StaticKey)?.State.IsHealthy != true)
            return;

        if (services.Get("tv")?.State.IsHealthy != true)
            return;

        try
        {
            await provider.ConfigureAsync(subtitlesConfig.CurrentValue.ToPreferences(), ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not sync Bazarr configuration");
        }
    }
}
