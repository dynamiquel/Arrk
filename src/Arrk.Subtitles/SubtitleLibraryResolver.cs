using Microsoft.Extensions.Logging;
using Arrk.Tv.Abstractions;

namespace Arrk.Subtitles;

/// <summary>
/// Resolves subtitle targets to the TV library. Kept in one place so every provider and the
/// shared status service resolve movies, series and episodes identically.
/// </summary>
public sealed class SubtitleLibraryResolver(
    IEnumerable<IMovieLibrary> movieLibraries,
    IEnumerable<ISeriesLibrary> seriesLibraries,
    ILogger<SubtitleLibraryResolver> logger)
{
    public bool HasLibraries => movieLibraries.Any() || seriesLibraries.Any();

    public async Task<IReadOnlyList<TvMovie>> GetAllMoviesAsync(CancellationToken ct = default)
    {
        IMovieLibrary? library = movieLibraries.FirstOrDefault();
        if (library is null)
            return [];

        try
        {
            return await library.GetAllMoviesAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "Could not list movies for subtitles");
            return [];
        }
    }

    public async Task<IReadOnlyList<TvSeries>> GetAllSeriesAsync(CancellationToken ct = default)
    {
        ISeriesLibrary? library = seriesLibraries.FirstOrDefault();
        if (library is null)
            return [];

        try
        {
            return await library.GetAllSeriesAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "Could not list series for subtitles");
            return [];
        }
    }

    public async Task<TvMovie?> GetMovieAsync(int movieId, CancellationToken ct = default)
    {
        IMovieLibrary? library = movieLibraries.FirstOrDefault();
        if (library is null)
            return null;

        try
        {
            return await library.GetMovieAsync(movieId, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "Could not resolve movie {MovieId} for subtitles", movieId);
            return null;
        }
    }

    public async Task<TvSeries?> GetSeriesAsync(int seriesId, CancellationToken ct = default)
    {
        ISeriesLibrary? library = seriesLibraries.FirstOrDefault();
        if (library is null)
            return null;

        try
        {
            return await library.GetSeriesAsync(seriesId, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "Could not resolve series {SeriesId} for subtitles", seriesId);
            return null;
        }
    }

    public async Task<List<TvEpisode>> GetEpisodesAsync(int seriesId, CancellationToken ct = default)
    {
        ISeriesLibrary? library = seriesLibraries.FirstOrDefault();
        if (library is null)
            return [];

        try
        {
            return [.. await library.GetEpisodesAsync(seriesId, ct)];
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "Could not resolve episodes for series {SeriesId}", seriesId);
            return [];
        }
    }

    public async Task<TvEpisode?> GetEpisodeAsync(int seriesId, int episodeId, CancellationToken ct = default)
    {
        List<TvEpisode> episodes = await GetEpisodesAsync(seriesId, ct);
        return episodes.FirstOrDefault(episode => episode.Id == episodeId);
    }
}
