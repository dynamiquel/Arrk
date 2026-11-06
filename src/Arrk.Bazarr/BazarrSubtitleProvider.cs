using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Arrk.Core.Events;
using Arrk.Subtitles.Abstractions;

namespace Arrk.Bazarr;

/// <summary>
/// Bazarr implementation of the subtitle contract. Bazarr owns provider search, scoring and file
/// placement; this adapter maps Arrk's targets, default languages and UI actions onto Bazarr's
/// REST API.
/// </summary>
public sealed class BazarrSubtitleProvider(
    BazarrClient client,
    BazarrSetupService setup,
    IEventBus events,
    IOptionsMonitor<BazarrConfig> config,
    IOptionsMonitor<SubtitlesConfig> subtitlesConfig,
    ILogger<BazarrSubtitleProvider> logger) : ISubtitleProvider
{
    public string Name => "bazarr";

    public int Priority => 1;

    public bool IsEnabled => config.CurrentValue.Enabled;

    readonly Dictionary<string, string> _languageCodes = new(StringComparer.OrdinalIgnoreCase);

    public async Task ConfigureAsync(SubtitlePreferences preferences, CancellationToken ct = default) =>
        await setup.EnsureConfiguredAsync(preferences, ct);

    public Task<bool> IsAvailableAsync(CancellationToken ct = default) => client.IsAvailableAsync(ct);

    public async Task<IReadOnlyList<SubtitleLanguage>> GetLanguagesAsync(CancellationToken ct = default)
    {
        List<BazarrLanguage> languages = await client.GetLanguagesAsync(ct);
        return languages
            .Where(language => !string.IsNullOrWhiteSpace(language.Code2))
            .Select(language => new SubtitleLanguage(language.Code2, language.Name))
            .DistinctBy(language => language.Code)
            .OrderBy(language => language.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    async Task<SubtitleStatus?> GetBazarrStatusAsync(SubtitleTarget target, CancellationToken ct = default)
    {
        switch (target.Kind)
        {
            case SubtitleMediaKind.Movie:
            {
                BazarrMovie? movie = (await client.GetMoviesAsync([target.MovieId], ct)).FirstOrDefault();
                return movie is null ? null : MapStatus([movie]);
            }
            case SubtitleMediaKind.Episode:
            {
                BazarrEpisode? episode = (await client.GetEpisodesAsync(episodeIds: [target.EpisodeId], ct: ct))
                    .FirstOrDefault();
                return episode is null ? null : MapStatus([episode]);
            }
            case SubtitleMediaKind.Series:
            case SubtitleMediaKind.Season:
            {
                List<BazarrEpisode> episodes = await client.GetEpisodesAsync(seriesIds: [target.SeriesId], ct: ct);
                if (target.Kind == SubtitleMediaKind.Season)
                    episodes = episodes.Where(episode => episode.Season == target.SeasonNumber).ToList();
                return episodes.Count == 0 ? null : MapStatus(episodes);
            }
            default:
                return null;
        }
    }

    public async Task<IReadOnlyList<SubtitleCandidate>> SearchAsync(
        SubtitleSearchRequest request,
        CancellationToken ct = default)
    {
        HashSet<string> languages = new(
            request.Languages
                .Select(SubtitleLanguages.ToTwoLetter)
                .Where(code => code.Length > 0),
            StringComparer.OrdinalIgnoreCase);

        if (languages.Count == 0)
            return [];

        IReadOnlyList<string> defaults = SubtitleLanguages.NormalizeTwoLetter(subtitlesConfig.CurrentValue.Languages);
        bool allDefault = languages.All(language => defaults.Any(defaultLanguage =>
            SubtitleLanguages.Matches(defaultLanguage, language)));

        if (!allDefault && request.Target.Kind is SubtitleMediaKind.Movie or SubtitleMediaKind.Episode)
            return await SearchWithSearchProfileAsync(request.Target, languages, ct);

        List<BazarrProviderCandidate> candidates = request.Target.Kind switch
        {
            SubtitleMediaKind.Movie => await client.SearchMovieAsync(request.Target.MovieId, ct),
            SubtitleMediaKind.Episode => await client.SearchEpisodeAsync(request.Target.EpisodeId, ct),
            _ => []
        };

        return await MapCandidatesAsync(candidates, languages, ct);
    }

    /// <summary>
    /// Bazarr's manual search only searches the languages in the item's profile. A user may ask for a
    /// language that is not one of the configured defaults, so switch the item to a temporary search
    /// profile that contains the requested languages, search, then restore the default profile.
    /// The candidate ids stay in Bazarr's cache for the download call.
    /// </summary>
    async Task<IReadOnlyList<SubtitleCandidate>> SearchWithSearchProfileAsync(
        SubtitleTarget target,
        IReadOnlySet<string> languages,
        CancellationToken ct)
    {
        IReadOnlyList<BazarrLanguageProfile> profiles = await client.GetLanguageProfilesAsync(ct);
        int? currentProfile = await CurrentProfileAsync(target, ct);
        int restore = currentProfile is int current && current != BazarrSetupService.SearchProfileId
            ? current
            : BazarrSetupService.ProfileId;

        List<BazarrProviderCandidate> candidates;
        try
        {
            List<BazarrLanguageProfile> updated = profiles
                .Where(item => item.ProfileId != BazarrSetupService.SearchProfileId)
                .ToList();
            updated.Add(BazarrSetupService.BuildProfile(
                BazarrSetupService.SearchProfileId,
                "Arrk Search",
                languages.ToList()));
            await client.UpdateProfilesAsync(updated, ct);
            await AssignProfileAsync(target, BazarrSetupService.SearchProfileId, ct);

            candidates = target.Kind switch
            {
                SubtitleMediaKind.Movie => await client.SearchMovieAsync(target.MovieId, ct),
                SubtitleMediaKind.Episode => await client.SearchEpisodeAsync(target.EpisodeId, ct),
                _ => []
            };
        }
        finally
        {
            await AssignProfileAsync(target, restore, ct);
        }

        return await MapCandidatesAsync(candidates, languages, ct);
    }

    async Task<IReadOnlyList<SubtitleCandidate>> MapCandidatesAsync(
        List<BazarrProviderCandidate> candidates,
        IReadOnlySet<string> languages,
        CancellationToken ct)
    {
        List<SubtitleCandidate> mapped = [];
        foreach (BazarrProviderCandidate candidate in candidates)
        {
            string languageCode = await LanguageCodeAsync(candidate.Language, ct);
            if (languageCode.Length == 0 || !languages.Contains(languageCode))
                continue;

            mapped.Add(MapCandidate(candidate, languageCode));
        }

        return mapped.OrderByDescending(candidate => candidate.Score).ToList();
    }

    async Task<string> LanguageCodeAsync(string languageName, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(languageName))
            return "";

        string normalized = SubtitleLanguages.NormalizeCode(languageName);
        if (normalized.Length == 2)
            return normalized;

        if (normalized.Length == 3)
        {
            CultureInfo? fromThree = CultureInfo.GetCultures(CultureTypes.NeutralCultures)
                .FirstOrDefault(culture => string.Equals(
                    culture.ThreeLetterISOLanguageName, normalized, StringComparison.OrdinalIgnoreCase));
            if (fromThree is not null && fromThree.TwoLetterISOLanguageName.Length == 2)
                return fromThree.TwoLetterISOLanguageName;
        }

        if (_languageCodes.TryGetValue(languageName, out string? cached))
            return cached;

        string code = CultureInfo.GetCultures(CultureTypes.NeutralCultures)
            .FirstOrDefault(culture => string.Equals(culture.EnglishName, languageName, StringComparison.OrdinalIgnoreCase))
            ?.TwoLetterISOLanguageName ?? "";

        if (code.Length == 0)
        {
            try
            {
                foreach (BazarrLanguage language in await client.GetLanguagesAsync(ct))
                {
                    if (!string.IsNullOrWhiteSpace(language.Name) && !string.IsNullOrWhiteSpace(language.Code2))
                        _languageCodes[language.Name] = language.Code2;
                }

                if (_languageCodes.TryGetValue(languageName, out string? mapped))
                    code = mapped;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogDebug(ex, "Could not map Bazarr language name {Language}", languageName);
            }
        }

        code ??= "";
        if (code.Length > 0)
            _languageCodes[languageName] = code;
        return SubtitleLanguages.NormalizeCode(code);
    }

    async Task<int?> CurrentProfileAsync(SubtitleTarget target, CancellationToken ct) =>
        target.Kind switch
        {
            SubtitleMediaKind.Movie => (await client.GetMoviesAsync([target.MovieId], ct)).FirstOrDefault()?.ProfileId,
            SubtitleMediaKind.Episode => (await client.GetSeriesAsync([target.SeriesId], ct)).FirstOrDefault()?.ProfileId,
            _ => null
        };

    async Task AssignProfileAsync(SubtitleTarget target, int profileId, CancellationToken ct)
    {
        switch (target.Kind)
        {
            case SubtitleMediaKind.Movie:
                await client.AssignMovieProfileAsync([target.MovieId], profileId, ct);
                break;
            case SubtitleMediaKind.Episode:
                await client.AssignSeriesProfileAsync([target.SeriesId], profileId, ct);
                break;
        }
    }

    public async Task DownloadAsync(SubtitleDownloadRequest request, CancellationToken ct = default)
    {
        BazarrProviderCandidate candidate = new()
        {
            Provider = request.Candidate.Provider,
            Subtitle = request.Candidate.Id,
            HearingImpaired = request.Candidate.HearingImpaired ? "True" : "False",
            Forced = request.Candidate.Forced ? "True" : "False",
            OriginalFormat = request.Candidate.OriginalFormat ? "True" : "False",
            Language = request.Candidate.LanguageName
        };

        switch (request.Target.Kind)
        {
            case SubtitleMediaKind.Movie:
                await client.DownloadMovieSubtitleAsync(request.Target.MovieId, candidate, ct);
                await WaitForTrackAsync(request.Target, request.Candidate.LanguageCode, ct);
                await events.Publish(new SubtitleDownloadedEvent(
                    Name,
                    request.Candidate.LanguageCode,
                    request.Target.Title,
                    request.Target.Subtitle,
                    MovieId: request.Target.MovieId), ct);
                break;
            case SubtitleMediaKind.Episode:
                await client.DownloadEpisodeSubtitleAsync(
                    request.Target.SeriesId,
                    request.Target.EpisodeId,
                    candidate,
                    ct);
                await WaitForTrackAsync(request.Target, request.Candidate.LanguageCode, ct);
                await events.Publish(new SubtitleDownloadedEvent(
                    Name,
                    request.Candidate.LanguageCode,
                    request.Target.Title,
                    request.Target.Subtitle,
                    SeriesId: request.Target.SeriesId,
                    EpisodeId: request.Target.EpisodeId), ct);
                break;
            default:
                throw new NotSupportedException("Manual subtitle downloads are only supported for a movie or episode.");
        }
    }

    /// <summary>
    /// Bazarr queues downloads, so the POST returns before the sidecar file exists. Wait briefly for
    /// the language to appear before telling Jellyfin to scan; if it takes longer than the window,
    /// the scheduled scan (or the next download event) still picks it up.
    /// </summary>
    async Task WaitForTrackAsync(SubtitleTarget target, string language, CancellationToken ct)
    {
        for (int attempt = 0; attempt < 15; attempt++)
        {
            try
            {
                SubtitleStatus? status = await GetBazarrStatusAsync(target, ct);
                if (status is not null && status.Available.Any(track =>
                        SubtitleLanguages.Matches(track.LanguageCode, language)))
                {
                    return;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogDebug(ex, "Could not check whether the {Language} subtitle landed", language);
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(1), ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }

        logger.LogInformation(
            "The {Language} subtitle for {Title} has not appeared in Bazarr yet; the next library scan will pick it up",
            language,
            target.Title);
    }

    static SubtitleStatus MapStatus(IReadOnlyList<BazarrEpisode> episodes)
    {
        List<SubtitleTrack> available = episodes
            .SelectMany(episode => episode.Subtitles)
            .Select(track => new SubtitleTrack(
                track.Code2,
                string.IsNullOrWhiteSpace(track.Name) ? track.Code2 : track.Name,
                track.Forced,
                track.HearingImpaired,
                track.Path))
            .DistinctBy(track => (track.LanguageCode, track.Forced, track.HearingImpaired))
            .ToList();

        List<SubtitleLanguage> missing = episodes
            .SelectMany(episode => episode.MissingSubtitles)
            .Where(language => !string.IsNullOrWhiteSpace(language.Code2))
            .Select(language => new SubtitleLanguage(
                language.Code2,
                string.IsNullOrWhiteSpace(language.Name) ? language.Code2 : language.Name))
            .DistinctBy(language => language.Code)
            .ToList();

        int missingEpisodes = episodes.Count(episode => episode.MissingSubtitles.Count > 0);
        return new SubtitleStatus(available, missing, episodes.Count, missingEpisodes);
    }

    static SubtitleStatus MapStatus(IReadOnlyList<BazarrMovie> movies)
    {
        BazarrMovie movie = movies[0];
        List<SubtitleTrack> available = movie.Subtitles
            .Select(track => new SubtitleTrack(
                track.Code2,
                string.IsNullOrWhiteSpace(track.Name) ? track.Code2 : track.Name,
                track.Forced,
                track.HearingImpaired,
                track.Path))
            .DistinctBy(track => (track.LanguageCode, track.Forced, track.HearingImpaired))
            .ToList();

        List<SubtitleLanguage> missing = movie.MissingSubtitles
            .Where(language => !string.IsNullOrWhiteSpace(language.Code2))
            .Select(language => new SubtitleLanguage(
                language.Code2,
                string.IsNullOrWhiteSpace(language.Name) ? language.Code2 : language.Name))
            .DistinctBy(language => language.Code)
            .ToList();

        return new SubtitleStatus(available, missing, 1, missing.Count > 0 ? 1 : 0);
    }

    static SubtitleCandidate MapCandidate(BazarrProviderCandidate candidate, string languageCode)
    {
        string languageName = candidate.Language ?? "";
        string displayName = SubtitleLanguages.DisplayName(languageCode);
        string title = candidate.ReleaseInfo.Count > 0
            ? string.Join(" · ", candidate.ReleaseInfo.Select(part => part.ReplaceLineEndings(" ").Trim()))
            : string.IsNullOrWhiteSpace(displayName) ? languageCode.ToUpperInvariant() : displayName;

        return new SubtitleCandidate
        {
            Provider = candidate.Provider,
            Id = candidate.Subtitle,
            LanguageCode = languageCode,
            LanguageName = string.IsNullOrWhiteSpace(displayName) ? languageName : displayName,
            Title = title,
            ReleaseInfo = candidate.ReleaseInfo,
            Score = candidate.Score,
            Forced = string.Equals(candidate.Forced, "True", StringComparison.OrdinalIgnoreCase),
            HearingImpaired = string.Equals(candidate.HearingImpaired, "True", StringComparison.OrdinalIgnoreCase),
            OriginalFormat = string.Equals(candidate.OriginalFormat, "True", StringComparison.OrdinalIgnoreCase),
            Uploader = candidate.Uploader
        };
    }
}
