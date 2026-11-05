namespace Stackarr.Music.Abstractions;

/// <summary>Read-only music library exposed by a music backend.</summary>
public interface IMusicLibrary
{
    Task<IReadOnlyList<Artist>> GetAllArtistsAsync(CancellationToken ct = default);

    Task<IReadOnlyList<Album>> GetAllAlbumsAsync(CancellationToken ct = default);

    Task<Album?> GetAlbumAsync(int albumId, CancellationToken ct = default);

    Task<Artist?> GetArtistAsync(int artistId, CancellationToken ct = default);

    Task<IReadOnlyList<Track>> GetTracksAsync(int albumId, CancellationToken ct = default);

    Task<Artist?> FindArtistByDeezerIdAsync(long deezerId, CancellationToken ct = default);

    Task<Artist?> FindArtistByNameAsync(string name, CancellationToken ct = default);

    Task<Album?> FindAlbumByTitleAsync(string? artist, string title, CancellationToken ct = default);

    Task<Album?> FindAlbumByDeezerIdAsync(long deezerId, CancellationToken ct = default);
}

/// <summary>Music library mutations a backend may support.</summary>
public interface IMusicLibraryWriter
{
    Task DeleteAlbumAsync(int albumId, CancellationToken ct = default);

    Task DeleteArtistAsync(int artistId, CancellationToken ct = default);

    Task SetAlbumMonitoredAsync(int albumId, bool monitored, CancellationToken ct = default);

    Task SetArtistMonitoredAsync(int artistId, bool monitored, CancellationToken ct = default);
}
