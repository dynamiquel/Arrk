using Stackarr.Core.Playback;
using Stackarr.Media.Abstractions;
using Stackarr.Tvarr.Models;

namespace Stackarr.Tvarr.Services;

/// <summary>Builds the stable media entity reference used by follow buttons and notifications.</summary>
public static class MediaEntityRefs
{
    public static MediaEntityRef? Movie(TvMovie movie)
    {
        if (movie.TmdbId <= 0)
            return null;

        return new MediaEntityRef(
            "tv",
            TvMediaDomains.MovieDomain,
            MediaItemKeys.Movie(movie.TmdbId),
            movie.Title,
            movie.Year > 0 ? movie.Year.ToString() : null,
            movie.PosterUrl,
            movie.FanartUrl,
            Url: "/movies");
    }

    public static MediaEntityRef? Series(TvSeries series)
    {
        string key = MediaItemKeys.Series(series.TvdbId, series.TmdbId);
        if (string.IsNullOrEmpty(key))
            return null;

        return new MediaEntityRef(
            "tv",
            TvMediaDomains.TvDomain,
            key,
            series.Title,
            null,
            series.PosterUrl,
            series.FanartUrl,
            Url: "/shows");
    }

    public static MediaEntityRef? Season(TvSeries series, int seasonNumber)
    {
        string seriesKey = MediaItemKeys.Series(series.TvdbId, series.TmdbId);
        if (string.IsNullOrEmpty(seriesKey) || seasonNumber <= 0)
            return null;

        return new MediaEntityRef(
            "tv",
            TvMediaDomains.TvDomain,
            MediaItemKeys.Season(seriesKey, seasonNumber),
            series.Title,
            $"Season {seasonNumber}",
            series.PosterUrl,
            series.FanartUrl,
            seriesKey,
            [seriesKey],
            Url: "/shows");
    }
}
