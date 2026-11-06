using Microsoft.Extensions.Logging;
using Arrk.Core.Configuration;
using Arrk.Media.Abstractions;
using Arrk.Tvarr.Clients;
using Arrk.Tvarr.Models;

namespace Arrk.Tvarr.Services;

/// <summary>
/// Applies configured minimum bitrates to the arrs' quality definitions.
///
/// <para>
/// Radarr and Sonarr ship every quality definition with <c>minSize = 0</c>, so the arrs will
/// happily grab a 3 GB "4K" release. Their size limits are expressed in MB per minute of
/// runtime; Arrk exposes the floor in Mbps because that is how bitrate is usually discussed.
/// The conversion is <c>MB/min = Mbps × 60 / 8</c>.
/// </para>
/// </summary>
public static class QualityDefinitionSync
{
    /// <summary>Converts a bitrate in Mbps to the arr's MB-per-minute size unit.</summary>
    public static double MbpsToMbPerMinute(double mbps) => mbps * 60d / 8d;

    /// <summary>
    /// Resolves the floor for a quality definition, or <c>null</c> when that resolution tier is
    /// unmanaged (<c>0</c>) and the arr's existing value should be left alone.
    /// </summary>
    public static double? MinimumSizeMbPerMinute(
        int resolution,
        IReadOnlyDictionary<string, double> minimumBitratesMbps)
    {
        string tier = TierFor(resolution);

        if (!minimumBitratesMbps.TryGetValue(tier, out double mbps) || mbps <= 0)
            return null;

        return MbpsToMbPerMinute(mbps);
    }

    /// <summary>
    /// Writes the configured floors to a single arr and returns how many definitions changed.
    /// Only positive tiers are managed; other definitions are untouched. A preferred or maximum
    /// size below the new floor is raised or cleared so the arr's
    /// <c>min &lt;= preferred &lt;= max</c> validation still passes.
    /// </summary>
    public static async Task<int> ApplyAsync(
        ArrClient client,
        QualityProfilesConfig config,
        string serviceName,
        ILogger logger,
        CancellationToken ct = default)
    {
        List<QualityDefinitionDto> definitions = await client.GetQualityDefinitions(ct);
        int changed = 0;

        foreach (QualityDefinitionDto definition in definitions)
        {
            double? minimum = MinimumSizeMbPerMinute(
                definition.Quality.Resolution,
                config.MinimumBitratesMbps);

            if (minimum is null)
                continue;

            double current = definition.MinSize ?? 0;
            if (Math.Abs(current - minimum.Value) < 0.01)
                continue;

            logger.LogInformation(
                "Setting {Service} quality '{Quality}' minimum size to {Size:0.#} MB/min ({Mbps:0.#} Mbps)",
                serviceName,
                definition.Quality.Name,
                minimum.Value,
                minimum.Value * 8d / 60d);

            definition.MinSize = minimum.Value;

            // The arrs validate min <= preferred <= max. A floor above either cap would be
            // rejected, so lift the soft preference and drop a contradictory maximum.
            if (definition.MaxSize is double maxSize && maxSize < minimum.Value)
                definition.MaxSize = null;

            if (definition.PreferredSize is double preferredSize && preferredSize < minimum.Value)
                definition.PreferredSize = minimum.Value;

            await client.UpdateQualityDefinition(definition, ct);
            changed++;
        }

        return changed;
    }

    static string TierFor(int resolution) => resolution switch
    {
        <= 576 => Resolutions.R480p,
        <= 720 => Resolutions.R720p,
        <= 1080 => Resolutions.R1080p,
        _ => Resolutions.R2160p
    };
}
