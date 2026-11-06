using Hangfire;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Arrk.Core.Configuration;
using Arrk.Core.Models;
using Arrk.Core.Services;
using Arrk.Tvarr.Clients;
using Arrk.Tvarr.Models;
using Arrk.Tvarr.Services;

namespace Arrk.Tvarr.Jobs;

public class QualityProfileSyncJob(
    ServiceRegistry serviceRegistry,
    RadarrClient radarr,
    SonarrClient sonarr,
    IOptionsMonitor<QualityProfilesConfig> profilesConfig,
    ILogger<QualityProfileSyncJob> logger)
{
    public const string DefaultCron = "*/30 * * * * *";

    [JobDisplayName("Quality Profile Sync")]
    public async Task Execute(CancellationToken ct)
    {
        Service? tvService = serviceRegistry.Get("tv");
        if (tvService?.State.IsHealthy != true)
        {
            return;
        }

        QualityProfilesConfig config = profilesConfig.CurrentValue;

        await QualityDefinitionSync.ApplyAsync(radarr, config, "Radarr", logger, ct);
        await QualityDefinitionSync.ApplyAsync(sonarr, config, "Sonarr", logger, ct);
        await SyncCustomFormats(radarr, config, "Radarr", ct);
        await SyncCustomFormats(sonarr, config, "Sonarr", ct);
        await SyncQualityProfiles(radarr, config, "Radarr", ct);
        await SyncQualityProfiles(sonarr, config, "Sonarr", ct);
    }

    async Task SyncCustomFormats(
        ArrClient client,
        QualityProfilesConfig config,
        string serviceName,
        CancellationToken ct)
    {
        HashSet<string> existingNames =
            (await client.GetCustomFormats(ct))
            .Select(f => f.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (CustomFormatSpec spec in CustomFormatDefinitions.VideoFormats.Concat(CustomFormatDefinitions.AudioFormats))
        {
            if (existingNames.Contains(spec.Name))
            {
                continue;
            }

            logger.LogInformation(
                "Creating custom format '{Name}' in {Service}",
                spec.Name,
                serviceName);

            await client.CreateCustomFormat(new
            {
                name = spec.Name,
                includeCustomFormatWhenRenaming = false,
                specifications = spec.Specifications
                    .Select(s => new
                    {
                        name = spec.Name,
                        implementation =
                            "ReleaseTitleSpecification",
                        negate = false,
                        required = true,
                        fields = new[]
                        {
                            new
                            {
                                name = "value",
                                value = s.Pattern
                            }
                        }
                    }).ToArray()
            }, ct);
        }
    }

    async Task SyncQualityProfiles(
        ArrClient client,
        QualityProfilesConfig config,
        string serviceName,
        CancellationToken ct)
    {
        List<QualityProfile> existingProfiles = await client.GetQualityProfiles(ct);
        List<QualityDefinitionDto> qualityDefinitions = await client.GetQualityDefinitions(ct);
        List<CustomFormatDto> customFormats = await client.GetCustomFormats(ct);

        HashSet<string> configuredNames = config.Profiles
            .Select(p => p.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        List<QualityProfile> profilesToDelete = existingProfiles
            .Where(p =>
                !configuredNames.Contains(p.Name))
            .ToList();

        foreach (QualityProfile profile in profilesToDelete)
        {
            try
            {
                logger.LogInformation(
                    "Deleting non-matching quality profile '{Name}' from {Service}",
                    profile.Name,
                    serviceName);

                await client.DeleteQualityProfile(profile.Id, ct);
            }
            catch (HttpRequestException ex)
            {
                logger.LogWarning(
                    "Could not delete quality profile '{Name}' (ID {Id}) from {Service}: {Reason} — skipping",
                    profile.Name,
                    profile.Id,
                    serviceName,
                    ex.Message);
            }
        }

        foreach (QualityProfileDefinition profileDef in config.Profiles)
        {
            if (existingProfiles.Any(p => p.Name.Equals(
                profileDef.Name,
                StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            logger.LogInformation(
                "Creating quality profile '{Name}' in {Service}",
                profileDef.Name,
                serviceName);

            HashSet<QualitySource> allowedTypes = profileDef.AllowedQualities.ToHashSet();
            int minRes = ParseResolution(profileDef.MinResolution);
            int maxRes = ParseResolution(profileDef.MaxResolution);
            List<object> formatItems = BuildFormatItems(customFormats, config.Preferred);

            (List<object> items, int cutoffId) = BuildQualityItems(
                qualityDefinitions,
                allowedTypes,
                minRes,
                maxRes);

            await client.CreateQualityProfile(new()
            {
                Name = profileDef.Name,
                Items = items,
                UpgradeAllowed =
                    profileDef.AllowUpgrade,
                Cutoff = cutoffId,
                FormatItems = formatItems,
                MinFormatScore = 0,
                Language = new LanguageDto { Id = -2, Name = "Original" }
            }, ct);
        }
    }

    static int GetFormatScore(
        CodecPreferences codecs, string formatName) =>
        formatName.ToLowerInvariant() switch
        {
            "dolby vision" => codecs.Video.GetValueOrDefault("dolby_vision"),
            "hdr10+" => codecs.Video.GetValueOrDefault("hdr10+"),
            "hdr10" => codecs.Video.GetValueOrDefault("hdr"),
            "hlg" => codecs.Video.GetValueOrDefault("hlg"),
            "dolby truehd" => codecs.Audio.GetValueOrDefault("dolby_truehd"),
            "dolby atmos" => codecs.Audio.GetValueOrDefault("dolby_atmos"),
            "dts-x" => codecs.Audio.GetValueOrDefault("dts_x"),
            "dts-hd ma" => codecs.Audio.GetValueOrDefault("dts_hd_ma"),
            "flac" => codecs.Audio.GetValueOrDefault("flac"),
            _ => 0
        };

    static List<object> BuildFormatItems(
        List<CustomFormatDto> customFormats,
        CodecPreferences codecs)
    {
        return customFormats
            .Select(cf => new
            {
                format = cf.Id,
                score = GetFormatScore(codecs, cf.Name)
            })
            .Cast<object>()
            .ToList();
    }

    static QualitySource GetNativeType(string qualityName)
    {
        string lower = qualityName.ToLowerInvariant();
        if (lower.Contains("remux"))
            return QualitySource.Remux;
        if (lower.Contains("bluray"))
            return QualitySource.Bluray;
        if (lower.Contains("web"))
            return QualitySource.Web;
        if (lower.Contains("dvd"))
            return QualitySource.DVD;
        return QualitySource.HDTV;
    }

    static int ParseResolution(string resolution) =>
        resolution.ToLowerInvariant() switch
        {
            "480p" or "sd" => 480,
            "720p" or "hd" => 720,
            "1080p" or "fhd" => 1080,
            "2160p" or "4k" or "uhd" => 2160,
            _ => 1080
        };

    static (List<object> items, int cutoffId) BuildQualityItems(
        List<QualityDefinitionDto> all,
        HashSet<QualitySource> allowedTypes,
        int minRes,
        int maxRes)
    {
        List<object> items = new();
        QualityDefinitionDto? lastAllowed = null;

        foreach (QualityDefinitionDto def in all.OrderBy(d => d.Weight))
        {
            QualitySource nativeType = GetNativeType(def.Quality.Name);
            bool allowed =
                allowedTypes.Contains(nativeType)
                && def.Quality.Resolution >= minRes
                && def.Quality.Resolution <= maxRes;

            if (allowed)
            {
                lastAllowed = def;
            }

            items.Add(new
            {
                id = 0,
                name = (string?)null,
                quality = new
                {
                    id = def.Quality.Id,
                    name = def.Quality.Name,
                    source = def.Quality.Source,
                    resolution = def.Quality.Resolution
                },
                allowed,
                items = new List<object>()
            });
        }

        int cutoffId = lastAllowed?.Quality.Id
            ?? all.MaxBy(d => d.Weight)?.Quality.Id
            ?? 1;
        return (items, cutoffId);
    }
}
