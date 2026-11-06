using Arrk.Tvarr.Components.Shared;
using Arrk.Core.Enums;
using Arrk.Core.Models;
using Arrk.Core.Playback;
using Arrk.Tvarr.Services;

namespace Arrk.Tvarr.Services;

/// <summary>
/// Builds the Continue Watching list with artwork resolved entirely outside the media player:
/// <b>TMDB first, then Sonarr/Radarr</b>. Jellyfin (or any future player) supplies only identity,
/// progress and a play link, never artwork.
/// </summary>
public sealed class ContinueWatchingService(
    WatchStateService watch,
    TmdbService tmdb,
    SonarrService sonarr,
    RadarrService radarr)
{
    public bool IsAvailable => watch.IsAvailable;

    public async Task<IReadOnlyList<ContinueWatchingItem>> GetAsync(
        string username,
        int limit = 20,
        MediaDomain? domain = null,
        CancellationToken ct = default)
    {
        IReadOnlyList<ContinueWatchingItem> raw = await watch.GetContinueWatchingAsync(username, limit * 2, ct);
        IEnumerable<ContinueWatchingItem> filtered = raw;
        if (domain is { } d)
            filtered = filtered.Where(i => i.Key.Domain == d);

        List<ContinueWatchingItem> items = filtered.Take(limit).ToList();
        await EnrichArtAsync(items, ct);
        return items;
    }

    async Task EnrichArtAsync(List<ContinueWatchingItem> items, CancellationToken ct)
    {
        if (items.Count == 0)
            return;

        // Shared arr libraries (fetched once) for the fallback artwork.
        List<TvSeries>? allSeries = null;
        List<TvMovie>? allMovies = null;
        try { allSeries = await sonarr.GetAllSeries(ct); } catch { /* fallback only */ }
        try { allMovies = await radarr.GetAllMovies(ct); } catch { /* fallback only */ }

        Dictionary<int, TvSeries> seriesByTvdb = (allSeries ?? [])
            .Where(s => s.TvdbId > 0)
            .GroupBy(s => s.TvdbId).ToDictionary(g => g.Key, g => g.First());
        Dictionary<int, TvMovie> moviesByTmdb = (allMovies ?? [])
            .Where(m => m.TmdbId > 0)
            .GroupBy(m => m.TmdbId).ToDictionary(g => g.Key, g => g.First());

        Dictionary<int, Dictionary<(int Season, int Episode), TvEpisode>> sonarrEpisodes = [];
        Dictionary<int, string> seriesLogos = [];
        Dictionary<string, TmdbTvEpisode> tmdbEpisodes = [];

        for (int i = 0; i < items.Count; i++)
        {
            ContinueWatchingItem item = items[i];
            if (item.IsEpisode)
            {
                (string? primary, string? fallback, string? logo, string? title) = await ResolveEpisodeAsync(
                    item, ct, seriesByTvdb, sonarrEpisodes, seriesLogos, tmdbEpisodes);
                ContinueWatchingItem resolved = item.WithArt(primary, fallback, logo);
                if (!string.IsNullOrWhiteSpace(title))
                    resolved = resolved with { Title = title };
                items[i] = resolved;
            }
            else
            {
                (string? primary, string? fallback, string? logo) = await ResolveMovieAsync(
                    item, moviesByTmdb, ct);
                items[i] = item.WithArt(primary, fallback, logo);
            }
        }
    }

    async Task<(string? Primary, string? Fallback, string? Logo, string? Title)> ResolveEpisodeAsync(
        ContinueWatchingItem item,
        CancellationToken ct,
        Dictionary<int, TvSeries> seriesByTvdb,
        Dictionary<int, Dictionary<(int Season, int Episode), TvEpisode>> sonarrEpisodes,
        Dictionary<int, string> seriesLogos,
        Dictionary<string, TmdbTvEpisode> tmdbEpisodes)
    {
        string? primary = null;
        string? logo = null;
        string? title = null;
        int tmdbId = item.SeriesTmdbId;

        if (tmdb.IsConfigured && tmdbId > 0)
        {
            string key = $"{tmdbId}-{item.Key.SeasonNumber}-{item.Key.EpisodeNumber}";
            if (tmdbEpisodes.TryGetValue(key, out TmdbTvEpisode? cached))
            {
                if (!string.IsNullOrEmpty(cached.StillPath))
                    primary = TmdbService.GetStillUrl(cached.StillPath, "w780");
                if (!string.IsNullOrWhiteSpace(cached.Name))
                    title = cached.Name;
            }
            else
            {
                TmdbTvSeason? season = await tmdb.GetTvSeasonAsync(tmdbId, item.Key.SeasonNumber, ct);
                if (season?.Episodes is not null)
                {
                    foreach (TmdbTvEpisode ep in season.Episodes)
                        tmdbEpisodes[$"{tmdbId}-{item.Key.SeasonNumber}-{ep.EpisodeNumber}"] = ep;

                    TmdbTvEpisode? match = season.Episodes.FirstOrDefault(e => e.EpisodeNumber == item.Key.EpisodeNumber);
                    if (match?.StillPath is { Length: > 0 } still)
                        primary = TmdbService.GetStillUrl(still, "w780");
                    if (!string.IsNullOrWhiteSpace(match?.Name))
                        title = match!.Name;
                }
            }

            if (seriesLogos.TryGetValue(tmdbId, out string? knownLogo))
            {
                logo = knownLogo;
            }
            else
            {
                try
                {
                    TmdbTvDetail? detail = await tmdb.GetTvDetailAsync(tmdbId, ct);
                    logo = TmdbService.FindBestLogo(detail?.Images?.Logos);
                }
                catch { /* non-critical */ }
                seriesLogos[tmdbId] = logo ?? "";
            }
        }

        // Fallback artwork + title from Sonarr (episode still image, then series poster).
        string? fallback = null;
        if (item.SeriesTvdbId > 0 && seriesByTvdb.TryGetValue(item.SeriesTvdbId, out TvSeries? series))
        {
            if (!sonarrEpisodes.TryGetValue(series.Id, out Dictionary<(int, int), TvEpisode>? episodes))
            {
                episodes = [];
                try
                {
                    foreach (TvEpisode ep in await sonarr.GetEpisodes(series.Id, ct))
                        episodes[(ep.SeasonNumber, ep.EpisodeNumber)] = ep;
                }
                catch { /* non-critical */ }
                sonarrEpisodes[series.Id] = episodes;
            }

            if (episodes.TryGetValue((item.Key.SeasonNumber, item.Key.EpisodeNumber), out TvEpisode? epMatch))
            {
                if (!string.IsNullOrEmpty(epMatch.ImageUrl))
                    fallback = epMatch.ImageUrl;
                if (string.IsNullOrWhiteSpace(title) && !string.IsNullOrWhiteSpace(epMatch.Title))
                    title = epMatch.Title;
            }

            if (string.IsNullOrEmpty(fallback) && !string.IsNullOrEmpty(series.PosterUrl))
                fallback = series.PosterUrl;
        }

        return (primary, fallback, logo, title);
    }

    async Task<(string? Primary, string? Fallback, string? Logo)> ResolveMovieAsync(
        ContinueWatchingItem item,
        Dictionary<int, TvMovie> moviesByTmdb,
        CancellationToken ct)
    {
        string? primary = null;
        string? logo = null;
        int tmdbId = item.Key.TmdbId;

        if (tmdb.IsConfigured && tmdbId > 0)
        {
            try
            {
                TmdbMovieDetail? detail = await tmdb.GetMovieDetailAsync(tmdbId, ct);
                if (!string.IsNullOrEmpty(detail?.BackdropPath))
                    primary = TmdbService.GetBackdropUrl(detail.BackdropPath, "w1280");
                logo = TmdbService.FindBestLogo(detail?.Images?.Logos);
            }
            catch { /* non-critical */ }
        }

        // Fallback artwork from Radarr.
        string? fallback = null;
        if (moviesByTmdb.TryGetValue(tmdbId, out TvMovie? movie))
            fallback = !string.IsNullOrEmpty(movie.FanartUrl) ? movie.FanartUrl : movie.PosterUrl;

        return (primary, fallback, logo);
    }
}
