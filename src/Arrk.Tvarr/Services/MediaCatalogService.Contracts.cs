using Arrk.Tv.Abstractions;

namespace Arrk.Tvarr.Services;

public partial class MediaCatalogService
{
    bool ITvCatalog.IsMetadataConfigured => IsTmdbConfigured;

    async Task<IReadOnlyDictionary<int, TvMovie>> ITvCatalog.GetLibraryMoviesAsync(
        bool force, CancellationToken ct)
    {
        Dictionary<int, TvMovie> movies = await GetLibraryMoviesAsync(force, ct);
        return movies;
    }

    async Task<(IReadOnlyDictionary<int, TvSeries> ByTmdb, IReadOnlyDictionary<int, TvSeries> ByTvdb)>
        ITvCatalog.GetLibrarySeriesAsync(bool force, CancellationToken ct)
    {
        (Dictionary<int, TvSeries> byTmdb, Dictionary<int, TvSeries> byTvdb) =
            await GetLibrarySeriesAsync(force, ct);
        return (byTmdb, byTvdb);
    }

    Task<TvMovie?> ITvCatalog.GetLibraryMovieAsync(int tmdbId, CancellationToken ct)
        => GetLibraryMovieAsync(tmdbId, ct);

    Task<TvSeries?> ITvCatalog.GetLibrarySeriesAsync(int tmdbId, CancellationToken ct)
        => GetLibrarySeriesAsync(tmdbId, ct);
}
