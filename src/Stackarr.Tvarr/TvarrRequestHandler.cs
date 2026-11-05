using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Stackarr.Core.Enums;
using Stackarr.Core.Models;
using Stackarr.Core.Playback;
using Stackarr.Core.Requests;
using Stackarr.Media.Abstractions;
using Stackarr.Tvarr.Clients;
using Stackarr.Tvarr.Models;
using Stackarr.Tvarr.Services;

namespace Stackarr.Tvarr;

/// <summary>
/// Contributes the TV/Movie request capability (Radarr for movies, Sonarr for TV). This is the
/// only place the request system meets Radarr/Sonarr; swapping either for an alternative means
/// registering a different <see cref="IMediaRequestHandler"/>.
/// </summary>
public sealed class TvarrRequestHandler(
    RadarrService radarr,
    SonarrService sonarr,
    IOptionsMonitor<TvConfig> config,
    ILogger<TvarrRequestHandler> logger) : IMediaRequestHandler
{
    public string Key => "tv";
    public string DisplayName => "TV & Movies";
    public bool IsAvailable => config.CurrentValue.Enabled;

    public IReadOnlyList<RequestCapability> Capabilities { get; } =
    [
        new(TvMediaDomains.Movie, RequestKind.Movie),
        new(TvMediaDomains.Tv, RequestKind.Series),
        new(TvMediaDomains.Tv, RequestKind.Season),
        new(TvMediaDomains.Tv, RequestKind.Episode),
    ];

    public async Task<IReadOnlyList<RequestQualityOption>> GetQualityOptionsAsync(CancellationToken ct = default)
    {
        List<RequestQualityOption> options = [];
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);

        foreach (QualityProfile profile in await SafeProfilesAsync(radarr.GetQualityProfiles, ct))
        {
            if (seen.Add(profile.Id.ToString()))
                options.Add(new(profile.Id.ToString(), profile.Name));
        }

        foreach (QualityProfile profile in await SafeProfilesAsync(sonarr.GetQualityProfiles, ct))
        {
            if (seen.Add(profile.Id.ToString()))
                options.Add(new(profile.Id.ToString(), profile.Name));
        }

        return options;
    }

    static async Task<List<QualityProfile>> SafeProfilesAsync(
        Func<CancellationToken, Task<List<QualityProfile>>> fetch, CancellationToken ct)
    {
        try
        {
            return await fetch(ct);
        }
        catch
        {
            return [];
        }
    }

    public async Task<RequestTargetState> CheckAsync(RequestTarget target, CancellationToken ct = default)
    {
        if (target.Kind == RequestKind.Movie)
        {
            int tmdbId = target.GetProviderIdInt(ProviderIdMapKeys.Tmdb);
            if (tmdbId <= 0)
                return RequestTargetState.Missing();

            List<TvMovie> movies = await radarr.GetAllMovies(ct);
            TvMovie? movie = movies.FirstOrDefault(m => m.TmdbId == tmdbId);
            return movie is null
                ? RequestTargetState.Missing()
                : RequestTargetState.InLibrary(movie.Id.ToString(), movie.HasFile);
        }

        int tvdbId = target.GetProviderIdInt(ProviderIdMapKeys.Tvdb);
        if (tvdbId <= 0)
            return RequestTargetState.Missing();

        List<TvSeries> series = await sonarr.GetAllSeries(ct);
        TvSeries? match = series.FirstOrDefault(s => s.TvdbId == tvdbId);
        return match is null
            ? RequestTargetState.Missing()
            : RequestTargetState.InLibrary(match.Id.ToString(), match.DownloadedEpisodes > 0);
    }

    public MediaEntityRef? ResolveEntity(RequestTarget target)
    {
        if (target.Domain == TvMediaDomains.MovieDomain)
        {
            int movieTmdbId = target.GetProviderIdInt(ProviderIdMapKeys.Tmdb);
            if (movieTmdbId <= 0)
                return null;

            return new MediaEntityRef(
                "tv",
                TvMediaDomains.MovieDomain,
                MediaItemKeys.Movie(movieTmdbId),
                target.Title,
                target.Year?.ToString(),
                target.PosterUrl,
                Url: "/movies");
        }

        string seriesKey = MediaItemKeys.Series(
            target.GetProviderIdInt(ProviderIdMapKeys.Tvdb),
            target.GetProviderIdInt(ProviderIdMapKeys.Tmdb));
        if (string.IsNullOrEmpty(seriesKey))
            return null;

        string itemKey = seriesKey;
        List<string> ancestors = [];
        string? subtitle = null;

        if (target.Kind == RequestKind.Season && target.SeasonNumber is > 0)
        {
            itemKey = MediaItemKeys.Season(seriesKey, target.SeasonNumber.Value);
            ancestors.Add(seriesKey);
            subtitle = $"Season {target.SeasonNumber.Value}";
        }
        else if (target.Kind == RequestKind.Episode
                 && target.SeasonNumber is > 0
                 && target.EpisodeNumber is > 0)
        {
            itemKey = MediaItemKeys.Episode(
                seriesKey, target.SeasonNumber.Value, target.EpisodeNumber.Value);
            ancestors.Add(MediaItemKeys.Season(seriesKey, target.SeasonNumber.Value));
            ancestors.Add(seriesKey);
            subtitle = $"S{target.SeasonNumber.Value:00}E{target.EpisodeNumber.Value:00}";
        }

        return new MediaEntityRef(
            "tv",
            TvMediaDomains.TvDomain,
            itemKey,
            target.Title,
            subtitle,
            target.PosterUrl,
            AncestorItemKeys: ancestors,
            Url: "/shows");
    }

    public async Task<FulfilmentResult> FulfilAsync(FulfilmentRequest request, CancellationToken ct = default)
    {
        RequestTarget target = request.Target;
        try
        {
            return target.Kind switch
            {
                RequestKind.Movie => await FulfilMovieAsync(request, ct),
                RequestKind.Series => await FulfilSeriesAsync(request, ct),
                RequestKind.Season => await FulfilSeasonAsync(request, ct),
                RequestKind.Episode => await FulfilEpisodeAsync(request, ct),
                _ => FulfilmentResult.Fail($"Unsupported request kind {target.Kind}.")
            };
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "TV request fulfilment failed for {Title}", target.Title);
            return FulfilmentResult.Fail(ex.Message);
        }
    }

    async Task<FulfilmentResult> FulfilMovieAsync(FulfilmentRequest request, CancellationToken ct)
    {
        int tmdbId = request.Target.GetProviderIdInt(ProviderIdMapKeys.Tmdb);
        if (tmdbId <= 0)
            return FulfilmentResult.Fail("A TMDB id is required to add a movie.");

        int? profileId = ParseProfileId(request.QualityKey);
        await radarr.SearchAndAddMovie(tmdbId, profileId, monitored: true, searchForMovie: request.SearchNow, ct);

        string? libraryId = null;
        List<TvMovie> movies = await radarr.GetAllMovies(ct);
        TvMovie? added = movies.FirstOrDefault(m => m.TmdbId == tmdbId);
        libraryId = added?.Id.ToString();
        return FulfilmentResult.Ok(libraryId, $"Added {request.Target.Title}.");
    }

    async Task<FulfilmentResult> FulfilSeriesAsync(FulfilmentRequest request, CancellationToken ct)
    {
        int tvdbId = request.Target.GetProviderIdInt(ProviderIdMapKeys.Tvdb);
        if (tvdbId <= 0)
            return FulfilmentResult.Fail("A TVDB id is required to add a series.");

        TvSeries? existing = await FindSeriesAsync(tvdbId, ct);
        if (existing is not null)
            return FulfilmentResult.Ok(existing.Id.ToString(), "Series already in library.");

        int profileId = await ResolveSeriesProfileAsync(request.QualityKey, ct);
        int id = await sonarr.AddSeriesWithOptions(
            tvdbId, request.Target.Title, profileId,
            monitoredSeasons: null, monitorFutureSeasons: true, searchForSeries: request.SearchNow, ct);

        return FulfilmentResult.Ok(id.ToString(), $"Added {request.Target.Title}.");
    }

    async Task<FulfilmentResult> FulfilSeasonAsync(FulfilmentRequest request, CancellationToken ct)
    {
        int tvdbId = request.Target.GetProviderIdInt(ProviderIdMapKeys.Tvdb);
        if (tvdbId <= 0)
            return FulfilmentResult.Fail("A TVDB id is required to request a season.");
        int season = request.Target.SeasonNumber ?? 0;
        if (season <= 0)
            return FulfilmentResult.Fail("A season number is required.");

        int profileId = await ResolveSeriesProfileAsync(request.QualityKey, ct);
        int seriesId = await EnsureSeriesAsync(tvdbId, request.Target.Title, profileId, ct);

        await sonarr.ApplySeasonMonitoring(seriesId, [season], monitorFutureSeasons: false, ct);
        if (request.SearchNow)
            await sonarr.CommandSearchSeason(seriesId, season, ct);

        return FulfilmentResult.Ok(seriesId.ToString(), $"Added {request.Target.Title} season {season}.");
    }

    async Task<FulfilmentResult> FulfilEpisodeAsync(FulfilmentRequest request, CancellationToken ct)
    {
        int tvdbId = request.Target.GetProviderIdInt(ProviderIdMapKeys.Tvdb);
        if (tvdbId <= 0)
            return FulfilmentResult.Fail("A TVDB id is required to request an episode.");
        int season = request.Target.SeasonNumber ?? 0;
        int episode = request.Target.EpisodeNumber ?? 0;
        if (season <= 0 || episode <= 0)
            return FulfilmentResult.Fail("A season and episode number are required.");

        int profileId = await ResolveSeriesProfileAsync(request.QualityKey, ct);
        int seriesId = await EnsureSeriesAsync(tvdbId, request.Target.Title, profileId, ct);

        List<TvEpisode> episodes = await sonarr.GetEpisodes(seriesId, ct);
        TvEpisode? match = episodes.FirstOrDefault(e => e.SeasonNumber == season && e.EpisodeNumber == episode);
        if (match is null)
            return FulfilmentResult.Fail($"Episode S{season}E{episode} was not found.");

        await sonarr.ToggleEpisodeMonitored(match.Id, true, ct);
        if (request.SearchNow)
            await sonarr.CommandSearchEpisode(match.Id, ct);

        return FulfilmentResult.Ok(seriesId.ToString(), $"Added {request.Target.Title} S{season}E{episode}.");
    }

    async Task<int> EnsureSeriesAsync(int tvdbId, string title, int profileId, CancellationToken ct)
    {
        TvSeries? existing = await FindSeriesAsync(tvdbId, ct);
        if (existing is not null)
            return existing.Id;

        return await sonarr.AddSeriesWithOptions(
            tvdbId, title, profileId,
            monitoredSeasons: null, monitorFutureSeasons: false, searchForSeries: false, ct);
    }

    async Task<TvSeries?> FindSeriesAsync(int tvdbId, CancellationToken ct)
    {
        List<TvSeries> all = await sonarr.GetAllSeries(ct);
        return all.FirstOrDefault(s => s.TvdbId == tvdbId);
    }

    async Task<int> ResolveSeriesProfileAsync(string? qualityKey, CancellationToken ct)
    {
        int? parsed = ParseProfileId(qualityKey);
        return parsed ?? await sonarr.GetDefaultQualityProfileId(ct);
    }

    static int? ParseProfileId(string? key) =>
        int.TryParse(key, out int id) && id > 0 ? id : null;

    /// <summary>Provider-id keys used by the request system.</summary>
    static class ProviderIdMapKeys
    {
        public const string Tmdb = "tmdb";
        public const string Tvdb = "tvdb";
    }
}
