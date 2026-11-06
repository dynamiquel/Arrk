using Arrk.Core.Enums;
using Arrk.Core.Requests;
using Arrk.Requests;
using Arrk.Tvarr.Components.Shared;

namespace Arrk.Tvarr.Services;

/// <summary>
/// Builds a <see cref="RequestTarget"/> from a catalog <see cref="MediaItem"/> plus a chosen scope.
/// Kept out of the Blazor components so the mapping is unit-testable.
/// </summary>
public static class TvRequestTargets
{
    public static RequestTarget ForMovie(MediaItem item) => new(
        TvMediaDomains.Movie,
        RequestKind.Movie,
        ProviderIds(item, includeSeriesIds: false),
        item.Title,
        item.Year > 0 ? item.Year : null,
        PosterUrl: item.PosterUrl);

    public static RequestTarget ForSeries(MediaItem item) => new(
        TvMediaDomains.Tv,
        RequestKind.Series,
        ProviderIds(item, includeSeriesIds: true),
        item.Title,
        item.Year > 0 ? item.Year : null,
        PosterUrl: item.PosterUrl);

    public static RequestTarget ForSeason(MediaItem item, int seasonNumber) => new(
        TvMediaDomains.Tv,
        RequestKind.Season,
        ProviderIds(item, includeSeriesIds: true),
        item.Title,
        item.Year > 0 ? item.Year : null,
        seasonNumber,
        null,
        item.PosterUrl);

    public static RequestTarget ForEpisode(MediaItem item, int seasonNumber, int episodeNumber) => new(
        TvMediaDomains.Tv,
        RequestKind.Episode,
        ProviderIds(item, includeSeriesIds: true),
        item.Title,
        item.Year > 0 ? item.Year : null,
        seasonNumber,
        episodeNumber,
        item.PosterUrl);

    static Dictionary<string, string> ProviderIds(MediaItem item, bool includeSeriesIds)
    {
        return ProviderIdMap.Build(
            (TvProviderIds.Tmdb, item.TmdbId > 0 ? item.TmdbId.ToString() : null),
            (TvProviderIds.Tvdb, includeSeriesIds && item.TvdbId > 0 ? item.TvdbId.ToString() : null),
            (TvProviderIds.Imdb, item.ImdbId));
    }
}
