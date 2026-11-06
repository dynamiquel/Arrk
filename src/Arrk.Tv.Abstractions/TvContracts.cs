namespace Arrk.Tv.Abstractions;

/// <summary>Read-only movie library exposed by a TV backend.</summary>
public interface IMovieLibrary
{
    Task<IReadOnlyList<TvMovie>> GetAllMoviesAsync(CancellationToken ct = default);

    Task<TvMovie?> GetMovieAsync(int id, CancellationToken ct = default);
}

/// <summary>Movie mutations a TV backend may support.</summary>
public interface IMovieLibraryWriter
{
    Task DeleteMovieAsync(int id, bool deleteFiles = false, CancellationToken ct = default);

    Task SearchAndAddMovieAsync(
        int tmdbId,
        int? qualityProfileId = null,
        bool monitored = true,
        bool searchForMovie = true,
        CancellationToken ct = default);

    Task SetQualityProfileAsync(int movieId, int qualityProfileId, CancellationToken ct = default);

    Task CommandSearchMovieAsync(int movieId, CancellationToken ct = default);

    Task SearchMissingMoviesAsync(CancellationToken ct = default);

    Task DeleteMovieFileAsync(int movieFileId, CancellationToken ct = default);
}

/// <summary>Read-only series/season/episode library exposed by a TV backend.</summary>
public interface ISeriesLibrary
{
    Task<IReadOnlyList<TvSeries>> GetAllSeriesAsync(CancellationToken ct = default);

    Task<TvSeries?> GetSeriesAsync(int id, CancellationToken ct = default);

    Task<IReadOnlyList<TvSeason>> GetSeasonsAsync(int seriesId, CancellationToken ct = default);

    Task<IReadOnlyList<TvEpisode>> GetEpisodesAsync(int seriesId, CancellationToken ct = default);
}

/// <summary>Series, season and episode mutations a TV backend may support.</summary>
public interface ISeriesLibraryWriter
{
    Task DeleteSeriesAsync(int id, bool deleteFiles = false, CancellationToken ct = default);

    Task DeleteEpisodeAsync(int episodeFileId, CancellationToken ct = default);

    Task<int> AddSeriesWithOptionsAsync(
        int tvdbId,
        string title,
        int qualityProfileId,
        IReadOnlyCollection<int>? monitoredSeasons,
        bool monitorFutureSeasons,
        bool searchForSeries,
        CancellationToken ct = default);

    Task ApplySeasonMonitoringAsync(
        int seriesId,
        IReadOnlyCollection<int> seasons,
        bool monitorFutureSeasons,
        CancellationToken ct = default);

    Task ToggleSeasonMonitoredAsync(int seriesId, int seasonNumber, bool monitored, CancellationToken ct = default);

    Task ToggleEpisodeMonitoredAsync(int episodeId, bool monitored, CancellationToken ct = default);

    Task CommandSearchSeriesAsync(int seriesId, CancellationToken ct = default);

    Task CommandSearchSeasonAsync(int seriesId, int seasonNumber, CancellationToken ct = default);

    Task CommandSearchEpisodeAsync(int episodeId, CancellationToken ct = default);

    Task SearchMissingEpisodesAsync(CancellationToken ct = default);
}

/// <summary>
/// The read-only library projection other modules use to answer "do I have this?" without
/// referencing the arr clients or the TV UI.
/// </summary>
public interface ITvCatalog
{
    bool IsMetadataConfigured { get; }

    Task<IReadOnlyDictionary<int, TvMovie>> GetLibraryMoviesAsync(
        bool force = false, CancellationToken ct = default);

    Task<(IReadOnlyDictionary<int, TvSeries> ByTmdb, IReadOnlyDictionary<int, TvSeries> ByTvdb)>
        GetLibrarySeriesAsync(bool force = false, CancellationToken ct = default);

    Task<TvMovie?> GetLibraryMovieAsync(int tmdbId, CancellationToken ct = default);

    Task<TvSeries?> GetLibrarySeriesAsync(int tmdbId, CancellationToken ct = default);
}

/// <summary>Deep links a player module can resolve for a TV item.</summary>
public interface ITvPlayerLinks
{
    string Name { get; }

    bool IsAvailable { get; }

    bool WebPlayerEnabled { get; }

    Task<string?> GetMovieUrlAsync(TvMovie movie, CancellationToken ct = default);

    Task<TvSeriesLinks?> GetSeriesLinksAsync(TvSeries series, CancellationToken ct = default);

    string? GetEpisodeUrl(TvSeriesLinks? links, int season, int episode);

    string? BuildWatchUrl(string itemId, string? title = null, string? back = null, string? art = null);

    string? ExtractItemId(string? url);
}

/// <summary>Player deep links for a series and each resolved episode.</summary>
public sealed record TvSeriesLinks(
    string SeriesUrl,
    IReadOnlyDictionary<(int Season, int Episode), string> Episodes);
