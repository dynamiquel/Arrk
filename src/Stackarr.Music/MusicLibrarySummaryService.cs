using Stackarr.Music.Models;
using Stackarr.Music.Services;

namespace Stackarr.Music;

/// <summary>Shared music-library snapshot behind the music tiles.</summary>
public sealed record MusicLibrarySummary(
    IReadOnlyList<MusicItem> Artists,
    IReadOnlyList<MusicItem> Albums,
    IReadOnlyList<MusicItem> Songs,
    long SizeBytes);

/// <summary>
/// Owns one short-lived music-library snapshot for the music tiles. The native library read is the
/// expensive part, so the album, song, download and disk tiles share it instead of each issuing the
/// same catalog queries.
/// </summary>
public sealed class MusicLibrarySummaryService(MusicCatalogService catalog, TimeProvider time)
{
    static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(15);

    readonly SemaphoreSlim _gate = new(1, 1);
    MusicLibrarySummary? _cached;
    DateTimeOffset _expires;

    public async Task<MusicLibrarySummary> GetAsync(CancellationToken ct = default)
    {
        DateTimeOffset now = time.GetUtcNow();
        if (_cached is not null && now < _expires)
            return _cached;

        await _gate.WaitAsync(ct);
        try
        {
            now = time.GetUtcNow();
            if (_cached is not null && now < _expires)
                return _cached;

            (List<MusicItem> artists, List<MusicItem> albums) = await catalog.GetLibraryViewAsync(ct);
            List<MusicItem> songs = await catalog.GetLibrarySongsAsync(ct);
            long size = await catalog.GetLibrarySizeBytesAsync(ct);
            _cached = new(artists, albums, songs, size);
            _expires = now + Lifetime;
            return _cached;
        }
        finally
        {
            _gate.Release();
        }
    }
}
