using System.Text.Json;
using Microsoft.Extensions.Options;
using Stackarr.Core;
using Stackarr.Core.Services;
using Stackarr.Subtitles.Abstractions;

namespace Stackarr.Bazarr;

/// <summary>
/// Converges Bazarr onto Stackarr's defaults: Sonarr/Radarr connections, the language profile,
/// providers and profile assignment. Automatic downloads are owned by the Stackarr dispatcher,
/// so Bazarr's own wanted search is kept out of the way. It is idempotent and re-runs on change.
/// </summary>
public sealed class BazarrSetupService(
    BazarrClient client,
    StackarrInstance stackarr,
    SettingsService settings,
    IOptionsMonitor<BazarrConfig> config)
{
    public const int ProfileId = 1;
    public const int SearchProfileId = 2;
    const string FingerprintSettingKey = "subtitles.bazarr.preferences";

    readonly SemaphoreSlim _configureLock = new(1, 1);

    public async Task EnsureConfiguredAsync(SubtitlePreferences preferences, CancellationToken ct = default)
    {
        await _configureLock.WaitAsync(ct);
        try
        {
            await ConfigureCoreAsync(preferences, ct);
        }
        finally
        {
            _configureLock.Release();
        }
    }

    async Task ConfigureCoreAsync(SubtitlePreferences preferences, CancellationToken ct)
    {
        IReadOnlyList<string> languages = SubtitleLanguages.NormalizeTwoLetter(preferences.Languages);
        BazarrConfig currentConfig = config.CurrentValue;
        List<string> providers = BuildProviders(currentConfig);

        List<BazarrLanguage> allLanguages = await client.GetLanguagesAsync(ct);
        List<string> enabledLanguages = allLanguages
            .Select(language => language.Code2)
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        List<KeyValuePair<string, string>> fields = BuildSettingsFields(languages, providers, enabledLanguages);
        await client.UpdateSettingsAsync(fields, ct);

        List<BazarrSeries> series = await client.GetSeriesAsync([], ct);
        List<BazarrMovie> movies = await client.GetMoviesAsync([], ct);

        // Existing items assigned to the profile pick up changed languages automatically; only
        // unassigned items need a POST, which also avoids re-indexing the whole library every sync.
        await client.AssignSeriesProfileAsync(
            series.Where(item => item.ProfileId != ProfileId).Select(item => item.SonarrSeriesId),
            ProfileId,
            ct);
        await client.AssignMovieProfileAsync(
            movies.Where(item => item.ProfileId != ProfileId).Select(item => item.RadarrId),
            ProfileId,
            ct);

        string fingerprint = Fingerprint(languages, providers);
        string? previous = await settings.GetAsync(FingerprintSettingKey, ct);
        if (!string.Equals(previous, fingerprint, StringComparison.Ordinal))
        {
            // Provider list changes should not inherit a stale throttle from the old configuration.
            await client.ResetProvidersAsync(ct);
            await settings.SetAsync(FingerprintSettingKey, fingerprint, ct);
        }
    }

    internal static List<string> BuildProviders(BazarrConfig config) =>
        [.. config.Providers
            .Where(provider => !string.IsNullOrWhiteSpace(provider))
            .Select(provider => provider.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)];

    internal List<KeyValuePair<string, string>> BuildSettingsFields(
        IReadOnlyList<string> languages,
        IReadOnlyList<string> providers,
        IReadOnlyList<string>? enabledLanguages = null)
    {
        List<KeyValuePair<string, string>> fields = [];

        foreach (string language in enabledLanguages ?? languages)
            fields.Add(new("languages-enabled", language));

        fields.Add(new("languages-profiles", JsonSerializer.Serialize(BuildProfiles(languages), BazarrClient.JsonOptions)));

        string sonarr = stackarr.GetContainerName("sonarr");
        string radarr = stackarr.GetContainerName("radarr");

        Add(fields, "settings-general-use_sonarr", "true");
        Add(fields, "settings-sonarr-ip", sonarr);
        Add(fields, "settings-sonarr-port", "8989");
        Add(fields, "settings-sonarr-base_url", "/sonarr");
        Add(fields, "settings-sonarr-apikey", "stackarr");
        Add(fields, "settings-sonarr-ssl", "false");

        Add(fields, "settings-general-use_radarr", "true");
        Add(fields, "settings-radarr-ip", radarr);
        Add(fields, "settings-radarr-port", "7878");
        Add(fields, "settings-radarr-base_url", "/radarr");
        Add(fields, "settings-radarr-apikey", "stackarr");
        Add(fields, "settings-radarr-ssl", "false");

        foreach (string provider in providers)
            fields.Add(new("settings-general-enabled_providers", provider));
        if (providers.Count == 0)
            fields.Add(new("settings-general-enabled_providers", ""));

        // OpenSubtitles is handled by Stackarr's own provider. Clear any legacy Bazarr
        // credentials so an upgraded install cannot silently keep using a second login.
        Add(fields, "settings-opensubtitlescom-username", "");
        Add(fields, "settings-opensubtitlescom-password", "");

        // Subf2m refuses requests without a browser user agent. Bazarr's default is empty, so set a
        // conventional one; users can override it in Bazarr's own settings.
        Add(fields, "settings-subf2m-user_agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36");

        Add(fields, "settings-general-serie_default_enabled", "true");
        Add(fields, "settings-general-serie_default_profile", ProfileId.ToString());
        Add(fields, "settings-general-movie_default_enabled", "true");
        Add(fields, "settings-general-movie_default_profile", ProfileId.ToString());

        // Stackarr's dispatcher owns automatic downloads. Keep Bazarr's own sweep out of the way
        // so the two systems cannot race on the same sidecar files.
        Add(fields, "settings-general-auto_update", "false");
        Add(fields, "settings-general-upgrade_subs", "false");
        Add(fields, "settings-general-wanted_search_frequency", "168");
        Add(fields, "settings-general-wanted_search_frequency_movie", "168");
        Add(fields, "settings-sonarr-defer_search_signalr", "true");
        Add(fields, "settings-radarr-defer_search_signalr", "true");

        return fields;
    }

    internal static IReadOnlyList<BazarrLanguageProfile> BuildProfiles(IReadOnlyList<string> languages) =>
    [
        BuildProfile(ProfileId, "Stackarr Default", languages),
        BuildProfile(SearchProfileId, "Stackarr Search", languages)
    ];

    internal static BazarrLanguageProfile BuildProfile(
        int profileId,
        string name,
        IReadOnlyList<string> languages)
    {
        int id = 0;
        List<BazarrLanguageProfileItem> items = [];
        foreach (string language in languages)
        {
            id++;
            items.Add(new BazarrLanguageProfileItem
            {
                Id = id,
                Language = language,
                AudioExclude = "False",
                AudioOnlyInclude = "False",
                Hi = "False",
                Forced = "False"
            });
        }

        return new BazarrLanguageProfile
        {
            ProfileId = profileId,
            Name = name,
            Cutoff = id,
            Items = items,
            MustContain = [],
            MustNotContain = [],
            OriginalFormat = null,
            Tag = null
        };
    }

    static void Add(List<KeyValuePair<string, string>> fields, string key, string value) =>
        fields.Add(new(key, value));

    static string Fingerprint(IReadOnlyList<string> languages, IReadOnlyList<string> providers) =>
        string.Join(",", languages) + "|" + string.Join(",", providers);
}
