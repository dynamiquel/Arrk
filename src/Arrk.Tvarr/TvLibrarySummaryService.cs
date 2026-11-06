using Arrk.Tv.Abstractions;
using Arrk.Tvarr.Models;
using Arrk.Tvarr.Services;

namespace Arrk.Tvarr;

/// <summary>Shared movie-library snapshot behind the movie tiles.</summary>
public sealed record MovieLibrarySummary(IReadOnlyList<TvMovie> Movies, int Downloading);

/// <summary>Shared show-library snapshot behind the show tiles.</summary>
public sealed record ShowLibrarySummary(IReadOnlyList<TvSeries> Series, int Downloading);

/// <summary>
/// Owns one short-lived Radarr/Sonarr library snapshot for the TV tiles. Several tiles on the same
/// dashboard read the same movie or show library; without this service each tile would issue its
/// own full library request and make the dashboard slower as tiles are added. Requests within the
/// snapshot window share one load, and the snapshot is refreshed on demand after it expires.
/// </summary>
public sealed class TvLibrarySummaryService(RadarrService radarr, SonarrService sonarr, TimeProvider time)
{
    static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(10);

    readonly SemaphoreSlim _movieGate = new(1, 1);
    readonly SemaphoreSlim _showGate = new(1, 1);
    MovieLibrarySummary? _movies;
    ShowLibrarySummary? _shows;
    DateTimeOffset _moviesExpires;
    DateTimeOffset _showsExpires;

    public async Task<MovieLibrarySummary> GetMoviesAsync(CancellationToken ct = default)
    {
        DateTimeOffset now = time.GetUtcNow();
        if (_movies is not null && now < _moviesExpires)
            return _movies;

        await _movieGate.WaitAsync(ct);
        try
        {
            now = time.GetUtcNow();
            if (_movies is not null && now < _moviesExpires)
                return _movies;

            List<TvMovie> movies = await radarr.GetAllMovies(ct);
            int downloading = 0;
            try
            {
                downloading = (await radarr.GetQueue(ct)).Count(x => x.Status is "downloading" or "queued");
            }
            catch
            {
                // The queue is optional; the library totals still work without it.
            }

            _movies = new(movies, downloading);
            _moviesExpires = now + Lifetime;
            return _movies;
        }
        finally
        {
            _movieGate.Release();
        }
    }

    public async Task<ShowLibrarySummary> GetShowsAsync(CancellationToken ct = default)
    {
        DateTimeOffset now = time.GetUtcNow();
        if (_shows is not null && now < _showsExpires)
            return _shows;

        await _showGate.WaitAsync(ct);
        try
        {
            now = time.GetUtcNow();
            if (_shows is not null && now < _showsExpires)
                return _shows;

            List<TvSeries> series = await sonarr.GetAllSeries(ct);
            int downloading = 0;
            try
            {
                downloading = (await sonarr.GetQueue(ct)).Count(x => x.Status is "downloading" or "queued");
            }
            catch
            {
                // The queue is optional; the library totals still work without it.
            }

            _shows = new(series, downloading);
            _showsExpires = now + Lifetime;
            return _shows;
        }
        finally
        {
            _showGate.Release();
        }
    }
}
