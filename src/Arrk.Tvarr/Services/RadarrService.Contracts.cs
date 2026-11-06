using Arrk.Tv.Abstractions;

namespace Arrk.Tvarr.Services;

public partial class RadarrService
{
    async Task<IReadOnlyList<TvMovie>> IMovieLibrary.GetAllMoviesAsync(CancellationToken ct)
        => await GetAllMovies(ct);

    Task<TvMovie?> IMovieLibrary.GetMovieAsync(int id, CancellationToken ct)
        => GetMovie(id, ct);

    Task IMovieLibraryWriter.DeleteMovieAsync(int id, bool deleteFiles, CancellationToken ct)
        => DeleteMovie(id, deleteFiles, ct);

    Task IMovieLibraryWriter.SearchAndAddMovieAsync(
        int tmdbId,
        int? qualityProfileId,
        bool monitored,
        bool searchForMovie,
        CancellationToken ct)
        => SearchAndAddMovie(tmdbId, qualityProfileId, monitored, searchForMovie, ct);

    Task IMovieLibraryWriter.SetQualityProfileAsync(int movieId, int qualityProfileId, CancellationToken ct)
        => SetQualityProfile(movieId, qualityProfileId, ct);

    Task IMovieLibraryWriter.CommandSearchMovieAsync(int movieId, CancellationToken ct)
        => CommandSearchMovie(movieId, ct);

    Task IMovieLibraryWriter.SearchMissingMoviesAsync(CancellationToken ct)
        => SearchMissingAsync(ct);

    Task IMovieLibraryWriter.DeleteMovieFileAsync(int movieFileId, CancellationToken ct)
        => DeleteMovieFile(movieFileId, ct);
}
