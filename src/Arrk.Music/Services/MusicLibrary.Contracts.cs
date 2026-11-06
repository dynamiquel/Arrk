using Arrk.Music.Abstractions;

namespace Arrk.Music.Services;

public partial class MusicLibrary
{
    async Task<IReadOnlyList<Artist>> IMusicLibrary.GetAllArtistsAsync(CancellationToken ct)
        => await GetAllArtistsAsync(ct);

    async Task<IReadOnlyList<Album>> IMusicLibrary.GetAllAlbumsAsync(CancellationToken ct)
        => await GetAllAlbumsAsync(ct);

    Task<Album?> IMusicLibrary.GetAlbumAsync(int albumId, CancellationToken ct)
        => GetAlbumAsync(albumId, ct);

    Task<Artist?> IMusicLibrary.GetArtistAsync(int artistId, CancellationToken ct)
        => GetArtistAsync(artistId, ct);

    async Task<IReadOnlyList<Track>> IMusicLibrary.GetTracksAsync(int albumId, CancellationToken ct)
        => await GetTracksAsync(albumId, ct);

    Task<Artist?> IMusicLibrary.FindArtistByDeezerIdAsync(long deezerId, CancellationToken ct)
        => FindArtistByDeezerIdAsync(deezerId, ct);

    Task<Artist?> IMusicLibrary.FindArtistByNameAsync(string name, CancellationToken ct)
        => FindArtistByNameAsync(name, ct);

    Task<Album?> IMusicLibrary.FindAlbumByTitleAsync(string? artist, string title, CancellationToken ct)
        => FindAlbumByTitleAsync(artist, title, ct);

    Task<Album?> IMusicLibrary.FindAlbumByDeezerIdAsync(long deezerId, CancellationToken ct)
        => FindAlbumByDeezerIdAsync(deezerId, ct);

    Task IMusicLibraryWriter.DeleteAlbumAsync(int albumId, CancellationToken ct)
        => DeleteAlbumAsync(albumId, ct);

    Task IMusicLibraryWriter.DeleteArtistAsync(int artistId, CancellationToken ct)
        => DeleteArtistAsync(artistId, ct);

    Task IMusicLibraryWriter.SetAlbumMonitoredAsync(int albumId, bool monitored, CancellationToken ct)
        => SetAlbumMonitoredAsync(albumId, monitored, ct);

    Task IMusicLibraryWriter.SetArtistMonitoredAsync(int artistId, bool monitored, CancellationToken ct)
        => SetArtistMonitoredAsync(artistId, monitored, ct);
}
